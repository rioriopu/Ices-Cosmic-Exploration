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
        public static readonly int[] Steps = { 10, 20, 30, 40, 50, 52, 55, 60, 65, 70, 75, 80, 85, 90, 95 };
        public static int MinLevel => Steps[0];
        public static int MaxLevel => Steps[^1];
        public static string StepsText => string.Join("→", Steps);
        public const int KeepFreeSlots = 2; // 購入後にアーマリーチェストの各部位に残しておく空き枠

        public static readonly GearSlot[] TargetSlots =
        {
            GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet,
        };

        public static bool IsJapanese => Svc.ClientState.ClientLanguage == ClientLanguage.Japanese;

        public class PlanEntry
        {
            public ShopGearItem Item;
            public int StepLevel;
            public bool Bought;
            public bool Failed;
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

            // 各段階・各部位で「そのLv以下で最高Lv」の装備を1点。同じアイテムは1度だけ、所持済みは買わない。
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
                    if (IsOwned(best))
                    {
                        plan.OwnedSkipped++;
                        continue;
                    }
                    plan.ToBuy.Add(new PlanEntry { Item = best, StepLevel = lv });
                    plan.TotalGil += best.Price;
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
            foreach (var e in plan.ToBuy) { e.Bought = false; e.Failed = false; }
            _menuDepth = -1;
            _lastProgress = DateTime.Now;

            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の購入を開始します: {plan.JobName} {plan.ToBuy.Count} 点 / {plan.TotalGil:N0} ギル（中止: Stop ボタン / /ice stop / Esc）"
                : $"Buying leveling gear: {plan.JobName} {plan.ToBuy.Count} items / {plan.TotalGil:N0} gil (abort: Stop button / /ice stop / Esc)", "[I.C.E.]");

            P.TaskManager.Enqueue(() => Task_Repair.Repair_PathTo(), "Leveling gear: walking to the vendor", Utils.TaskConfig);

            // 階層メニュー(Lv帯)→店舗の順にまとめて購入する
            var groups = plan.ToBuy
                .GroupBy(e => (e.Item.MenuName, e.Item.ShopId))
                .OrderBy(g => MenuOrder(g.Key.MenuName))
                .ThenBy(g => g.Key.ShopId)
                .ToList();
            foreach (var g in groups)
            {
                var entries = g.ToList();
                string menu = g.Key.MenuName;
                string shopName = entries[0].Item.ShopName;
                int menuIndex = entries[0].Item.MenuIndex;
                int shopIndex = entries[0].Item.ShopIndex;
                P.TaskManager.Enqueue(() => { _groupStart = DateTime.Now; _retry = 0; _verifyItem = 0; _menuCloses = 0; _lastMenuSig = ""; return true; }, "Leveling gear: next shop");
                P.TaskManager.Enqueue(() => BuyGroup(menu, shopName, menuIndex, shopIndex, entries), $"Leveling gear: {menu} / {shopName}", Utils.TaskConfig);
            }
            P.TaskManager.Enqueue(() => { _groupStart = DateTime.Now; return true; });
            P.TaskManager.Enqueue(() => CloseAllMenus(), "Leveling gear: closing menus", Utils.TaskConfig);
            P.TaskManager.Enqueue(() => Finish(), "Leveling gear: finished");
        }

        /// <summary>緊急停止。タスクを全て破棄し、開いている店舗/メニューを閉じる。Stop ボタン・/ice stop・Esc から呼ばれる。</summary>
        public static unsafe void Abort(string reason)
        {
            if (!Running && _plan == null)
                return;
            if (_plan != null) _plan.Aborted = true;
            Running = false;
            P.TaskManager.Abort();
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
        private static unsafe bool? BuyGroup(string menu, string shopName, int menuIndex, int shopIndex, List<PlanEntry> entries)
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
                    IceLogging.Info($"購入完了: {next.Item.Name} ({next.Item.Price:N0}g) [{_plan.BoughtCount}/{_plan.ToBuy.Count}]", tag);
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
                    // 目的の店舗ではない → 閉じて NPC から辿り直す
                    if (EzThrottler.Throttle("LGear close shop", 1500))
                    {
                        IceLogging.Info($"開いている店舗に {next.Item.Name} が無いので閉じて辿り直します(品目 {items.Length} 件)", tag);
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

        private static bool? Finish()
        {
            if (_plan == null)
                return true;
            bool wasRunning = Running;
            Running = false;
            if (!wasRunning || _plan.Aborted)
                return true;

            int failed = _plan.ToBuy.Count(e => e.Failed || !e.Bought);
            IceLogging.ChatInfo(IsJapanese
                ? $"レベリング装備の購入が終わりました: {_plan.BoughtCount} 点 / {_plan.SpentGil:N0} ギル" + (failed > 0 ? $"（未購入 {failed} 点）" : "")
                : $"Leveling gear purchase finished: {_plan.BoughtCount} items / {_plan.SpentGil:N0} gil" + (failed > 0 ? $" ({failed} not bought)" : ""), "[I.C.E.]");

            if (C.LevelingGear_AutoEquipBest && _plan.BoughtCount > 0)
                Task_RelicTurnin.EnqueueEquipBestGear();
            return true;
        }
    }
}
