using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using ECommons.Automation;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.Utilities.LevelingGearShop;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// レベリング装備の自動購入。現在ジョブの装備(主道具/副道具/頭/胴/腕/脚/足)を指定の Lv 段階で
    /// 拠点のギル装備ベンダー(ゴッドギス)から買う。所持済みは買わず、アーマリーチェストの空き枠が足りなければ中止する。
    /// 流れ: 計画(BuildPlan) → 確認 → NPC へ移動 → 階層メニュー(Lv帯) → 店舗 → 購入(反映確認) → 次の店舗 … → 最強装備。
    /// メニューは文言で選び、文言が読めない/一致しない場合はゲームデータ上の並び順(位置)で選ぶ。
    /// 緊急停止: Stop ボタン / "/ice stop" / Esc キー(購入中のみ) → Abort()。
    /// </summary>
    internal static class Task_BuyLevelingGear
    {
        // 購入する装備Lvの段階。丁度の Lv の装備が無い段階は、その Lv 以下で最も高い Lv の装備を買う(例: Lv75 → Lv74/73)。
        // コスモレシピは製作者のレベルに応じて難易度が上がり、特に Lv90→91 で要求値が大きく跳ねるため、
        // ゴッドギスにある Lv91(il610)/Lv94(il650)/Lv97(il670) の段階を入れている。
        public static readonly int[] Steps = { 10, 20, 30, 40, 50, 52, 55, 60, 65, 70, 75, 80, 85, 90, 91, 94, 97 };
        public static int MinLevel => Steps[0];
        public static int MaxLevel => Steps[^1];
        public static string StepsText => string.Join("→", Steps);
        public const int KeepFreeSlots = 2; // 購入後にアーマリーチェストの各部位に残しておく空き枠

        // 購入/売却の対象部位。アクセサリ(耳・首・腕輪・指)もゴッドギスに Lv5〜100 まで揃っているので含める(ユーザー要望 2026-09-28)。
        public static readonly GearSlot[] TargetSlots =
        {
            GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet,
            GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.Ring,
        };

        /// <summary>部位ごとに必要な個数。指輪は左右に 2 つ着けるので 2。</summary>
        public static int NeedCount(GearSlot slot) => slot == GearSlot.Ring ? 2 : 1;

        public static bool IsJapanese => Svc.ClientState.ClientLanguage == ClientLanguage.Japanese;

        /// <summary>購入した品がどこに入ったか(アーマリーチェスト/かばん)。ゲーム側の設定や空き状況で変わる</summary>
        public enum Landing { Unknown, Armoury, Bags }

        public class PlanEntry
        {
            public ShopGearItem Item;
            public int StepLevel;
            public bool Bought;
            public bool Failed;
            public Landing LandedIn = Landing.Unknown;
        }

        public class PurchasePlan
        {
            public uint Job;
            public string JobName = "";
            public int JobLevel;                 // 計画時点のジョブLv
            public List<int> StepsUsed = new();  // 実際に使った段階(現在Lv → それより上の段階)
            public string StepsText => string.Join("→", StepsUsed.Select((s, i) => i == 0 && s == Math.Min(Math.Max(JobLevel, MinLevel), MaxLevel) && s != Steps[0] ? $"{s}(現在)" : s.ToString()));
            public uint NpcId;
            public List<PlanEntry> ToBuy = new();
            public int OwnedSkipped;
            public List<string> QuestLocked = new(); // 店舗に並ぶ条件のクエストが未達成で除外した品(名前)
            public long TotalGil;
            public long PlayerGil;
            public Dictionary<GearSlot, int> Shortage = new(); // 部位 → 不足枠数
            public string Error = "";
            public int BoughtCount;
            public long SpentGil;
            public bool Aborted;
            public bool CanBuy => string.IsNullOrEmpty(Error) && ToBuy.Count > 0 && Shortage.Count == 0 && PlayerGil >= TotalGil;
        }

        public static PurchasePlan Current { get; private set; }
        public static bool Running { get; private set; }

        /// <summary>現在ジョブの購入計画を作る(所持済み除外、部位別の空き枠不足、合計ギル)。</summary>
        public static PurchasePlan BuildPlan(uint job)
        {
            // ジョブ名はクライアント言語(日本語なら「木工師」等)で表示する
            string jobName = CosmicHelper.GetJobName(job);
            if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(job, out var cj) && !string.IsNullOrEmpty(cj.Name.ExtractText()))
                jobName = cj.Name.ExtractText();
            var plan = new PurchasePlan { Job = job, JobName = jobName };
            Current = plan;

            if (!CosmicHelper.CrafterJobList.Contains(job) && !CosmicHelper.GatheringJobList.Contains(job))
            {
                plan.Error = IsJapanese ? "クラフター/ギャザラーのジョブで実行してください" : "Switch to a crafter or gatherer job first";
                return plan;
            }
            plan.NpcId = CurrentVendorNpcId();
            if (plan.NpcId == 0)
            {
                plan.Error = IsJapanese ? "この惑星には装備ベンダーが登録されていません" : "No gear vendor is registered for this planet";
                return plan;
            }

            var data = Resolve(plan.NpcId);
            UpdateOwnership(data);
            var candidates = data.AllItems
                .Where(x => x.Jobs.Contains(job) && KindMatchesJob(x.Kind, job) && TargetSlots.Contains(x.Slot))
                .ToList();
            // 店舗に並ぶ条件のクエスト(例: フィッシャーギグ=潜水漁の解放)が未達成の品は、ゲーム側が店舗一覧から隠すので計画から外す。
            // 外さないと購入時に「店舗に品が無い」を繰り返して止まる(2026-09-30 漁師 Lv29 で発生)。
            foreach (var locked in candidates.Where(x => !MeetsQuestRequirement(x)).ToList())
            {
                candidates.Remove(locked);
                if (!plan.QuestLocked.Contains(locked.Name))
                    plan.QuestLocked.Add(locked.Name);
            }
            if (plan.QuestLocked.Count > 0)
                IceLogging.Info($"クエスト未達成のため店舗に並ばない品を計画から外しました: {string.Join(", ", plan.QuestLocked)}", "[Leveling Gear]");
            if (candidates.Count == 0)
            {
                plan.Error = IsJapanese ? "ベンダーの品揃えを取得できませんでした" : "Could not read the vendor's inventory";
                return plan;
            }

            // 現在のジョブLvを起点にする: 「今の Lv で装備できる最高の装備」を最初の段階とし、それより上の段階だけを買う。
            // (Lv47 なら Lv47 以下の最高Lv装備 → 50 → 52 → … → 95。既に超えている段階の装備は買わない)
            plan.JobLevel = Player.GetLevel((Job)job);
            int currentStep = Math.Min(Math.Max(plan.JobLevel, MinLevel), MaxLevel);
            plan.StepsUsed.Add(currentStep);
            plan.StepsUsed.AddRange(Steps.Where(s => s > currentStep));

            // 各段階・各部位で「そのLv以下で最高Lv」の装備を必要数(指輪は 2)。同じアイテムは1度だけ計画し、所持済みの分は買わない。
            var planned = new HashSet<uint>();
            foreach (int lv in plan.StepsUsed)
            {
                foreach (var slot in TargetSlots)
                {
                    var best = candidates
                        .Where(x => x.Slot == slot && x.LevelEquip <= lv)
                        .OrderByDescending(x => x.LevelEquip)
                        .ThenByDescending(x => x.ItemLevel)
                        .ThenBy(x => x.Price)
                        .FirstOrDefault();
                    if (best == null || !planned.Add(best.ItemId))
                        continue;
                    int need = NeedCount(slot);
                    int owned = Math.Min(OwnedCount(best), need);
                    plan.OwnedSkipped += owned;
                    // 足りない分だけ買う(指輪を 1 つだけ持っていれば 1 つ)。同じ品が 2 件並ぶが、購入は 1 件ずつ所持数の増加で確認するので重複しない
                    for (int n = owned; n < need; n++)
                    {
                        plan.ToBuy.Add(new PlanEntry { Item = best, StepLevel = lv });
                        plan.TotalGil += best.Price;
                    }
                }
            }

            // 購入後に各部位へ KeepFreeSlots 以上の空きが残らなければ不足扱い
            foreach (var slot in TargetSlots)
            {
                int need = plan.ToBuy.Count(e => e.Item.Slot == slot);
                if (need == 0) continue;
                int shortage = need + KeepFreeSlots - ArmouryFreeSlots(slot);
                if (shortage > 0)
                    plan.Shortage[slot] = shortage;
            }

            plan.PlayerGil = GetGil();
            return plan;
        }

        // ------------------------------------------------------------------ 実行 ----

        private static PurchasePlan _plan;
        private static DateTime _groupStart = DateTime.MinValue;
        private static DateTime _lastProgress = DateTime.MinValue;
        private static uint _verifyItem = 0;
        private static bool _verifyHq = false;
        private static int _verifyBefore = 0;
        private static int _verifyArmouryBefore = 0;   // 購入前のアーマリーチェスト(該当部位)の所持数。格納先の判定用
        private static int _verifyBagsBefore = 0;      // 購入前のかばんの所持数。格納先の判定用
        private static DateTime _verifyAt = DateTime.MinValue;
        private static int _retry = 0;
        // メニューの現在位置。-1=不明(閉じて NPC から辿り直す) / 0=NPC の最初のメニュー / 1=階層メニュー内 / 2=店舗が開くのを待つ
        private static int _menuDepth = -1;
        private static int _menuCloses = 0;
        private static string _lastMenuSig = "";
        private const double GroupTimeoutSeconds = 180;
        private const double VerifySeconds = 8;
        private const double NoProgressAbortSeconds = 120; // 購入が1点も進まないまま経過したら中止
        private const int MaxMenuCloses = 8;               // メニューを閉じた回数がこれを超えたら中止(同じメニューの反復対策)

        public static void Enqueue(PurchasePlan plan)
        {
            _plan = plan;
            Running = true;
            plan.BoughtCount = 0;
            plan.SpentGil = 0;
            plan.Aborted = false;
            foreach (var e in plan.ToBuy) { e.Bought = false; e.Failed = false; e.LandedIn = Landing.Unknown; }
            _verifyItem = 0; // 前回の中止時の確認待ちを持ち越さない
            _menuDepth = -1;
            _lastProgress = DateTime.Now;

            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の購入を開始します: {plan.JobName} {plan.ToBuy.Count} 点 / {plan.TotalGil:N0} ギル（中止: Stop ボタン / /ice stop / Esc）"
                : $"Buying leveling gear: {plan.JobName} {plan.ToBuy.Count} items / {plan.TotalGil:N0} gil (abort: Stop button / /ice stop / Esc)", "[I.C.E.]");

            P.TaskManager.Enqueue(() => { _returnStart = DateTime.Now; return true; });
            P.TaskManager.Enqueue(() => ReturnToHub(), "Leveling gear: Stellar Return to the hub", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => Task_Repair.Repair_PathTo(), "Leveling gear: walking to the vendor", Utils.TaskConfig);

            // 階層メニュー(Lv帯)→店舗の順にまとめて購入する
            var groups = plan.ToBuy
                .GroupBy(e => (e.Item.MenuName, e.Item.ShopId))
                .OrderBy(g => MenuOrder(g.Key.MenuName))
                .ThenBy(g => g.Key.ShopId)
                .ToList();
            TryGetCached(plan.NpcId, out var shopData);
            foreach (var g in groups)
            {
                var entries = g.ToList();
                string menu = g.Key.MenuName;
                string shopName = entries[0].Item.ShopName;
                int menuIndex = entries[0].Item.MenuIndex;
                int shopIndex = entries[0].Item.ShopIndex;
                // この店舗にシート上並ぶ全装備の ID。開いた店舗が目的の店舗か(品が隠されているだけか)を見分けるのに使う
                var shopItemIds = shopData?.Shops.FirstOrDefault(s => s.ShopId == g.Key.ShopId)?.Items.Select(x => x.ItemId).ToHashSet()
                                  ?? entries.Select(e => e.Item.ItemId).ToHashSet();
                // 無進捗タイマー(_lastProgress)は店舗ごとに起点し直す(帰還や徒歩の移動時間を「無進捗」に数えない)
                P.TaskManager.Enqueue(() => { _groupStart = DateTime.Now; _lastProgress = DateTime.Now; _retry = 0; _verifyItem = 0; _menuCloses = 0; _lastMenuSig = ""; return true; }, "Leveling gear: next shop");
                P.TaskManager.Enqueue(() => BuyGroup(menu, shopName, menuIndex, shopIndex, entries, shopItemIds), $"Leveling gear: {menu} / {shopName}", Utils.TaskConfig);
            }
            P.TaskManager.Enqueue(() => { _groupStart = DateTime.Now; return true; });
            P.TaskManager.Enqueue(() => CloseAllMenus(), "Leveling gear: closing menus", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => Finish(), "Leveling gear: finished");
        }

        private static DateTime _returnStart = DateTime.MinValue;
        private const double ReturnTimeoutSeconds = 90;

        /// <summary>帰還タイムアウトの起点を今にする(帰還タスクを積む直前に呼ぶ。前回中断時の値を引きずらない)。</summary>
        public static void ResetReturnStart() => _returnStart = DateTime.Now;

        /// <summary>
        /// 拠点から離れた場所(採取地など)に居るときはコスモデジョン(Stellar Return)で拠点へ戻る。拠点付近ならそのまま次へ。
        /// 購入/売却の両方で使う。Stellar Return が完全に無効化されている設定なら徒歩に任せる。
        /// </summary>
        public static unsafe bool? ReturnToHub()
        {
            string tag = "[Leveling Gear]";
            if (_returnStart == DateTime.MinValue)
                _returnStart = DateTime.Now;
            if (!CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var hub))
                return true; // 拠点座標が無い惑星 → 徒歩に任せる
            if (C.AvoidStellarReturn && !C.AvoidStellarReturnExceptHub)
            {
                IceLogging.Info("Stellar Return が無効化されているため徒歩でベンダーへ向かいます", tag);
                return true;
            }
            if (!Player.Available)
                return false; // 転移中
            if (Player.DistanceTo(hub) < C.HubReturn_Distance)
            {
                if (!PlayerHelper.IsScreenReady())
                    return false;
                _returnStart = DateTime.MinValue;
                return true;
            }
            if ((DateTime.Now - _returnStart).TotalSeconds > ReturnTimeoutSeconds)
            {
                IceLogging.Warning($"コスモデジョンで {ReturnTimeoutSeconds:F0} 秒以内に拠点へ戻れなかったため、徒歩でベンダーへ向かいます", tag);
                _returnStart = DateTime.MinValue;
                return true;
            }
            // COSMO MISSIONS ウィンドウが開いていると発動できないので先に閉じる
            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var wksMissionWin) && wksMissionWin.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear close WKSMission", 500))
                    GenericHandlers.FireCallback("WKSMission", true, -1);
                return false;
            }
            if (Player.Mounted)
            {
                if (EzThrottler.Throttle("LGear dismount", 1000)) Utils.Dismount();
                return false;
            }
            // 移動中(IsBusy に含まれる)だと発動できないので、navmesh が走っていれば止める
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
            {
                P.Navmesh.Stop();
                return false;
            }
            if (!Player.IsBusy && EzThrottler.Throttle("LGear stellar return", 3000))
            {
                IceLogging.Info($"拠点から {Player.DistanceTo(hub):F0}m 離れているため、コスモデジョンで拠点へ戻ります", tag);
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 26);
            }
            return false;
        }

        /// <summary>緊急停止。タスクを全て破棄し、開いている店舗/メニューを閉じる。Stop ボタン・/ice stop・Esc から呼ばれる。</summary>
        public static unsafe void Abort(string reason)
        {
            if (!Running && _plan == null)
                return;
            if (_plan != null) _plan.Aborted = true;
            Running = false;
            P.TaskManager.Abort();
            // 購入を送って反映を待っている最中に止めた場合、その品はサーバー側で購入済みで、少し遅れて所持品に入る。
            // かばんに入っても移せるよう記録しておく(届かなかった/アーマリーに入った場合は、後でかばんに無いことを確認して外す)
            if (_verifyItem != 0 && (DateTime.Now - _verifyAt).TotalSeconds <= VerifySeconds)
            {
                Task_GearStorage.NotePurchasedInBags(_verifyItem);
                IceLogging.Info($"中止時に購入の反映待ちだった品(ItemId {_verifyItem})は、あとで所持品に届く可能性があるため、かばんに入った場合の移動対象として記録しました", "[Leveling Gear]");
            }
            _verifyItem = 0;
            try
            {
                if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
                    ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                if (TryGetMenu(out _, out _, out var close))
                    close();
            }
            catch { }
            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の購入を中止しました（{reason}）: 購入済み {_plan?.BoughtCount ?? 0} 点 / {_plan?.SpentGil ?? 0:N0} ギル"
                : $"Leveling gear purchase aborted ({reason}): bought {_plan?.BoughtCount ?? 0} items / {_plan?.SpentGil ?? 0:N0} gil", "[I.C.E.]");
            // 中止時は移動タスクを積まない(Stop の直後に TaskManager が破棄されるため)。格納先だけ記録して案内する
            if (_plan != null)
                ReportLanding(_plan, aborted: true);
        }

        // メニュー名「職人用装備の購入（Lv21～）」などから Lv を取り出して並び順にする
        private static int MenuOrder(string menuName)
        {
            var m = Regex.Match(menuName ?? "", @"Lv\s*(\d+)", RegexOptions.IgnoreCase);
            return m.Success && int.TryParse(m.Groups[1].Value, out var lv) ? lv : int.MaxValue;
        }

        private static string Normalize(string s) => (s ?? "").Replace(" ", "").Replace("　", "").Trim();

        private static int FindEntry(List<string> texts, string name)
        {
            string n = Normalize(name);
            if (n.Length == 0) return -1;
            for (int i = 0; i < texts.Count; i++)
            {
                string t = Normalize(texts[i]);
                if (t.Length == 0) continue;
                if (t == n || t.Contains(n) || (n.Contains(t) && t.Length >= 4))
                    return i;
            }
            return -1;
        }

        // 開いている選択メニュー(SelectIconString / SelectString)の項目と選択/閉じる操作を取り出す
        private static unsafe bool TryGetMenu(out List<string> texts, out Action<int> select, out Action close)
        {
            texts = null; select = null; close = null;
            if (GenericHelpers.TryGetAddonMaster<SelectIconString>("SelectIconString", out var sis) && sis.IsAddonReady)
            {
                var entries = sis.Entries;
                texts = entries.Select(e => { try { return e.Text ?? ""; } catch { return ""; } }).ToList();
                select = i => entries[i].Select();
                close = () => ECommons.Automation.Callback.Fire(sis.Base, true, -1);
                return true;
            }
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
            {
                var entries = ss.Entries;
                texts = entries.Select(e => { try { return e.Text ?? ""; } catch { return ""; } }).ToList();
                select = i => entries[i].Select();
                close = () => ECommons.Automation.Callback.Fire(ss.Base, true, -1);
                return true;
            }
            return false;
        }

        private static unsafe int CountOwned(uint itemId, bool hq)
            => InventoryManager.Instance()->GetInventoryItemCount(itemId, hq, true, true);

        // 1店舗分の購入。毎tick 状況を見て「メニューを辿る/購入する/反映を待つ」を進める。
        private static unsafe bool? BuyGroup(string menu, string shopName, int menuIndex, int shopIndex, List<PlanEntry> entries, HashSet<uint> shopItemIds)
        {
            string tag = "[Leveling Gear]";
            if (_plan == null || _plan.Aborted || !Running)
                return true;

            // Esc キーで緊急停止(ゲームがメニューを閉じるのと同時に購入も止める)
            if (Svc.KeyState[VirtualKey.ESCAPE])
            {
                Abort("Esc");
                return true;
            }
            if (!Player.Available)
                return false;

            var next = entries.FirstOrDefault(e => !e.Bought && !e.Failed);
            if (next == null)
                return true;

            // 進捗の無い状態が続いたら中止(同じメニューを反復するなどの異常対策)
            if ((DateTime.Now - _lastProgress).TotalSeconds > NoProgressAbortSeconds)
            {
                Abort(IsJapanese ? $"{NoProgressAbortSeconds:F0}秒以上購入が進まない" : $"no progress for {NoProgressAbortSeconds:F0}s");
                return true;
            }
            if (_menuCloses > MaxMenuCloses)
            {
                IceLogging.Error($"メニューを {_menuCloses} 回閉じても目的の店舗に辿り着けません。最後のメニュー: [{_lastMenuSig}]", tag);
                Abort(IsJapanese ? "メニューを辿れない" : "could not navigate the vendor menu");
                return true;
            }
            if ((DateTime.Now - _groupStart).TotalSeconds > GroupTimeoutSeconds)
            {
                IceLogging.Error($"{menu} / {shopName} の購入が {GroupTimeoutSeconds:F0} 秒以内に終わらないため、この店舗を飛ばします", tag);
                foreach (var e in entries) if (!e.Bought) e.Failed = true;
                return true;
            }

            // 直前の購入の反映待ち
            if (_verifyItem != 0)
            {
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yn) && yn.IsAddonReady)
                {
                    if (EzThrottler.Throttle("LGear yes", 300)) yn.Yes();
                    return false;
                }
                if (CountOwned(_verifyItem, _verifyHq) > _verifyBefore)
                {
                    next.Bought = true;
                    _plan.BoughtCount++;
                    _plan.SpentGil += next.Item.Price;
                    _lastProgress = DateTime.Now;
                    // どこに入ったか(アーマリーチェスト/かばん)を所持数の増え方で判定する。ゲームは空きがあれば通常アーマリーへ入れるが、
                    // 設定や状況でかばん(所持品)に入ることがある(実機 2026-09-27 23:59: 空きがあるのに 13 点が「所持品に入りました」)
                    int armouryDelta = CountInArmoury(next.Item.ItemId, next.Item.Slot) - _verifyArmouryBefore;
                    int bagsDelta = CountInBags(next.Item.ItemId) - _verifyBagsBefore;
                    next.LandedIn = armouryDelta > 0 ? Landing.Armoury : bagsDelta > 0 ? Landing.Bags : Landing.Unknown;
                    // かばんに入った(または判定できなかった)品は、移し終えるまで記録しておく(中止しても最強装備の前などで移せるように)
                    if (next.LandedIn != Landing.Armoury)
                        Task_GearStorage.NotePurchasedInBags(next.Item.ItemId);
                    string landing = next.LandedIn switch
                    {
                        Landing.Armoury => "アーマリーチェスト",
                        Landing.Bags => "かばん(所持品)",
                        _ => $"格納先不明(アーマリー増分 {armouryDelta} / かばん増分 {bagsDelta})",
                    };
                    IceLogging.Info($"購入完了: {next.Item.Name} ({next.Item.Price:N0}g) → {landing} [{_plan.BoughtCount}/{_plan.ToBuy.Count}]", tag);
                    _verifyItem = 0;
                    _retry = 0;
                    return false;
                }
                if ((DateTime.Now - _verifyAt).TotalSeconds > VerifySeconds)
                {
                    _verifyItem = 0;
                    _retry++;
                    if (_retry >= 2)
                    {
                        IceLogging.Error($"{next.Item.Name} を購入できませんでした(2回試行)。飛ばします", tag);
                        next.Failed = true;
                        _retry = 0;
                    }
                }
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear yes", 300)) yesno.Yes();
                return false;
            }

            // 店舗が開いている
            if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
            {
                var items = shop.ShopItems;
                int idx = Array.FindIndex(items, x => x.ItemId == next.Item.ItemId);
                if (idx < 0)
                {
                    // 開いている店舗が目的の店舗(シート上の品揃えと大半が一致)なのに品が無い
                    // → その品はゲーム側の条件(未達成クエスト等)で隠されている。飛ばして次の品へ進む(辿り直しても出てこない)
                    int matched = items.Count(x => shopItemIds.Contains(x.ItemId));
                    if (items.Length > 0 && matched >= Math.Max(1, items.Length / 2))
                    {
                        IceLogging.Warning($"{next.Item.Name} は目的の店舗({shopName})に並んでいません(条件未達成で非表示の可能性)。飛ばします [一致 {matched}/{items.Length} 件]", tag);
                        next.Failed = true;
                        _lastProgress = DateTime.Now;
                        return false;
                    }
                    // 目的の店舗ではない → 閉じて NPC から辿り直す
                    if (EzThrottler.Throttle("LGear close shop", 1500))
                    {
                        IceLogging.Info($"開いている店舗に {next.Item.Name} が無いので閉じて辿り直します(品目 {items.Length} 件、目的の店舗との一致 {matched} 件)", tag);
                        ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                        _menuDepth = -1;
                        _menuCloses++;
                    }
                    return false;
                }
                _menuDepth = 2;
                if (GetGil() < next.Item.Price)
                {
                    IceLogging.ChatError(IsJapanese
                        ? $"所持ギルが足りないため購入を中止しました(次: {next.Item.Name} {next.Item.Price:N0}ギル)"
                        : $"Not enough gil, purchase aborted (next: {next.Item.Name} {next.Item.Price:N0} gil)", "[I.C.E.]");
                    _plan.Aborted = true;
                    ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                    return true;
                }
                if (EzThrottler.Throttle("LGear buy", 1500))
                {
                    _verifyItem = next.Item.ItemId;
                    _verifyHq = next.Item.IsHQ;
                    _verifyBefore = CountOwned(_verifyItem, _verifyHq);
                    _verifyArmouryBefore = CountInArmoury(next.Item.ItemId, next.Item.Slot);
                    _verifyBagsBefore = CountInBags(next.Item.ItemId);
                    _verifyAt = DateTime.Now;
                    IceLogging.Info($"購入: {next.Item.Name} Lv{next.Item.LevelEquip} {next.Item.Price:N0}g (index {idx})", tag);
                    items[idx].Select(1);
                }
                return false;
            }

            // 選択メニュー
            if (TryGetMenu(out var texts, out var select, out var close))
            {
                string sig = string.Join(" | ", texts);
                if (sig != _lastMenuSig)
                {
                    _lastMenuSig = sig;
                    IceLogging.Info($"メニュー(depth={_menuDepth}, {texts.Count}項目): [{sig}]", tag);
                }

                if (_menuDepth < 0)
                {
                    // どの階層か分からない(店舗を閉じた直後など) → 閉じて NPC から辿り直す
                    if (EzThrottler.Throttle("LGear close menu", 1000)) { close(); _menuCloses++; }
                    return false;
                }

                // 文言で選ぶ: 店舗名(階層メニュー内) → 階層メニュー名(最初のメニュー)
                int i = FindEntry(texts, shopName);
                int nextDepth = 2;
                if (i < 0 && !string.IsNullOrEmpty(menu))
                {
                    i = FindEntry(texts, menu);
                    nextDepth = 1;
                }
                // 文言が読めない/一致しない → ゲームデータ上の並び順(位置)で選ぶ
                if (i < 0)
                {
                    if (_menuDepth == 0 && menuIndex >= 0 && menuIndex < texts.Count)
                    {
                        i = menuIndex;
                        nextDepth = shopIndex >= 0 ? 1 : 2;
                    }
                    else if (_menuDepth == 1 && shopIndex >= 0 && shopIndex < texts.Count)
                    {
                        i = shopIndex;
                        nextDepth = 2;
                    }
                }
                if (i >= 0)
                {
                    if (EzThrottler.Throttle("LGear select", 700))
                    {
                        IceLogging.Info($"メニュー選択: #{i} '{texts[i]}' (depth {_menuDepth}→{nextDepth})", tag);
                        select(i);
                        _menuDepth = nextDepth;
                    }
                    return false;
                }
                if (EzThrottler.Throttle("LGear close menu", 1500))
                {
                    IceLogging.Info($"目的の項目が無いメニューを閉じます(depth={_menuDepth}): [{sig}]", tag);
                    close();
                    _menuCloses++;
                    _menuDepth = -1;
                }
                return false;
            }

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear talk", 100)) talk.Click();
                return false;
            }

            // 何も開いていない → NPC に話しかける(最初のメニューから辿る)
            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out var npc))
            {
                if (Player.DistanceTo(npc.Location_Npc) > 6f)
                {
                    Task_NavmeshMove.Task_NavTo(npc.Location_Circle, false, 3f, npcLoc: npc.Location_Npc);
                    return false;
                }
                if (Player.Mounted)
                {
                    Utils.Dismount();
                    return false;
                }
                if (!GenericHelpers.IsOccupied() && Utils.TryGetObjectByDataId(npc.NpcId, out var obj) && obj != null
                    && EzThrottler.Throttle("LGear interact", 1200))
                {
                    _menuDepth = 0;
                    Utils.TargetgameObject(obj);
                    Utils.InteractWithObject(obj);
                }
            }
            else
            {
                IceLogging.Error("装備ベンダーが登録されていない惑星です", tag);
                _plan.Aborted = true;
                return true;
            }
            return false;
        }

        private static unsafe bool? CloseAllMenus()
        {
            if (_plan == null || _plan.Aborted || !Running)
                return true;
            if ((DateTime.Now - _groupStart).TotalSeconds > 10)
                return true;
            if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear close shop", 800)) ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                return false;
            }
            if (TryGetMenu(out _, out _, out var close))
            {
                if (EzThrottler.Throttle("LGear close menu", 800)) close();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear talk", 100)) talk.Click();
                return false;
            }
            return !GenericHelpers.IsOccupied();
        }

        // 途中で打ち切ったとき(所持ギル不足など)に、店舗・メニュー・会話を閉じる(最大 10 秒。CloseAllMenus は打ち切り時は何もしないため)
        private static unsafe bool? CloseMenusAfterStop()
        {
            if ((DateTime.Now - _groupStart).TotalSeconds > 10)
                return true;
            if (GenericHelpers.TryGetAddonMaster<Shop>("Shop", out var shop) && shop.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear close shop", 800)) ECommons.Automation.Callback.Fire(shop.Base, true, -1);
                return false;
            }
            if (TryGetMenu(out _, out _, out var close))
            {
                if (EzThrottler.Throttle("LGear close menu", 800)) close();
                return false;
            }
            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("LGear talk", 100)) talk.Click();
                return false;
            }
            return !GenericHelpers.IsOccupied();
        }

        private static bool? Finish()
        {
            if (_plan == null)
                return true;
            bool wasRunning = Running;
            Running = false;
            if (!wasRunning)
                return true; // 緊急停止(Abort)済み。タスクは破棄されている
            if (_plan.Aborted)
            {
                // 所持ギル不足などで途中で打ち切った(緊急停止ではないのでタスクは生きている)。
                // 買えた分の格納先を記録し、かばんに入った物(以前の中止で残った購入品を含む)はアーマリーチェストへ移す
                if (_plan.BoughtCount > 0 || Task_GearStorage.PendingFromPurchase.Count > 0)
                {
                    ReportLanding(_plan, aborted: false);
                    // 打ち切りの経路では CloseAllMenus が何もしないので、店舗のメニューが残っているとアイテムを動かせない。先に閉じる
                    P.TaskManager.Enqueue(() => { _groupStart = DateTime.Now; return true; });
                    P.TaskManager.Enqueue(() => CloseMenusAfterStop(), "Leveling gear: closing menus before moving items", Utils.TaskConfig);
                    Task_GearStorage.EnqueuePurchased(_plan.Job,
                        _plan.ToBuy.Where(e => e.Bought).Select(e => e.Item.ItemId).ToHashSet(),
                        _plan.ToBuy.Count(e => e.Bought && e.LandedIn == Landing.Bags));
                }
                return true;
            }

            var notBought = _plan.ToBuy.Where(e => e.Failed || !e.Bought).Select(e => e.Item.Name).Distinct().ToList();
            int failed = _plan.ToBuy.Count(e => e.Failed || !e.Bought);
            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の購入が終わりました: {_plan.BoughtCount} 点 / {_plan.SpentGil:N0} ギル" + (failed > 0 ? $"（未購入 {failed} 点: {string.Join("、", notBought)}）" : "")
                : $"Leveling gear purchase finished: {_plan.BoughtCount} items / {_plan.SpentGil:N0} gil" + (failed > 0 ? $" ({failed} not bought: {string.Join(", ", notBought)})" : ""), "[I.C.E.]");

            // 今回 1 点も買えなくても、以前の中止でかばんに残った購入品があれば移す(中止時の案内「次の購入完了時に移す」を守る)
            if (_plan.BoughtCount > 0 || Task_GearStorage.PendingFromPurchase.Count > 0)
            {
                ReportLanding(_plan, aborted: false);
                // かばんに入った購入品(この起動中に中止などで残った分を含む)をアーマリーチェストへ移す。
                // Stylist / おすすめ装備はかばんの中を候補にしないため、最強装備の前に行う。
                // かばんに 1 点も無ければ(ゲーム設定で全部アーマリーに入った場合)、その旨をログに残して何もしない
                Task_GearStorage.EnqueuePurchased(_plan.Job,
                    _plan.ToBuy.Where(e => e.Bought).Select(e => e.Item.ItemId).ToHashSet(),
                    _plan.ToBuy.Count(e => e.Bought && e.LandedIn == Landing.Bags));
            }

            if (C.LevelingGear_AutoEquipBest && _plan.BoughtCount > 0)
                Task_RelicTurnin.EnqueueEquipBestGear();
            return true;
        }

        /// <summary>
        /// 購入品の格納先(アーマリーチェスト/かばん)をまとめてログに残す。かばんに入った品があればチャットにも出す
        /// (中止時は移動を行わないので、次回の購入完了時か最強装備の前に移すことを案内する)。
        /// </summary>
        private static void ReportLanding(PurchasePlan plan, bool aborted)
        {
            string tag = "[Leveling Gear]";
            var bought = plan.ToBuy.Where(e => e.Bought).ToList();
            if (bought.Count == 0)
                return;
            var bags = bought.Where(e => e.LandedIn == Landing.Bags).ToList();
            int armoury = bought.Count(e => e.LandedIn == Landing.Armoury);
            int unknown = bought.Count(e => e.LandedIn == Landing.Unknown);
            IceLogging.Info($"購入品の格納先: アーマリーチェスト {armoury} 点 / かばん {bags.Count} 点 / 不明 {unknown} 点(計 {bought.Count} 点)"
                + (bags.Count > 0 ? $" かばん: {string.Join(", ", bags.Select(e => e.Item.Name))}" : ""), tag);
            if (bags.Count == 0)
            {
                // ゲーム側の設定や空き状況により、購入品はすべてアーマリーチェストに入った(かばんへの格納は検知されなかった)
                IceLogging.Info(unknown == 0
                    ? "購入品はすべてアーマリーチェストに入りました(かばんへの格納は検知されませんでした)"
                    : aborted
                        ? $"かばんへの格納は検知されませんでした(格納先が判定できなかった品が {unknown} 点あります。中止したため今は移動せず、最強装備の前か次の購入完了時にかばんを確認します)"
                        : $"かばんへの格納は検知されませんでした(格納先が判定できなかった品が {unknown} 点あります。移動処理でかばんを走査して確認します)", tag);
                return;
            }
            if (!aborted)
            {
                IceLogging.ChatInfo(IsJapanese
                    ? $"購入品のうち {bags.Count} 点はかばん(所持品)に入ったため、アーマリーチェストへ移します"
                    : $"{bags.Count} purchased item(s) went into your bags, so ICE will move them to the armoury chest", "[I.C.E.]");
                return;
            }
            // 中止時は移動タスクを積めない。かばんに入った品は記録してあるので、最強装備の前(自動の最強装備が有効な場合)か次の購入完了時に移す
            IceLogging.ChatInfo(IsJapanese
                ? (C.LevelingGear_AutoEquipBest
                    ? $"購入品のうち {bags.Count} 点はかばん(所持品)に入っています。中止したため今は移さず、次の最強装備の前か次の購入完了時にアーマリーチェストへ移します"
                    : $"購入品のうち {bags.Count} 点はかばん(所持品)に入っています。中止したため今は移しません。次にレベリング装備を購入したときに移しますが、それまでに使う場合は手動でアーマリーチェストへ移してください")
                : (C.LevelingGear_AutoEquipBest
                    ? $"{bags.Count} purchased item(s) are in your bags. Since the purchase was stopped, ICE will move them to the armoury chest before the next best-gear equip or after the next purchase"
                    : $"{bags.Count} purchased item(s) are in your bags. Since the purchase was stopped, ICE will move them after the next leveling gear purchase; move them to the armoury chest manually if you need them sooner"), "[I.C.E.]");
        }
    }
}
