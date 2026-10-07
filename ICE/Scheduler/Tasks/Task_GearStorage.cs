using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ICE.Utilities.LevelingGearShop;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// かばん(Inventory1〜4)に入っている装備をアーマリーチェストの該当部位へ移す。対象は次のとおり:
    /// ・指定ジョブの主道具/副道具(レリックの強化(納品)で受け取った新しい主道具はかばんに入る。実機 2026-09-28)
    /// ・レベリング装備の購入でかばんに入った品(頭〜足・耳・首・腕輪・指の全部位、指輪は 2 個とも)。
    ///   今回の購入分に加え、この起動中に中止などで移せずに残った購入品(PendingFromPurchase)も対象。
    ///   ユーザーが自分でかばんに置いた装備(空き枠を作るために退避した旧装備など)は動かさない。
    /// ゲームは購入した装備を通常アーマリーチェストへ入れるが、設定や状況によってかばん(所持品)に入ることがある
    /// (実機 2026-09-27 23:59: アーマリーに空きがあるのに 13 点が「所持品に入りました」)。
    /// Stylist(/stylist crafter)やゲームの「おすすめ装備」はアーマリーチェストと装備中の物しか候補にしないため、
    /// かばんに残っていると「最強装備」で見つけられない。
    /// 移動は 1 tick に 1 件だけ発行し、次の tick 以降で反映を確認してから次の品へ進む(未反映のまま連続発行すると同じ空き枠を取り合う)。
    /// </summary>
    internal static class Task_GearStorage
    {
        private static bool IsJapanese => Task_BuyLevelingGear.IsJapanese;

        private const double PendingTimeoutSeconds = 3;  // 1 件の移動の反映待ち上限
        private const double TotalTimeoutSeconds = 20;   // タスク全体の上限(計画を作ってから。件数が多いときは 1 件 2 秒で延ばす)
        private const double BusyWaitMaxSeconds = 60;    // 会話/演出が終わるのを待つ上限(超えたら移動を諦める)
        private const double RescanDelayMs = 750;        // 納品直後に道具が見えないときの再走査までの待ち
        private const int NoFreeSlotChatThrottleMs = 5 * 60 * 1000; // 「空きが無い」のチャット通知は数分に 1 回

        private static readonly InventoryType[] BagTypes =
        {
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        };

        private class MoveEntry
        {
            public uint ItemId;
            public string Name = "";
            public InventoryType SrcContainer;
            public int SrcSlot;
            public GearSlot Slot;
            public bool Purchased;                // 今回のレベリング装備の購入で買った品か
            public bool IsTool;                   // 指定ジョブの主道具/副道具として拾った品か(チャットの文言の出し分けに使う)
            public bool Moved;
            public bool Failed;
        }

        /// <summary>1 回のタスク実行分の進行状態。タスク関数ごとに新しく作るので、途中で Abort されても次回に古い状態が残らない。</summary>
        private class MoveState
        {
            public uint Job;
            public bool AfterUpgrade;             // レリック強化(納品)直後の呼び出しか(文言と再走査の有無が変わる)
            public bool Purchase;                 // レベリング装備の購入後の呼び出しか(購入品とレベリング装備だけを移し、文言も変わる)
            public HashSet<uint> PurchasedIds = new();  // 今回の購入品(優先して移す)
            public int ExpectedInBags;            // 購入時に「かばんに入った」と判定した点数(見つからなければ警告する)
            public HashSet<uint> GearIds;         // 部位を問わず移す ItemId(今回の購入品+かばんに残っている購入品)。null = まだ作っていない
            public DateTime FirstCallAt = DateTime.MinValue;
            public List<MoveEntry> Plan;          // null = まだ計画していない(ゲームが busy の間は作らない)
            public bool Rescanned;                // 納品直後の再走査を済ませたか
            public DateTime FirstEmptyAt = DateTime.MinValue;
            public MoveEntry Pending;             // MoveItemSlot を発行して反映待ちの品
            public InventoryType PendingDst;
            public int PendingDstSlot;
            public DateTime PendingAt;
            public DateTime Start;
            public HashSet<GearSlot> NoFreeSlotWarned = new();               // 部位ごとに 1 回だけ警告する
            public HashSet<(InventoryType, int)> UsedDst = new();           // このタスクで移動先に使った枡(反映前に同じ枡を選ばない)
            public MoveState(uint job, bool afterUpgrade) { Job = job; AfterUpgrade = afterUpgrade; }
        }

        /// <summary>
        /// 指定ジョブの道具をかばんからアーマリーチェストへ移すタスク関数を作る(P.TaskManager にそのまま渡せる)。
        /// afterUpgrade=true はレリック強化(納品)直後の呼び出し。所持品の反映が遅れて新しい道具が見えないことがあるので、
        /// 見つからなければ少し待って 1 回だけ再走査し、チャットの文言も「強化した道具」にする。
        /// </summary>
        public static Func<bool?> CreateTask(uint job, bool afterUpgrade = false)
        {
            var state = new MoveState(job, afterUpgrade);
            return () => Step(state);
        }

        /// <summary>
        /// レベリング装備の購入でかばんに入った(または格納先を判定できなかった)品の ItemId。移し終えたら外す(この起動の間だけ保持)。
        /// 購入を Stop/Esc で中止したときは移動タスクを積めないので、ここに残して最強装備の前(Enqueue)や次の購入後に移す。
        /// </summary>
        internal static readonly HashSet<uint> PendingFromPurchase = new();

        /// <summary>購入した品がかばんに入った(または格納先が分からない)ことを記録する(Task_BuyLevelingGear から呼ぶ)</summary>
        public static void NotePurchasedInBags(uint itemId) => PendingFromPurchase.Add(itemId);

        /// <summary>指定ジョブの道具(主道具/副道具)と、かばんに残っている購入品の移動タスクを末尾に積む。</summary>
        public static void Enqueue(uint job, bool afterUpgrade = false)
            => P.TaskManager.Enqueue(CreateTask(job, afterUpgrade), $"Moving {CosmicHelper.GetJobName(job)} tools from bags to the armoury", Utils.TaskConfig);

        /// <summary>
        /// レベリング装備の購入後の移動タスクを末尾に積む。今回の購入品(purchasedIds)と、この起動中に移せずに残った購入品が対象。
        /// かばんに 1 点も無ければ(ゲームが全部アーマリーチェストに入れた場合)、その旨をログに残して何もしない。
        /// expectedInBags は購入時に「かばんに入った」と判定した点数。
        /// </summary>
        public static void EnqueuePurchased(uint job, HashSet<uint> purchasedIds, int expectedInBags)
        {
            var state = new MoveState(job, false)
            {
                Purchase = true,
                PurchasedIds = purchasedIds ?? new HashSet<uint>(),
                ExpectedInBags = expectedInBags,
            };
            P.TaskManager.Enqueue(() => Step(state), "Moving purchased leveling gear from bags to the armoury", Utils.TaskConfig);
        }

        // かばんを走査して、移す装備を列挙する。
        // 道具モード: 指定ジョブが装備できる主道具/副道具 + かばんに残っている購入品(全部位)。
        //   レリック強化直後(AfterUpgrade)は従来どおり道具だけ(新しい道具の反映待ちの再走査を、他の品で妨げないため)。
        // 購入モード: 今回の購入品 + かばんに残っている購入品(全部位)。今回の購入品を先に並べる(アーマリーの空きが足りないときに優先するため)。
        // 部位とジョブの判定は LevelingGearShop(Lumina の EquipSlotCategory / ClassJobCategory)を再利用する。
        private static unsafe List<MoveEntry> BuildPlan(MoveState s)
        {
            var list = new List<MoveEntry>();
            var inv = InventoryManager.Instance();
            if (inv == null) return list;
            var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet == null) return list;
            if (s.GearIds == null)
            {
                s.GearIds = s.AfterUpgrade ? new HashSet<uint>() : new HashSet<uint>(PendingFromPurchase);
                s.GearIds.UnionWith(s.PurchasedIds);
            }

            foreach (var type in BagTypes)
            {
                var container = inv->GetInventoryContainer(type);
                if (container == null) continue;
                for (int i = 0; i < container->Size; i++)
                {
                    var it = container->GetInventorySlot(i);
                    if (it == null || it->ItemId == 0) continue;
                    if (!itemSheet.TryGetRow(it->ItemId, out var row)) continue;
                    var slot = ToSlot(row.EquipSlotCategory.ValueNullable);
                    if (ArmouryOf(slot) == InventoryType.Invalid) continue;
                    bool levelingGear = s.GearIds.Contains(it->ItemId);
                    bool tool = (slot == GearSlot.MainHand || slot == GearSlot.OffHand) && CosmicJobs(row.ClassJobCategory.ValueNullable).Contains(s.Job);
                    if (s.Purchase ? !levelingGear : !(tool || levelingGear)) continue;
                    list.Add(new MoveEntry
                    {
                        ItemId = it->ItemId,
                        Name = row.Name.ExtractText(),
                        SrcContainer = type,
                        SrcSlot = i,
                        Slot = slot,
                        Purchased = s.PurchasedIds.Contains(it->ItemId),
                        IsTool = tool && !levelingGear,
                    });
                }
            }
            // 今回の購入品を先に(安定ソート)
            return list.OrderByDescending(e => e.Purchased).ToList();
        }

        // 移動先コンテナの最初の空き枠(このタスクで既に移動先に使った枡は除く)。無ければ -1。
        private static unsafe int FindFreeSlot(InventoryManager* inv, InventoryType type, MoveState s)
        {
            var container = inv->GetInventoryContainer(type);
            if (container == null) return -1;
            for (int i = 0; i < container->Size; i++)
            {
                if (s.UsedDst.Contains((type, i))) continue;
                var it = container->GetInventorySlot(i);
                if (it == null || it->ItemId == 0) return i;
            }
            return -1;
        }

        // 元スロットに今もその品があるか
        private static unsafe bool StillInSource(InventoryManager* inv, MoveEntry e)
        {
            var container = inv->GetInventoryContainer(e.SrcContainer);
            if (container == null) return false;
            var it = container->GetInventorySlot(e.SrcSlot);
            return it != null && it->ItemId == e.ItemId;
        }

        private enum MoveResult { Waiting, Done, Replaced }

        // 移動の反映状況。移動先の枡にその品があれば完了。元スロットが空になっただけでは(反映途中の可能性があるので)まだ待つ。
        // 元スロットが別の品に変わっていれば、手動操作などで計画が崩れたとみなす。
        private static unsafe MoveResult CheckMove(InventoryManager* inv, MoveState s)
        {
            var dst = inv->GetInventoryContainer(s.PendingDst);
            if (dst != null)
            {
                var it = dst->GetInventorySlot(s.PendingDstSlot);
                if (it != null && it->ItemId == s.Pending.ItemId) return MoveResult.Done;
            }
            var src = inv->GetInventoryContainer(s.Pending.SrcContainer);
            if (src != null)
            {
                var it = src->GetInventorySlot(s.Pending.SrcSlot);
                if (it != null && it->ItemId != 0 && it->ItemId != s.Pending.ItemId) return MoveResult.Replaced;
            }
            return MoveResult.Waiting;
        }

        private static string SlotName(GearSlot slot) => IsJapanese ? SlotNameJp(slot) : slot switch
        {
            GearSlot.MainHand => "main hand",
            GearSlot.OffHand => "off hand",
            GearSlot.Ring => "ring",
            _ => slot.ToString().ToLowerInvariant(),
        };

        private static unsafe bool? Step(MoveState s)
        {
            string tag = "[Task_GearStorage]";
            if (s.FirstCallAt == DateTime.MinValue)
                s.FirstCallAt = DateTime.Now;
            // 会話/演出中に MoveItemSlot を発行しても無視されるので待つ(上限つき。店舗のメニューが残ったままなどで永久に待たない)。
            // 計画もこの後に作る(納品直後は所持品の反映が終わってからでないと新しい道具が見えない)。
            if (!Player.Available || Player.IsBusy || GenericHelpers.IsOccupied())
            {
                if (s.Plan == null && (DateTime.Now - s.FirstCallAt).TotalSeconds > BusyWaitMaxSeconds)
                {
                    IceLogging.Warning($"会話/演出が {BusyWaitMaxSeconds:F0} 秒以上終わらないため、かばんからアーマリーチェストへの移動を行いません", tag);
                    return true;
                }
                if (s.Plan != null && (DateTime.Now - s.Start).TotalSeconds > Math.Max(TotalTimeoutSeconds, s.Plan.Count * 2.0) + BusyWaitMaxSeconds)
                {
                    IceLogging.Warning("移動の途中で会話/演出が続いたため、かばんからの移動を打ち切ります", tag);
                    return Finish(s, tag);
                }
                return false;
            }

            var inv = InventoryManager.Instance();
            if (inv == null)
                return false;

            if (s.Plan == null)
            {
                var plan = BuildPlan(s);
                if (plan.Count == 0 && s.AfterUpgrade && !s.Rescanned)
                {
                    // 納品直後は新しい道具の反映がわずかに遅れることがある。少し待って 1 回だけ見直す
                    if (s.FirstEmptyAt == DateTime.MinValue)
                    {
                        s.FirstEmptyAt = DateTime.Now;
                        IceLogging.Debug($"{CosmicHelper.GetJobName(s.Job)} の道具がかばんに見えないので、{RescanDelayMs:F0}ms 後に再走査します", tag);
                    }
                    if ((DateTime.Now - s.FirstEmptyAt).TotalMilliseconds < RescanDelayMs)
                        return false;
                    s.Rescanned = true;
                    plan = BuildPlan(s);
                }
                s.Plan = plan;
                s.Start = DateTime.Now;
                if (s.Plan.Count == 0)
                {
                    PrunePending();
                    if (s.Purchase)
                    {
                        // ゲーム側の設定や空き状況により、購入品がすべてアーマリーチェストに入った場合はここに来る(移す物が無い)
                        if (s.ExpectedInBags > 0)
                            IceLogging.Warning($"購入時にかばん(所持品)に入ったと判定した {s.ExpectedInBags} 点が、かばんに見つかりません(手動で移動・売却した可能性)。移動は行いません", tag);
                        else
                            IceLogging.Info($"かばんに購入品はありません。購入品はすべてアーマリーチェストに入っているため、移動は不要です(購入品 {s.PurchasedIds.Count} 種)", tag);
                    }
                    else
                        IceLogging.Debug($"{CosmicHelper.GetJobName(s.Job)} の主道具/副道具・購入品はかばんに無いので移動しません", tag);
                    return true;
                }
                IceLogging.Info($"かばんに{(s.Purchase ? "購入品" : $"{CosmicHelper.GetJobName(s.Job)} の主道具/副道具・購入品")}が {s.Plan.Count} 件あります"
                    + $"(うち今回の購入品 {s.Plan.Count(e => e.Purchased)} 件)。アーマリーチェストへ移します: "
                    + string.Join(", ", s.Plan.Select(e => $"{e.Name}[{SlotNameJp(e.Slot)}]({e.SrcContainer}#{e.SrcSlot})")), tag);
                if (s.Purchase && s.ExpectedInBags > 0 && s.Plan.Count(e => e.Purchased) < s.ExpectedInBags)
                    IceLogging.Warning($"購入時にかばんに入ったと判定したのは {s.ExpectedInBags} 点ですが、かばんで見つかった今回の購入品は {s.Plan.Count(e => e.Purchased)} 点です(手動で移動・売却した可能性)", tag);
            }

            double totalLimit = Math.Max(TotalTimeoutSeconds, s.Plan.Count * 2.0);
            if ((DateTime.Now - s.Start).TotalSeconds > totalLimit)
            {
                IceLogging.Warning($"かばんからの移動が {totalLimit:F0} 秒以内に終わらなかったので打ち切ります", tag);
                return Finish(s, tag);
            }

            // 反映待ち
            if (s.Pending != null)
            {
                switch (CheckMove(inv, s))
                {
                    case MoveResult.Done:
                        s.Pending.Moved = true;
                        IceLogging.Info($"移動完了: {s.Pending.Name} → {s.PendingDst}#{s.PendingDstSlot}", tag);
                        s.Pending = null;
                        return false;
                    case MoveResult.Replaced:
                        IceLogging.Warning($"{s.Pending.Name} の元の位置 {s.Pending.SrcContainer}#{s.Pending.SrcSlot} が別の品に変わっていました(手動操作の可能性)。この品は諦めます", tag);
                        s.Pending.Failed = true;
                        s.Pending = null;
                        return false;
                }
                if ((DateTime.Now - s.PendingAt).TotalSeconds > PendingTimeoutSeconds)
                {
                    if (!StillInSource(inv, s.Pending))
                    {
                        // 移動先の枡では確認できなかったが、かばんからは出ている(別の枡に収まった等)。目的は果たしているので移動済み扱い
                        s.Pending.Moved = true;
                        IceLogging.Info($"移動完了(枡は未確認): {s.Pending.Name} はかばんから出ています", tag);
                    }
                    else
                    {
                        IceLogging.Warning($"{s.Pending.Name} の移動が {PendingTimeoutSeconds:F0} 秒で反映されませんでした。この品は諦めます", tag);
                        s.Pending.Failed = true;
                    }
                    s.Pending = null;
                }
                return false;
            }

            var next = s.Plan.FirstOrDefault(e => !e.Moved && !e.Failed);
            if (next == null)
                return Finish(s, tag);

            if (!StillInSource(inv, next))
            {
                // 計画後に位置が変わった(手動操作など)。二重移動を避けてこの品は飛ばす
                IceLogging.Warning($"{next.Name} が {next.SrcContainer}#{next.SrcSlot} に無くなっているので飛ばします", tag);
                next.Failed = true;
                return false;
            }

            var dstType = ArmouryOf(next.Slot);
            int dstSlot = FindFreeSlot(inv, dstType, s);
            if (dstSlot < 0)
            {
                if (s.NoFreeSlotWarned.Add(next.Slot))
                {
                    IceLogging.Warning($"アーマリーチェストの{SlotNameJp(next.Slot)}に空きが無いため {next.Name} を移せません", tag);
                    // レベリング中はミッションごとに呼ばれるので、チャットへの通知は数分に 1 回に抑える
                    if (EzThrottler.Throttle($"GearStorage NoFreeSlot {s.Job}/{next.Slot}", NoFreeSlotChatThrottleMs))
                        IceLogging.ChatInfo(IsJapanese
                            ? $"アーマリーチェストの{SlotNameJp(next.Slot)}に空きが無いため、{next.Name} をかばんから移せません。空きを作ってください"
                            : $"No free {SlotName(next.Slot)} slot in the armoury chest; cannot move {next.Name} out of your bags. Please free a slot", "[I.C.E.]");
                }
                next.Failed = true;
                return false;
            }

            int rc = inv->MoveItemSlot(next.SrcContainer, (ushort)next.SrcSlot, dstType, (ushort)dstSlot, true);
            IceLogging.Info($"移動: {next.Name} {next.SrcContainer}#{next.SrcSlot} → {dstType}#{dstSlot} (rc={rc})", tag);
            s.UsedDst.Add((dstType, dstSlot));
            s.Pending = next;
            s.PendingDst = dstType;
            s.PendingDstSlot = dstSlot;
            s.PendingAt = DateTime.Now;
            return false;
        }

        // かばんに無くなった購入品を「残っている購入品」の記録から外す(移し終えた、手動で移した、売った等)
        private static void PrunePending()
        {
            if (PendingFromPurchase.Count > 0)
                PendingFromPurchase.RemoveWhere(id => CountInBags(id) == 0);
        }

        private static bool? Finish(MoveState s, string tag)
        {
            PrunePending();
            var moved = s.Plan.Where(e => e.Moved).ToList();
            var notMoved = s.Plan.Where(e => !e.Moved).ToList();
            IceLogging.Info($"かばんからの移動を終了: 移動 {moved.Count} 件 / 未移動 {notMoved.Count} 件(計 {s.Plan.Count} 件)"
                + (notMoved.Count > 0 ? $" 未移動: {string.Join(", ", notMoved.Select(e => $"{e.Name}[{SlotNameJp(e.Slot)}]"))}" : ""), tag);
            string jobName = CosmicHelper.GetJobName(s.Job);
            if (moved.Count > 0)
            {
                string names = ShortList(moved.Select(e => e.Name));
                // 道具モードでは「このジョブの道具」と「購入でかばんに入った装備(別ジョブの品もありうる)」を分けて言う
                bool anyTool = moved.Any(e => e.IsTool);
                bool anyPurchased = moved.Any(e => !e.IsTool);
                string jaWhat = anyTool && anyPurchased ? $"{jobName}の道具と、購入でかばんに入った装備"
                              : anyTool ? $"{jobName}の道具" : "購入でかばんに入った装備";
                string enWhat = anyTool && anyPurchased ? $"{jobName} tool(s) and purchased gear that went into your bags"
                              : anyTool ? $"{jobName} tool(s)" : "purchased gear that went into your bags";
                IceLogging.ChatInfo(IsJapanese
                    ? (s.AfterUpgrade
                        ? $"強化した道具をアーマリーチェストへ移しました: {names}"
                        : s.Purchase
                            ? $"かばん(所持品)に入った装備 {moved.Count} 点をアーマリーチェストへ移しました: {names}"
                            : $"かばんにあった{jaWhat}をアーマリーチェストへ移しました: {names}")
                    : (s.AfterUpgrade
                        ? $"Moved the upgraded tool(s) to the armoury chest: {names}"
                        : s.Purchase
                            ? $"Moved {moved.Count} gear item(s) from your bags to the armoury chest: {names}"
                            : $"Moved {enWhat} to the armoury chest: {names}"), "[I.C.E.]");
            }
            if (s.Purchase && notMoved.Count > 0)
            {
                // 購入後の移動で移せなかった品(空き不足・時間切れ・手動操作)。最強装備の候補にならないので、ユーザーに知らせる
                string names = ShortList(notMoved.Select(e => $"{e.Name}({SlotNameJp(e.Slot)})"));
                IceLogging.ChatError(IsJapanese
                    ? $"かばん(所持品)の装備 {notMoved.Count} 点をアーマリーチェストへ移せませんでした(空き不足など): {names}。空きを作って手動で移してください"
                    : $"Could not move {notMoved.Count} gear item(s) from your bags to the armoury chest (no free slot, etc.): {names}. Please free slots and move them manually", "[I.C.E.]");
            }
            return true;
        }

        // チャット用に名前を短くまとめる(多いときは先頭 8 件 + 件数)
        private static string ShortList(IEnumerable<string> names)
        {
            var list = names.ToList();
            if (list.Count <= 8)
                return string.Join(", ", list);
            return IsJapanese
                ? $"{string.Join(", ", list.Take(8))} …ほか {list.Count - 8} 件"
                : $"{string.Join(", ", list.Take(8))} … and {list.Count - 8} more";
        }
    }
}
