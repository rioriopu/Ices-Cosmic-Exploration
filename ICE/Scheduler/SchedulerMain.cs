using ECommons.Automation.NeoTaskManager;
using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using static ICE.Enums.IceState;

namespace ICE.Scheduler
{
    internal static unsafe class SchedulerMain
    {
        internal static bool EnablePlugin()
        {
            State = Start;
            IceLogging.Info($"Setting State to: {State} / Enabling Plugin");
            Mission_Settings.SelectedJob = (uint)Player.Job;
            IceLogging.Info($"Player starting job upon pressing the start: {Mission_Settings.SelectedJob}");
            GenericManager.StorePandoraStates();
            return true;
        }
        internal static bool DisablePlugin()
        {
            IceLogging.Debug("Stopping the plugin state", "[Schedular - Disable Plugin]");
            P.TaskManager.Abort();
            State = IceState.Idle;
            GenericManager.RestorePandoraStates();
            if (P.Navmesh.Installed)
            {
                if (P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
            }

            return true;
        }

        // Debug only settings
        internal static bool DebugOOMMain = false;
        internal static bool DebugOOMSub = false;

        internal static IceState State = Idle;
        internal static MissionAttributes MissionState = MissionAttributes.None;

        // ジョブ手動切替の追従用デバウンス
        private static long _jobSwitchSeenTick = 0;

        /// <summary>
        /// ミッション遂行中にユーザーがゲーム内でCosmic採取/制作ジョブを手動で切り替えたら、現ミッションを破棄(または報告)し、
        /// SelectedJobを新ジョブへ追従させて、新ジョブのミッションを受注し直す。
        /// botが受注時に切り替えるジョブ(Mission_ChangeJob)とは区別するため、「現ジョブが現ミッションの対応ジョブに
        /// 含まれない」ことを手動切替の判定とする(受注後はPlayer.Jobがミッションのジョブと一致しているため)。
        /// </summary>
        private static void CheckManualJobSwitch()
        {
            // 作業状態かつミッション受注中のみ対象(GrabMission中はMission_ChangeJobの一時切替と紛れるため除外)。
            if (State is not (ExecutingMission or Gather or Craft or Fish)) { _jobSwitchSeenTick = 0; return; }
            if (!Player.Available || CosmicHelper.CurrentLunarMission == 0) { _jobSwitchSeenTick = 0; return; }
            // アジェンダモードはジョブをモード側が管理するので追従対象外
            if (Mission_Settings.Mode == Enums.ModeSelect.AgendaMode) { _jobSwitchSeenTick = 0; return; }

            uint job = (uint)Player.Job;
            bool isCosmicJob = CosmicHelper.CrafterJobList.Contains(job) || CosmicHelper.GatheringJobList.Contains(job);
            if (!isCosmicJob) { _jobSwitchSeenTick = 0; return; }

            // 現ジョブが現ミッションの対応ジョブに含まれる = 正常(botの受注時切替)。含まれない = ユーザーの手動切替。
            bool jobInMission = CosmicHelper.SheetMissionDict.TryGetValue(CosmicHelper.CurrentLunarMission, out var mi)
                                && mi.Jobs.Contains(job);
            if (jobInMission) { _jobSwitchSeenTick = 0; return; }

            // デバウンス(2秒): ジョブチェンジ直後やbotの一時切替での誤検知を避ける
            if (_jobSwitchSeenTick == 0) { _jobSwitchSeenTick = Environment.TickCount64; return; }
            if (Environment.TickCount64 - _jobSwitchSeenTick < 2000) return;
            _jobSwitchSeenTick = 0;

            IceLogging.Info($"ジョブ手動切替を検知(job={job})。現ミッションを破棄し、SelectedJobを{job}へ追従して新ジョブのミッションを受注し直します", "[JobSwitch]");
            Mission_Settings.SelectedJob = job;
            P.TaskManager.Tasks.Clear();
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            // ランク未達なら放棄、ランク到達済みなら報告(Task_AbandonMission内で判定)。完了後 GoldCheck が State=Start に戻し、
            // 更新済みのSelectedJobで新ジョブのミッションが選ばれる。
            Task_AbandonMission.ForceAbandon = true;
            State = AbandonMission;
        }

        internal static void Tick()
        {
            CheckManualJobSwitch();

            if (P.TaskManager.NumQueuedTasks == 0 && State != Idle)
            {
                switch (State)
                {
                    case Gambling: Task_Gamba.Enqueue(); break;
                    case Start: Task_CheckState.Enqueue(); break;
                    case Spiritbond: Task_Spiritbond.Enqueue(); break;
                    case Repair: Task_Repair.Enqueue(); break;
                    case HubReturn: Task_HubActivities.Enqueue(); break;
                    case GrabMission: Task_CheckMissions.Enqueue(); break;
                    case AbandonMission: Task_AbandonMission.Enqueue(); break;
                    case ExecutingMission: Task_ExecuteMission.Enqueue(); break;
                    case ScoreCheck: Task_CheckScore.Enqueue(); break;
                    case TurninMission: Task_TurninMission.Enqueue(); break;
                    case Craft: Task_Craft.Enqueue(); break;
                    case Gather: Task_Gather.Enqueue(); break;
                    case Fish: Task_Fishing.Enqueue(); break;
                    case DualClass: Task_DualClass.Enqueue(); break;
                    case ManualMode: Task_Manual.Enqueue(); break;
                    case ArtifactSearch: Task_ArtifactSearch.Enqueue_DroneCheck(); break;
                    default: DisablePlugin(); break;
                }
            }
        }
    }
}