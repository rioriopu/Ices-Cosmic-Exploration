using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using ICE.Utilities.Cosmic_Helper;
using MissionRank = FFXIVClientStructs.FFXIV.Client.Game.WKS.WKSMissionModule.MissionRank;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_AbandonMission
    {
        public static void Enqueue()
        {
            P.TaskManager.Enqueue(() => AbandonMission(), "Abandoning the current mission");
            P.TaskManager.Enqueue(() => Task_TurninMission.GoldCheck(), "Checking post mission state + gold state condition");
            P.TaskManager.Enqueue(() => Task_TurninMission.CommandCheck(), "Checking for post mission commands");
            if (C.DelayGrabMission)
                P.TaskManager.EnqueueDelay(C.DelayIncrease);
        }

        public static bool WasAbandoned = false;
        public static bool ForceAbandon = false;

        // ---- 放棄/報告の見張り ----
        // ゲームへの放棄/報告は「製作スタンス・製作画面・レシピ帳が残っている」「別の操作中」だと黙って拒否され、
        // チャットに「現在の状態では実行できません」が出るだけで終わる。以前はこの呼び出しを毎フレーム無制限に繰り返していたため、
        // 実機(2026-09-26 19:05〜19:28)で 75,786 回連続で拒否され、エラーが延々とチャットに流れ続けた。
        // 今は「先に製作の後始末をする」「呼び出しは 1.5 秒間隔」「90 秒で諦めて停止する」の 3 つで守る。
        private static readonly TimeSpan NativeCallInterval = TimeSpan.FromMilliseconds(1500);
        private const double GiveUpSeconds = 90;
        private static DateTime _attemptSince = DateTime.MinValue;
        private static DateTime _nextNativeCall = DateTime.MinValue;
        private static int _nativeCalls = 0;

        /// <summary>放棄の見張りを最初から数え直す(Start 時や成功時)</summary>
        public static void ResetAttempt()
        {
            _attemptSince = DateTime.MinValue;
            _nextNativeCall = DateTime.MinValue;
            _nativeCalls = 0;
        }

        public static bool? AbandonMission()
        {
            string tag = "Abandon Mission";

            if (CosmicHelper.CurrentLunarMission == 0)
            {
                if (!ForceAbandon)
                {
                    if (Mission_Settings.TurninState > TurninState.None)
                        P.MissionTimer.CompleteMission();
                    else
                        P.MissionTimer.AbandonMission();

                    Mission_Settings.TurninState = TurninState.None;

                    CosmicHelper.Task_UpdateRelicMissionInfo();
                }

                ForceAbandon = false;
                WasAbandoned = false;

                if (P.AutoHook.Installed)
                    P.AutoHook.DeleteAllAnonymousPresets();

                if (_nativeCalls > 0)
                    IceLogging.Info($"ミッションの放棄/報告が通りました({_nativeCalls} 回目の呼び出し、{(DateTime.Now - _attemptSince).TotalSeconds:F0} 秒)", tag);
                ResetAttempt();

                IceLogging.Info("Current mission is 0, checking to see where we need to be now", tag);
                return true;
            }
            else
            {
                if (_attemptSince == DateTime.MinValue)
                    _attemptSince = DateTime.Now;

                Task_TurninMission.PreviousMissionId = CosmicHelper.CurrentLunarMission;
                if (EzThrottler.Throttle("Score Check Update"))
                    Task_TurninMission.ScoreCheck();

                if (Player.Job == (Job)18 && Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Gathering])
                {
                    if (EzThrottler.Throttle("Stop fishing so we can turn in this mission!", 2000))
                        Task_DualClass.StopFishing();

                    return false;
                }

                // 長すぎる場合は諦めて止める。同じ呼び出しを繰り返しても結果は変わらず、エラーが流れ続けるだけ
                var elapsed = (DateTime.Now - _attemptSince).TotalSeconds;
                if (elapsed > GiveUpSeconds)
                {
                    IceLogging.Error($"{elapsed:F0} 秒間ミッションを放棄/報告できませんでした({_nativeCalls} 回試行)。ICE を停止します"
                                   + $" [Crafting={Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting]}, Preparing={Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft]},"
                                   + $" Occupied={GenericHelpers.IsOccupied()}, ArtisanBusy={SafeArtisanBusy()}]", tag);
                    IceLogging.ChatError(Loc.T("ICE could not abandon or report the current mission for 90 seconds, so it stopped. Please abandon the mission manually and press Start again."), "[I.C.E.]");
                    ResetAttempt();
                    P.TaskManager.Tasks.Clear();
                    SchedulerMain.State = IceState.Idle;
                    return true;
                }

                // 製作の状態が残っている間は、放棄/報告を投げてもゲームに拒否される。先に後始末をする
                if (Task_Craft.IsCraftingStateActive())
                {
                    if (EzThrottler.Throttle("Abandon: leaving craft state", 3000))
                        IceLogging.Info("製作スタンス/製作画面/レシピ帳が残っているため、放棄の前に製作を終了します", tag);
                    Task_Craft.TryLeaveCraftingState(tag);
                    return false;
                }

                // 別の操作中(NPC との会話、エリア移動など)も拒否される
                if (GenericHelpers.IsOccupied())
                    return false;

                // 呼び出しは間隔を空ける(毎フレーム投げてもゲーム側の処理は 1 回分しか進まない)
                if (DateTime.Now < _nextNativeCall)
                    return false;
                _nextNativeCall = DateTime.Now + NativeCallInterval;
                _nativeCalls++;

                var rank = Task_CheckScore.CurrentRank();
                if (rank > MissionRank.None)
                {
                    if (rank != MissionRank.Failed)
                    {
                        Mission_Settings.TurninState = rank switch
                        {
                            MissionRank.Gold => TurninState.Gold,
                            MissionRank.Silver => TurninState.Silver,
                            MissionRank.Bronze => TurninState.Bronze,
                            _ => TurninState.Bronze,
                        };
                    }

                    IceLogging.Debug($"Reporting the mission ({_nativeCalls})", tag);
                    ReportMissionInstance();
                    WasAbandoned = false;
                    return false;
                }
                else
                {
                    IceLogging.Debug($"Abandoning the mission ({_nativeCalls})", tag);
                    if (_nativeCalls > 1 && _nativeCalls % 5 == 0)
                        IceLogging.Warning($"ミッションの放棄がまだ通っていません({_nativeCalls} 回、{elapsed:F0} 秒)。ゲーム側で拒否されている可能性があります", tag);
                    AbandonMissionInstance();
                    WasAbandoned = true;
                }
            }

            return false;
        }

        private static bool SafeArtisanBusy()
        {
            try { return P.Artisan.IsBusy != null && P.Artisan.IsBusy(); } catch { return false; }
        }

        private static unsafe void AbandonMissionInstance()
        {
            var WKSInstance = WKSManager.Instance();
            // エリア遷移中などで取得できないフレームがあるため、null なら何もしない(次tickで再試行される)。
            if (WKSInstance == null || WKSInstance->MissionModule == null)
                return;
            WKSInstance->MissionModule->AbandonMission();
        }

        private static unsafe void ReportMissionInstance()
        {
            var WKSInstance = WKSManager.Instance();
            if (WKSInstance == null || WKSInstance->MissionModule == null)
                return;
            WKSInstance->MissionModule->ReportMission();
        }

        private static string NormalizeWhitespace(string text)
        {
            return text.Trim()
                       .Replace(' ', ' ')  // Non-breaking space to regular space
                       .Replace(' ', ' ')  // Thin space to regular space
                       .Replace(' ', ' ')  // Narrow no-break space to regular space
                       .Replace('　', ' '); // Ideographic space to regular space
        }
    }
}
