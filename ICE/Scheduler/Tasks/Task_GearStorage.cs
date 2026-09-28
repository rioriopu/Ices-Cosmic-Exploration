using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ICE.Utilities.LevelingGearShop;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// かばん(Inventory1〜4)に入っている指定ジョブの主道具/副道具をアーマリーチェスト(ArmoryMainHand / ArmoryOffHand)へ移す。
    /// レリックの強化(納品)で受け取った新しい主道具はかばんに入るが、Stylist(/stylist crafter)やゲームの「おすすめ装備」は
    /// アーマリーチェストと装備中の物しか候補にしないため、かばんに残っていると「最強装備」で見つけられず流れが止まる(実機 2026-09-28)。
    /// 移動は 1 tick に 1 件だけ発行し、次の tick 以降で反映を確認してから次の品へ進む(未反映のまま連続発行すると同じ空き枠を取り合う)。
    /// </summary>
    internal static class Task_GearStorage
    {
        private static bool IsJapanese => Task_BuyLevelingGear.IsJapanese;

        private const double PendingTimeoutSeconds = 3;  // 1 件の移動の反映待ち上限
        private const double TotalTimeoutSeconds = 20;   // タスク全体の上限(計画を作ってから)
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
            public bool Moved;
            public bool Failed;
        }

        /// <summary>1 回のタスク実行分の進行状態。タスク関数ごとに新しく作るので、途中で Abort されても次回に古い状態が残らない。</summary>
        private class MoveState
        {
            public uint Job;
            public bool AfterUpgrade;             // レリック強化(納品)直後の呼び出しか(文言と再走査の有無が変わる)
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

        /// <summary>指定ジョブの道具移動タスクを末尾に積む。</summary>
        public static void Enqueue(uint job, bool afterUpgrade = false)
            => P.TaskManager.Enqueue(CreateTask(job, afterUpgrade), $"Moving {CosmicHelper.GetJobName(job)} tools from bags to the armoury", Utils.TaskConfig);

        // かばんを走査して、指定ジョブが装備できる主道具/副道具を列挙する。
        // 部位とジョブの判定は LevelingGearShop(Lumina の EquipSlotCategory / ClassJobCategory)を再利用する。
        private static unsafe List<MoveEntry> BuildPlan(uint job)
        {
            var list = new List<MoveEntry>();
            var inv = InventoryManager.Instance();
            if (inv == null) return list;
            var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet == null) return list;

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
                    if (slot != GearSlot.MainHand && slot != GearSlot.OffHand) continue;
                    if (!CosmicJobs(row.ClassJobCategory.ValueNullable).Contains(job)) continue;
                    list.Add(new MoveEntry
                    {
                        ItemId = it->ItemId,
                        Name = row.Name.ExtractText(),
                        SrcContainer = type,
                        SrcSlot = i,
                        Slot = slot,
                    });
                }
            }
            return list;
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

        private static string SlotName(GearSlot slot)
            => IsJapanese ? SlotNameJp(slot) : (slot == GearSlot.MainHand ? "main hand" : "off hand");

        private static unsafe bool? Step(MoveState s)
        {
            string tag = "[Task_GearStorage]";
            if (!Player.Available)
                return false;
            // 会話/演出中に MoveItemSlot を発行しても無視されるので待つ。
            // 計画もこの後に作る(納品直後は所持品の反映が終わってからでないと新しい道具が見えない)。
            if (Player.IsBusy || GenericHelpers.IsOccupied())
                return false;

            var inv = InventoryManager.Instance();
            if (inv == null)
                return false;

            if (s.Plan == null)
            {
                var plan = BuildPlan(s.Job);
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
                    plan = BuildPlan(s.Job);
                }
                s.Plan = plan;
                s.Start = DateTime.Now;
                if (s.Plan.Count == 0)
                {
                    IceLogging.Debug($"{CosmicHelper.GetJobName(s.Job)} の主道具/副道具はかばんに無いので移動しません", tag);
                    return true;
                }
                IceLogging.Info($"{CosmicHelper.GetJobName(s.Job)} の主道具/副道具がかばんに {s.Plan.Count} 件あります。アーマリーチェストへ移します: "
                    + string.Join(", ", s.Plan.Select(e => $"{e.Name}({e.SrcContainer}#{e.SrcSlot})")), tag);
            }

            if ((DateTime.Now - s.Start).TotalSeconds > TotalTimeoutSeconds)
            {
                IceLogging.Warning($"道具の移動が {TotalTimeoutSeconds:F0} 秒以内に終わらなかったので打ち切ります", tag);
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

        private static bool? Finish(MoveState s, string tag)
        {
            var moved = s.Plan.Where(e => e.Moved).ToList();
            int failed = s.Plan.Count(e => !e.Moved);
            IceLogging.Info($"道具の移動を終了: 移動 {moved.Count} 件 / 未移動 {failed} 件(計 {s.Plan.Count} 件)", tag);
            if (moved.Count > 0)
            {
                string names = string.Join(", ", moved.Select(e => e.Name));
                string jobName = CosmicHelper.GetJobName(s.Job);
                IceLogging.ChatInfo(IsJapanese
                    ? (s.AfterUpgrade
                        ? $"強化した道具をアーマリーチェストへ移しました: {names}"
                        : $"かばんにあった{jobName}の道具をアーマリーチェストへ移しました: {names}")
                    : (s.AfterUpgrade
                        ? $"Moved the upgraded tool(s) to the armoury chest: {names}"
                        : $"Moved {jobName} tool(s) from your bags to the armoury chest: {names}"), "[I.C.E.]");
            }
            return true;
        }
    }
}
