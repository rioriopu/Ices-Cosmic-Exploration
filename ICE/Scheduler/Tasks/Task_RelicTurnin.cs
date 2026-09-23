using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ICE.Utilities.Cosmic_Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal class Task_RelicTurnin
    {
        public static uint TurninJob = 0;
        // 納品(強化)のために一時的に切り替えたジョブ。0 = 切り替えていない。
        private static uint _tempJob = 0;

        /// <summary>Relic Grind モードで、設定に関係なく納品→ジョブ切替→最強装備までを自動で行うか。</summary>
        public static bool AutoUpgradeActive =>
            Mission_Settings.Mode == ModeSelect.RelicMode && C.Relic_AutoUpgradeInRelicMode;

        /// <summary>主道具の分析が規定値に達したら納品(強化)に行くか。「Turnin if relic is complete」または Relic Grind の自動強化。</summary>
        public static bool ShouldTurninRelic => C.TurninRelic || AutoUpgradeActive;

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
            (
                new(RegisterJob, "Register Job Swap Class"),
                new(CheckJobSwap, "Checking to see if we need to swap jobs", Utils.TaskConfig),
                new(Relic_PathTo, "Heading to the relic NPC for turnin"),
                new(TalkToResearchWay, "Talk to researchway"),
                new(SelectReport, "Selecting Report", Utils.TaskConfig),
                new(SelectRelicClass, "Selecting the class to turnin on", Utils.TaskConfig)
            );
        }
        public static bool? RegisterJob()
        {
            IceLogging.Verbose("Registering what job to turn in on");
            // 納品判定(HubActivityCheck)は Mission_Settings.SelectedJob のツールで行っている。
            // 実ジョブ(Player.Job)を登録していると、Agendaモードで「選択ジョブ≠現ジョブ」のとき
            // 納品できないツールを延々と納品しようとして進めない無限ループになる。判定と実行を揃える。
            TurninJob = Mission_Settings.SelectedJob != 0 ? Mission_Settings.SelectedJob : (uint)Player.Job;
            _tempJob = ResolveTempJob(TurninJob);
            if (_tempJob != 0)
                IceLogging.Info($"納品(強化)中は一時的にジョブ {(Job)_tempJob} へ切り替えます(納品ジョブ: {(Job)TurninJob})", "[Task_Relic]");

            return true;
        }

        private static unsafe bool HasGearset(uint job)
        {
            if (job == 0) return false;
            var gearsets = RaptureGearsetModule.Instance();
            if (gearsets == null) return false;
            foreach (ref var gs in gearsets->Entries)
            {
                if (!gearsets->IsValidGearset(gs.Id)) continue;
                if (gs.ClassJob == job) return true;
            }
            return false;
        }

        // 納品(強化)する主道具を装備したままだと強化できないため、一時的に切り替えるジョブを決める。
        // 設定のジョブ(Relic_BattleJob)にギアセットがあればそれを使い、無ければ納品ジョブ以外でギアセットのある任意のジョブを選ぶ
        // (戦闘職を優先し、無ければ他のクラフター/ギャザラーでもよい)。切り替えない場合は 0。
        private static unsafe uint ResolveTempJob(uint turninJob)
        {
            bool swap = Char_Info.Relic_SwapJob || AutoUpgradeActive;
            if (!swap)
                return 0;

            var configured = Char_Info.Relic_BattleJob;
            if (configured != 0 && configured != turninJob && HasGearset(configured))
                return configured;

            var gearsets = RaptureGearsetModule.Instance();
            if (gearsets == null)
                return 0;

            uint bestBattle = 0, bestBattleLv = 0;
            uint bestOther = 0, bestOtherLv = 0;
            foreach (ref var gs in gearsets->Entries)
            {
                if (!gearsets->IsValidGearset(gs.Id)) continue;
                uint job = gs.ClassJob;
                if (job == 0 || job == turninJob) continue;
                uint lv = (uint)Player.GetLevel((Job)job);
                if (lv == 0) continue;

                bool isCosmicJob = CosmicHelper.CrafterJobList.Contains(job) || CosmicHelper.GatheringJobList.Contains(job);
                if (!isCosmicJob)
                {
                    if (lv > bestBattleLv) { bestBattle = job; bestBattleLv = lv; }
                }
                else if (lv > bestOtherLv) { bestOther = job; bestOtherLv = lv; }
            }

            if (bestBattle != 0) return bestBattle;
            if (bestOther != 0) return bestOther;

            IceLogging.Warning("納品(強化)用に切り替えられるジョブのギアセットが見つかりません。主道具を装備したままでは強化できないため、納品ジョブ以外のギアセットを1つ作ってください", "[Task_Relic]");
            return 0;
        }

        public static bool? CheckJobSwap()
        {
            if (_tempJob != 0)
            {
                if (Player.Job != (Job)_tempJob)
                {
                    if (EzThrottler.Throttle("Swapping jobs", 1000))
                    {
                        IceLogging.Verbose($"Telling the game to swap you to jobID: {_tempJob}");
                        GearsetHandler.TaskClassChange((Job)_tempJob);
                    }

                    return false;
                }
                else
                {
                    IceLogging.Verbose("Job swap is complete! Turning in the relic now");

                    return true;
                }
            }
            else if ((uint)Player.Job != Mission_Settings.SelectedJob)
            {
                if (EzThrottler.Throttle("Swapping jobs", 1000))
                {
                    IceLogging.Verbose($"Telling the game to swap you to jobID: {Mission_Settings.SelectedJob}");
                    GearsetHandler.TaskClassChange((Job)Mission_Settings.SelectedJob);
                }
                return false;
            }
            else
            {
                IceLogging.Debug("No swap is necessary/not configured properly. Continuing on");
                return true;
            }
        }

        public static bool? Relic_PathTo()
        {
            string handle = "[Task_Relic: PathTo]";
            var zoneId = Player.Territory;

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Relic, out var npcEntry))
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

        public static bool? TalkToResearchWay()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                IceLogging.Info("Talk to researchway complete");
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Clicking the talk dialog", 100))
                {
                    talk.Click();
                }
            }

            if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Relic, out var npcEntry))
            {
                Utils.TryGetObjectByDataId(npcEntry.NpcId, out var researchNpc);
                if (EzThrottler.Throttle("Interacting with researchingway"))
                {
                    Utils.TargetgameObject(researchNpc);
                    Utils.InteractWithObject(researchNpc);
                }
            }

            return false;
        }

        public static bool? SelectReport()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectIconString>("SelectIconString", out var selectIconString) && selectIconString.IsAddonReady)
            {
                IceLogging.Info("We're onto selecting the class to turnin, woo!");
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting the research one"))
                    selectString.Entries[0].Select();
            }

            return false;
        }

        public static bool? SelectRelicClass()
        {
            Dictionary<uint, bool> jobUnlocked = new()
            {
                [8] = true,
                [9] = true,
                [10] = true,
                [11] = true,
                [12] = true,
                [13] = true,
                [14] = true,
                [15] = true,
                [16] = true,
                [17] = true,
                [18] = true,
            };
            foreach (var jobId in jobUnlocked)
            {
                if (Player.GetLevel((Job)jobId.Key) == 0)
                    jobUnlocked[jobId.Key] = false;
            }

            if (EzThrottler.Throttle("Throttle job unlock message", 1000))
                IceLogging.Debug($"Amount of jobs unlocked: {jobUnlocked.Where(x => x.Value).Count()}");
            uint selectedEntry = 0;
            foreach (var jobId in jobUnlocked)
            {
                if (TurninJob == jobId.Key)
                    break;
                else
                {
                    if (jobId.Value)
                        selectedEntry += 1;
                }
            }


            if (GenericHelpers.TryGetAddonMaster<SelectIconString>("SelectIconString", out var selectIconString) && selectIconString.IsAddonReady)
            {
                if (EzThrottler.Throttle($"Selecting jobId: {TurninJob}"))
                {
                    IceLogging.Debug($"Selecting Entry: {selectedEntry} for job: {TurninJob} to turnin relic");
                    selectIconString.Entries[selectedEntry].Select();
                }
            }
            else if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var selectYesno) && selectYesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting yes for turnin"))
                {
                    IceLogging.Verbose("Selecting yes for the turnin");
                    selectYesno.Yes();
                }
            }
            else if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Clicking the talk dialog", 50))
                {
                    IceLogging.Verbose("Clicking the talk dialog");
                    talk.Click();
                }
            }
            else if (!Player.IsBusy)
            {
                IceLogging.Info("No longer busy talking to researchingway, to we're done");
                if (_tempJob != 0)
                {
                    // 元のジョブへ戻ってから最強装備を行う。Enqueue だと拠点作業(購入/ガンブル/帰還)の後ろに回り、
                    // 一時ジョブのまま拠点作業をしてしまうため、この直後に割り込ませる。
                    P.TaskManager.InsertMulti
                    (
                        new(() => ReturnBackToJob(), "Returning back to the original job", Utils.TaskConfig),
                        new(() => EquipBestGear(), "Equipping best gear after the relic upgrade", Utils.TaskConfig)
                    );
                }
                return true;
            }

            return false;

        }
        // 納品(強化)後の最強装備。Stylist プラグインがあればそれを使い、無ければゲームの「おすすめ装備」で装備してギアセットを更新する。
        public static bool? EquipBestGear()
        {
            var jobId = TurninJob;
            bool useStylist = C.Relic_Stylist && Utils.HasPlugin("Stylist");

            if (useStylist)
            {
                // Stylist に装備させ、少し待ってからギアセットだけ保存する(おすすめ装備で上書きしない)
                if (CosmicHelper.CrafterJobList.Contains(jobId))
                    Task_TurninMission.ExecuteCommand("/stylist crafter");
                else if (CosmicHelper.GatheringJobList.Contains(jobId))
                    Task_TurninMission.ExecuteCommand("/stylist gatherer");
                P.TaskManager.Insert(() => SaveGearsetTask(), "Updating the gearset with the equipped gear", Utils.TaskConfig);
                P.TaskManager.InsertDelay(1000);
                return true;
            }

            P.TaskManager.Insert(() => EquipRecommendedGear(), "Equipping recommended gear", Utils.TaskConfig);
            return true;
        }

        private static int _recommendStep = 0;
        private static long _recommendTick = 0;

        // 現在のギアセット(現在のジョブのもの)を今の装備で更新する。
        // 更新しないと、強化で主道具が別アイテムになった後に再度そのジョブへ切り替えたとき主道具が外れたままになる。
        private static unsafe void SaveCurrentGearset()
        {
            var gearsets = RaptureGearsetModule.Instance();
            if (gearsets == null || gearsets->CurrentGearsetIndex < 0)
                return;
            var current = gearsets->GetGearset(gearsets->CurrentGearsetIndex);
            if (current == null || current->ClassJob != (byte)Player.Job)
                return;
            gearsets->UpdateGearset(gearsets->CurrentGearsetIndex);
            IceLogging.Info($"ギアセット {gearsets->CurrentGearsetIndex + 1} を現在の装備で更新しました", "[Task_Relic]");
        }

        public static bool? SaveGearsetTask()
        {
            if (Player.IsBusy || GenericHelpers.IsOccupied())
                return false;
            SaveCurrentGearset();
            return true;
        }

        // ゲームの「おすすめ装備」(RecommendEquipModule)で現在のジョブの最強装備を着け、ギアセットへ保存する。
        // Stylist プラグインが無い環境向け。SetupForClassJob → (計算完了待ち) → EquipRecommendedGear → ギアセット保存 の順に進める。
        public static unsafe bool? EquipRecommendedGear()
        {
            if (Player.IsBusy || GenericHelpers.IsOccupied())
                return false;

            var module = RecommendEquipModule.Instance();
            long now = Environment.TickCount64;

            switch (_recommendStep)
            {
                case 0:
                    if (module == null) { _recommendStep = 2; _recommendTick = now; return false; }
                    module->SetupForClassJob((byte)Player.Job);
                    _recommendStep = 1;
                    _recommendTick = now;
                    return false;
                case 1:
                    if (module != null && module->IsUpdating) return false;
                    if (now - _recommendTick < 500) return false;
                    if (module != null)
                    {
                        module->EquipRecommendedGear();
                        IceLogging.Info("おすすめ装備で最強装備を行いました", "[Task_Relic]");
                    }
                    _recommendStep = 2;
                    _recommendTick = now;
                    return false;
                default:
                    if (now - _recommendTick < 1000) return false;
                    SaveCurrentGearset();
                    _recommendStep = 0;
                    return true;
            }
        }

        public static bool StylistCheck()
        {
            var jobId = TurninJob;

            if (CosmicHelper.CrafterJobList.Contains(jobId))
            {
                Task_TurninMission.ExecuteCommand("/stylist crafter");
            }
            else if (CosmicHelper.GatheringJobList.Contains(jobId))
            {
                Task_TurninMission.ExecuteCommand("/stylist gatherer");
            }
            P.TaskManager.EnqueueDelay(1000);
            P.TaskManager.Enqueue(() => ReturnBackToJob(), "Returning back to original job", Utils.TaskConfig);

            return true;
        }

        private static int postRelicCounter = 0;

        public static bool? ReturnBackToJob()
        {
            if ((uint)Player.Job == TurninJob)
            {
                IceLogging.Debug("We're back on the proper job, continuing on");

                var delayAmount = C.DelayPostRelic == 0 ? 25 : C.DelayPostRelic;

                if (EzThrottler.Throttle("Add to counter", delayAmount))
                {
                    postRelicCounter += 1;
                }

                if (postRelicCounter >= 2)
                {
                    postRelicCounter = 0;
                    _tempJob = 0;
                    return true;
                }
                else
                    return false;
            }
            else
            {
                if (EzThrottler.Throttle("Swapping jobs", 1000))
                {
                    IceLogging.Verbose($"Telling the game to swap you to jobID: {TurninJob}");
                    GearsetHandler.TaskClassChange((Job)TurninJob);
                }
                return false;
            }
        }
    }
}
