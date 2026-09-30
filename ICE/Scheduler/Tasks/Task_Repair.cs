using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Repair
    {
        // ---- 修理の失敗判定(この Start の間だけ有効。SchedulerMain.EnablePlugin → ResetSession で解除) ----
        // 実機(2026-09-30 02:34〜02:54): 自己修理できない装備(ダークマターの等級不足/修理担当ジョブのレベル不足)で
        // 「30 秒で打ち切り → 黙って受注」を 10 回繰り返し、02:55 に装備が壊れて 07:51 まで Artisan が製作を拒否し続けた。
        // 今は自己修理に失敗したら修理 NPC に切り替え、修理 NPC でも直らなければ理由をチャットに出して止める。

        /// <summary>この Start の間、自己修理では装備を直せないと分かったジョブ(そのジョブの修理は修理 NPC で行う)</summary>
        internal static readonly HashSet<uint> SelfRepairUnusableJobs = new();
        internal static bool IsSelfRepairUnusable => SelfRepairUnusableJobs.Contains((uint)Player.Job);
        /// <summary>この Start の間、所持品(アーマリーチェスト)の損耗だけでは修理に行かない(修理窓で直しきれなかった)</summary>
        internal static bool ArmoryRepairSuppressed = false;

        private const double OpenSelfRepairMaxSeconds = 15;     // 自己修理の窓が開くまでの上限
        private const double SelfRepairMaxSeconds = 30;         // 自己修理の上限(従来どおり)
        private const double RepairButtonDisabledSeconds = 3;   // 「すべて修理」が押せない状態がこれだけ続いたら自己修理できないと判断
        private const double NpcRepairMaxSeconds = 90;          // 修理 NPC での修理が終わるまでの上限
        private const double NpcArmoryGraceSeconds = 10;        // 装備中の品が直ったあと、所持品の修理を待つ上限(修理窓が開いてから数える)

        private static long _openSelfRepairStart = 0;
        private static long _selfRepairStart = 0;
        private static long _repairButtonDisabledSince = 0;
        private static string _selfRepairFailReason = "";       // 空でなければ今回の自己修理は失敗(FinishSelfRepair で修理 NPC へ切り替える)
        private static DateTime _npcRepairSince = DateTime.MinValue;
        private static DateTime _npcEquippedDoneAt = DateTime.MinValue;

        // 修理 NPC までの移動は 5 分で打ち切って次へ進む(着けなかった場合は RepairAtNpc の上限 → VerifyVendorRepair で止まる)
        internal static readonly ECommons.Automation.NeoTaskManager.TaskManagerConfiguration NpcPathConfig = new(timeLimitMS: 300000, abortOnTimeout: false);

        /// <summary>Start 時に修理の失敗判定をすべて解除する(装備やダークマターを整えていれば自己修理に戻る)</summary>
        internal static void ResetSession()
        {
            SelfRepairUnusableJobs.Clear();
            ArmoryRepairSuppressed = false;
            _selfRepairFailReason = "";
            _openSelfRepairStart = 0;
            ResetSelfRepairWatch();
            _npcRepairSince = DateTime.MinValue;
            _npcEquippedDoneAt = DateTime.MinValue;
        }

        private static void ResetSelfRepairWatch()
        {
            _selfRepairStart = 0;
            _repairButtonDisabledSince = 0;
        }

        public static void Enqueue()
        {
            bool repairAll = PlayerHelper.AnyNeedsRepair(Char_Info.RepairPercent) && Char_Info.RepairAllGear && !ArmoryRepairSuppressed;
            // 壊れた装備(耐久 0%)はしきい値と関係なく修理する
            bool repairEquipped = PlayerHelper.NeedsRepair(Char_Info.RepairPercent) || PlayerHelper.HasBrokenEquippedGear();

            if (repairAll)
            {
                P.TaskManager.EnqueueMulti
                (
                    new(BeginSelfRepair, "Resetting the self repair watch"),
                    new(OpenSelfRepair, "Opening the self repair window"),
                    new(SelfRepair_All, "Executing the self repair"),
                    new(CloseRepair, "Closing Self Repair"),
                    new(FinishSelfRepair, "Checking the self repair result")
                );
            }
            else if (repairEquipped)
            {
                P.TaskManager.EnqueueMulti
                (
                    new(BeginSelfRepair, "Resetting the self repair watch"),
                    new(OpenSelfRepair, "Opening the self repair window"),
                    new(SelfRepair, "Executing the self repair"),
                    new(CloseRepair, "Closing Self Repair"),
                    new(FinishSelfRepair, "Checking the self repair result")
                );
            }
            else if (SchedulerMain.State == IceState.Repair)
            {
                // 修理が必要な品が無くなっていた。以前は何も積まずに Repair 状態のまま空回りしていた
                IceLogging.Info("修理が必要な装備が無くなっていたので、ミッションの受注へ進みます", "[Task_Repair]");
                SchedulerMain.State = IceState.GrabMission;
            }
        }

        /// <summary>自己修理の見張りを初期化する(自己修理の流れの先頭に積む。Task_DualClass からも使う)</summary>
        public static bool? BeginSelfRepair()
        {
            _openSelfRepairStart = 0;
            _selfRepairFailReason = "";
            ResetSelfRepairWatch();
            return true;
        }

        // 自己修理の結果で次の状態を決める。失敗していたら修理 NPC へ切り替え、修理 NPC も使えなければ止める。
        // 修理の流れ(Repair 状態)の中にいないとき(デバッグ画面の「Test Repair Function」など)は状態を変えない
        private static bool? FinishSelfRepair()
        {
            string tag = "[Task_Repair: Self Repair Result]";
            bool inRepairFlow = SchedulerMain.State == IceState.Repair;

            if (string.IsNullOrEmpty(_selfRepairFailReason))
            {
                // 修理できた = 装備は使える状態。製作側の「破損が修理されないまま続いた」回数を戻す
                if (!PlayerHelper.HasBrokenEquippedGear())
                    Task_Craft.ResetBrokenGearHits();
                if (inRepairFlow)
                    SchedulerMain.State = IceState.GrabMission;
                return true;
            }

            string reason = _selfRepairFailReason;
            _selfRepairFailReason = "";
            if (!inRepairFlow)
            {
                IceLogging.Warning($"自己修理に失敗しました({reason})", tag);
                return true;
            }

            if (!HasRepairNpc())
            {
                StopForRepair($"自己修理に失敗し({reason})、この惑星の修理 NPC も登録されていません",
                    Loc.T("ICE stopped because your gear needs repair but no repair NPC is registered for this planet. Repair your gear manually and press Start again."), tag);
                return true;
            }

            IceLogging.Warning($"自己修理に失敗しました({reason})。修理 NPC での修理に切り替えます(RepairAtVendor の設定に関係なく) {PlayerHelper.DescribeBrokenGear()}", tag);
            IceLogging.ChatInfo(Loc.T("Self repair could not fix your equipped gear, so ICE will repair at the repair NPC."), "[I.C.E.]");
            // 拠点での用事は修理だけにする(他の用事は次の HubActivityCheck で改めて判定される)
            Task_HubActivities.RepairNpc = true;
            Task_HubActivities.RelicTurnin = false;
            Task_HubActivities.CosmoBuy = false;
            Task_HubActivities.CanGamba = false;
            Task_HubActivities.CanBuyDrones = false;
            Task_HubActivities.CollectVenture = false;
            SchedulerMain.State = IceState.HubReturn;
            return true;
        }

        internal static bool HasRepairNpc() => NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out _);

        /// <summary>
        /// 修理できないまま進むと壊れた装備で空回りするため、理由をチャットに出して止める(既存の停止手順: Idle + キュー破棄)。
        /// </summary>
        internal static void StopForRepair(string logReason, string chatText, string tag)
        {
            IceLogging.Error($"修理できないため ICE を停止します: {logReason} {PlayerHelper.DescribeBrokenGear()}", tag);
            IceLogging.ChatError(chatText, "[I.C.E.]");
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            SchedulerMain.State = IceState.Idle;
            P.TaskManager.Tasks.Clear();
            if (C.PlaySoundAlert)
                _ = SoundPlayer.PlaySoundAsync();
        }

        public static unsafe bool? HubCheck()
        {
            string tag = "[Hub Return]";

            if (!C.UseHubReturn)
            {
                IceLogging.Info("We were told we didn't wanna hub return, so we gonna respec this");
                return true;
            }

            if (C.AvoidStellarReturn && !C.AvoidStellarReturnExceptHub)
            {
                IceLogging.Info("Stellar Return is fully disabled, walking to hub instead", tag);
                return true;
            }

            // COSMO MISSIONS(WKSMission)ウィンドウが開いていると Stellar Return(帰還)が発動できない。
            // 開いていれば先に閉じてから帰還処理へ進む(実機報告: 窓を開いたまま止まり、閉じると帰還が始まる)。
            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var wksMissionWin) && wksMissionWin.IsAddonReady)
            {
                if (EzThrottler.Throttle("CloseWKSMissionForReturn", 500))
                {
                    IceLogging.Info("帰還前に COSMO MISSIONS ウィンドウを閉じます(Stellar Return 阻害回避)", tag);
                    GenericHandlers.FireCallback("WKSMission", true, -1);
                }
                return false;
            }

            if (CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var HubCenter))
            {
                Vector3 PlayerPos = Player.Position;

                if (Player.DistanceTo(HubCenter) < C.HubReturn_Distance)
                {
                    if (PlayerHelper.IsScreenReady())
                    {
                        IceLogging.Info("Player is in the range of the main hub area right now", tag);
                        return true;
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Waiting for screen to be ready", 2000))
                            IceLogging.Verbose("Waiting for screen to be ready", tag);
                    }
                }
                else
                {
                    //Not within the vicinity of the hub area, time to return
                    if (!Player.IsBusy)
                    {
                        if (EzThrottler.Throttle("Returning back to the moon base"))
                            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 26);
                    }
                }

                return false;
            }
            else
            {
                IceLogging.Error($"HEY. WE'RE MISSING THE HUB. THIS ISN'T GOOD. PLEASE ICE FIX THIS <3\n" +
                    $"From: Past Ice.", tag);
                SchedulerMain.State = IceState.Idle;
                P.TaskManager.Tasks.Clear();
                return true;
            }
        }
        public static bool? Repair_PathTo()
        {
            string handle = "[Task_Repair: PathTo]";

            var zoneId = Player.Territory;

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out var npcEntry))
            {
                Vector3 randomPos = NpcData.GetRandomPointInCircle(npcEntry.Location_Circle, 0.5f);
                if (!Task_NavmeshMove.Task_NavTo(randomPos, distance: 5, npcLoc: npcEntry.Location_Npc).Value)
                {
                    if (EzThrottler.Throttle("Repair move message", 1000))
                        IceLogging.Verbose($"Pathing to repair NPC. Current distance: {Player.DistanceTo(npcEntry.Location_Npc)}", handle);
                }
                else
                {
                    IceLogging.Debug("We're close enough to the repair npc! Continuing on", handle);
                    return true;
                }
            }
            else
            {
                if (EzThrottler.Throttle("Error message: NPC", 5000))
                    IceLogging.Error("Hey! We don't have this npc coded yet, which means I forgot bout it, could you let me know\n" +
                                     $"Planet Territory ID: {Player.Territory.RowId}", handle);
            }

            return false;
        }

        /// <summary>修理 NPC での修理を始める前に見張りを初期化する(Task_HubActivities から積む)</summary>
        public static bool? BeginNpcRepair()
        {
            _npcRepairSince = DateTime.MinValue;
            _npcEquippedDoneAt = DateTime.MinValue;
            return true;
        }

        public static unsafe bool? RepairAtNpc()
        {
            string tag = "[Task_Repair: NPC]";
            var now = DateTime.Now;
            if (_npcRepairSince == DateTime.MinValue)
                _npcRepairSince = now;

            IGameObject? gameObject = null;
            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Repair, out var npcEntry))
                Utils.TryGetObjectByDataId(npcEntry.NpcId, out gameObject);

            // 装備中の品は壊れた品を含めて 100% 近くまで直す。所持品(アーマリーチェスト)は RepairAllGear のときだけ
            bool equippedDone = !PlayerHelper.NeedsRepair(99.9f) && !PlayerHelper.HasBrokenEquippedGear();
            bool armoryPending = Char_Info.RepairAllGear && !ArmoryRepairSuppressed && PlayerHelper.AnyNeedsRepair(99.9f);
            if (EzThrottler.Throttle("NPC repair state log", 2000))
                IceLogging.Debug($"装備中の修理完了={equippedDone} | 所持品の修理待ち={armoryPending}", tag);

            if (equippedDone && !armoryPending)
            {
                IceLogging.Debug("Repair Complete! Finishing task and closing window", tag);
                return true;
            }

            // 以前は「所持品まで全部 99.9% 超」になるまで待ち続けた。所持品が直りきらないと TaskManager の上限(30 分)まで
            // 止まることがあったので、時間で打ち切る。装備中の品が直ったかどうかは VerifyVendorRepair で判定する
            if ((now - _npcRepairSince).TotalSeconds >= NpcRepairMaxSeconds)
            {
                IceLogging.Warning($"修理 NPC での修理が {NpcRepairMaxSeconds:F0} 秒以内に終わりませんでした(ギル不足/NPC に話しかけられない可能性)。結果の確認へ進みます {PlayerHelper.DescribeBrokenGear()}", tag);
                return true;
            }

            bool repairReady = GenericHelpers.TryGetAddonMaster<Repair>("Repair", out var repair) && repair.IsAddonReady;
            // 所持品を待つ猶予は、修理窓が操作できるようになってから数える(話しかけ・選択肢の間に過ぎてしまわないように)
            if (equippedDone && repairReady)
            {
                if (_npcEquippedDoneAt == DateTime.MinValue)
                    _npcEquippedDoneAt = now;
                else if ((now - _npcEquippedDoneAt).TotalSeconds >= NpcArmoryGraceSeconds)
                {
                    ArmoryRepairSuppressed = true;
                    IceLogging.Warning("装備中の品は修理できましたが、所持品(アーマリーチェスト)に直りきらない品が残りました。この Start の間は所持品の損耗では修理に行きません", tag);
                    return true;
                }
            }

            if (repairReady)
            {
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var Yesno) && Yesno.IsAddonReady)
                {
                    if (FrameThrottler.Throttle("Saying yes to the gil"))
                        Yesno.Yes();
                }
                else if (EzThrottler.Throttle("Sending Repair Request", 2000))
                {
                    if (!equippedDone)
                    {
                        // まず装備中の品(壊れた品を含む)を「すべて修理」で直す。以前はしきい値超〜99% の装備だと押さずに待ち続けた
                        IceLogging.Debug("Repair All (equipped)", tag);
                        repair.RepairAll();
                    }
                    else
                    {
                        // 所持品の修理(従来の RepairAllGear 用コールバック)
                        IceLogging.Debug("Firing off callback to repair all", tag);
                        GenericHandlers.FireCallback("Repair", true, 1);
                    }
                }
            }
            else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectIconString", out var iconString) && GenericHelpers.IsAddonReady(iconString))
            {
                if (FrameThrottler.Throttle("Firing off repair string"))
                {
                    IceLogging.Debug("Selecting repair from vendor", tag);
                    ECommons.Automation.Callback.Fire(iconString, true, 6);
                }
            }
            else
            {
                if (EzThrottler.Throttle("Attempting to target the repair NPC + Interact"))
                {
                    Utils.TargetgameObject(gameObject);
                    Utils.InteractWithObject(gameObject);
                }
            }

            return false;
        }

        /// <summary>
        /// 修理 NPC での修理のあとに呼ぶ。装備中の品が壊れたまま/しきい値以下のままなら(ギル不足・NPC に届かない等)、
        /// 次の HubActivityCheck でまた修理 NPC へ向かう空回りになるので、理由をチャットに出して止める。
        /// </summary>
        public static bool? VerifyVendorRepair()
        {
            string tag = "[Task_Repair: Verify]";
            bool broken = PlayerHelper.HasBrokenEquippedGear();
            bool stillLow = PlayerHelper.NeedsRepair(Char_Info.RepairPercent);
            if (broken || stillLow)
            {
                StopForRepair($"修理 NPC で修理したあとも装備が直っていません [壊れ={broken}, 耐久{Char_Info.RepairPercent}%以下={stillLow}]",
                    Loc.T("ICE stopped because your gear could not be repaired at the repair NPC (not enough gil, or the NPC could not be reached). Repair your gear manually and press Start again."), tag);
                return true;
            }
            // 装備中の品は使える状態でも、所持品(アーマリーチェスト)が直らずに残った(NPC に届かない・ギル不足・修理窓が開かない等)。
            // そのままだと次の HubActivityCheck でまた所持品のために修理 NPC へ向かい、受注せずに往復し続けるので、
            // この Start の間は所持品の損耗では修理に行かない
            if (Char_Info.RepairAllGear && !ArmoryRepairSuppressed && PlayerHelper.AnyNeedsRepair(Char_Info.RepairPercent))
            {
                ArmoryRepairSuppressed = true;
                IceLogging.Warning("修理 NPC で所持品(アーマリーチェスト)を直せませんでした。この Start の間は所持品の損耗では修理に行きません", tag);
            }
            Task_Craft.ResetBrokenGearHits();
            IceLogging.Info("修理 NPC での修理を確認しました", tag);
            return true;
        }

        public unsafe static bool? OpenSelfRepair()
        {
            string tag = "[Task_Repair: Open Self Repair]";
            var now = Environment.TickCount64;
            if (_openSelfRepairStart == 0)
                _openSelfRepairStart = now;

            if (C.Stop_DarkMatter && PlayerHelper.GetItemCount(Utils.DarkMatter_8Id, out var dmCount) && dmCount < Char_Info.Minimum_DarkMatter)
            {
                _openSelfRepairStart = 0;
                IceLogging.ChatInfo("We've ran below the amount of dark matter we want to have, and we can't repair. So we're just hard stopping", "[I.C.E.] Task: Self Repair");
                SchedulerMain.State = IceState.Idle;
                P.TaskManager.Tasks.Clear();
                return true;
            }

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var x) && GenericHelpers.IsAddonReady(x))
            {
                _openSelfRepairStart = 0;
                return true;
            }

            // 窓が開かない(以前は TaskManager の上限 30 分まで待っていた)。移動中・操作中などの一時的な原因もありうるので、
            // ジョブを「自己修理不可」にはせず、今回だけ修理 NPC に回す(次回はまた自己修理を試す)
            if (now - _openSelfRepairStart > OpenSelfRepairMaxSeconds * 1000)
            {
                _openSelfRepairStart = 0;
                _selfRepairFailReason = $"自己修理の窓が {OpenSelfRepairMaxSeconds:F0} 秒以内に開かない";
                IceLogging.Warning(_selfRepairFailReason, tag);
                return true;
            }

            if (Svc.Condition[ConditionFlag.Mounted])
            {
                if (EzThrottler.Throttle("Attempting to dismount for repairing"))
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
                return false;
            }

            if (EzThrottler.Throttle("Opening Self Repair", 1000))
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);
            return false;
        }

        // RepairAllGear=False 側。以前は打ち切りが無く、直らない装備だと TaskManager の上限(30 分)まで止まっていた
        public static bool? SelfRepair() => SelfRepairCore(false, "[Self Repair Task]");

        public static bool? SelfRepair_All() => SelfRepairCore(true, "Self Repair: All");

        // 自己修理の本体。includeArmory=true は「所持品も修理」(RepairAllGear)用。
        // 失敗(ボタンが押せない/30 秒で終わらない)したら _selfRepairFailReason を立て、このジョブを「自己修理不可」にして true を返す。
        // 装備中の品が直っていて所持品だけ残った場合は失敗にせず、この Start の間は所持品の修理を打ち切る。
        private unsafe static bool? SelfRepairCore(bool includeArmory, string tag)
        {
            // 窓が開かなかった等で既に失敗している → 何もせず次(CloseRepair → FinishSelfRepair)へ
            if (!string.IsNullOrEmpty(_selfRepairFailReason))
                return true;

            // 装備中の品: しきい値以下が無く、壊れた品(耐久 0%)も無い
            bool equippedOk = !PlayerHelper.NeedsRepair(Char_Info.RepairPercent) && !PlayerHelper.HasBrokenEquippedGear();
            bool armoryOk = !includeArmory || ArmoryRepairSuppressed || !PlayerHelper.AnyNeedsRepair(Char_Info.RepairPercent);

            if (equippedOk && armoryOk)
            {
                if (Svc.Condition[ConditionFlag.Occupied39])
                {
                    if (EzThrottler.Throttle("Waiting for repair"))
                        IceLogging.Verbose("Waiting for us to finish repairs", tag);
                    return false;
                }
                ResetSelfRepairWatch();
                IceLogging.Debug("All gear has been repaired, continuing", tag);
                return true;
            }

            var now = Environment.TickCount64;
            if (_selfRepairStart == 0)
                _selfRepairStart = now;

            bool buttonDead = _repairButtonDisabledSince != 0 && now - _repairButtonDisabledSince > RepairButtonDisabledSeconds * 1000;
            bool timedOut = now - _selfRepairStart > SelfRepairMaxSeconds * 1000;
            if (buttonDead || timedOut)
            {
                string why = buttonDead
                    ? "修理窓の「すべて修理」が押せない(ダークマターの等級不足/修理担当ジョブのレベル不足の可能性)"
                    : $"{SelfRepairMaxSeconds:F0} 秒以内に完了しない(修理窓の操作不成立の可能性)";
                ResetSelfRepairWatch();
                if (equippedOk)
                {
                    // 装備中の品は直っている。残りは所持品(修理窓の表示区分外、または自己修理できない品)なので、
                    // この Start の間は所持品の損耗では修理に行かない(毎回 30 秒待つのを防ぐ)
                    ArmoryRepairSuppressed = true;
                    IceLogging.Warning($"装備中の品は修理できましたが、所持品に自己修理できない品が残っています({why})。この Start の間は所持品の損耗では修理しません", tag);
                    return true;
                }
                _selfRepairFailReason = why;
                SelfRepairUnusableJobs.Add((uint)Player.Job);
                IceLogging.Warning($"自己修理に失敗しました: {why}。この Start の間、{Player.Job} の修理は修理 NPC で行います {PlayerHelper.DescribeBrokenGear()}", tag);
                return true;
            }

            if (Svc.Condition[ConditionFlag.Mounted])
            {
                _repairButtonDisabledSince = 0;
                if (EzThrottler.Throttle("Attempting to dismount for repairing"))
                {
                    IceLogging.Debug("Dismounting for self repair", tag);
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
                }
                return false;
            }
            if (Svc.Condition[ConditionFlag.Occupied39])
            {
                // 修理中(アニメーション)。この間は「押せない」の判定をしない
                _repairButtonDisabledSince = 0;
                if (EzThrottler.Throttle("Waiting for repair"))
                    IceLogging.Verbose("Waiting for us to finish repairs", tag);
                return false;
            }
            // 確認ダイアログ → はい(AddonMaster 経由で確実に押す)
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yn) && yn.IsAddonReady)
            {
                _repairButtonDisabledSince = 0;
                if (FrameThrottler.Throttle("SelfRepairYes", 300)) yn.Yes();
                return false;
            }
            // 修理窓 → 「すべて修理」。押せない(灰色)状態が続くなら、ダークマターの等級不足か修理担当ジョブのレベル不足
            // (RepairAll() は ClickButtonIfEnabled なので、押せないと何も起きずに空回りしていた)
            if (GenericHelpers.TryGetAddonMaster<Repair>("Repair", out var rep) && rep.IsAddonReady)
            {
                var button = rep.Addon->RepairAllButton;
                if (button != null && button->IsEnabled)
                {
                    _repairButtonDisabledSince = 0;
                    if (FrameThrottler.Throttle("Firing off repair request", 300))
                    {
                        IceLogging.Debug("Repair All (ECommons)", tag);
                        rep.RepairAll();
                    }
                }
                else if (_repairButtonDisabledSince == 0)
                {
                    _repairButtonDisabledSince = now;
                }
                return false;
            }
            // 修理窓が無い(まだ開いていない/手動で閉じられた)のに装備は損耗 → 自己修理を(再)展開して止まらないようにする
            _repairButtonDisabledSince = 0;
            if (EzThrottler.Throttle("ReopenSelfRepair", 1000))
            {
                IceLogging.Debug("修理窓が無いので自己修理を再展開します", tag);
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);
            }
            return false;
        }

        public unsafe static bool? CloseRepair()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var Yesno) && Yesno.IsAddonReady)
            {
                if (FrameThrottler.Throttle("Closing surprise repair window"))
                    ECommons.Automation.Callback.Fire(Yesno.Base, true, -1);
                return false;
            }
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var repairWindow) && GenericHelpers.IsAddonReady(repairWindow))
            {
                if (EzThrottler.Throttle("Attempting to close out the repair window", 300))
                {
                    IceLogging.Debug("Closing the repair window", "[Repair Task]");
                    ECommons.Automation.Callback.Fire(repairWindow, true, -1);
                }
                return false;
            }
            // 修理窓が無い/閉じかけなら完了。以前は窓が最初から無いと false を返し続け、TaskManager の上限まで止まることがあった
            return true;
        }
    }
}
