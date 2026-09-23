using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using System.Collections.Generic;
using System.Linq;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using MissionRank = FFXIVClientStructs.FFXIV.Client.Game.WKS.WKSMissionModule.MissionRank;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_TurninMission
    {
        public static uint PreviousMissionId = 0;
        private static bool PathfoundToRed = false;
        private static int PreviousScore = 0;
        private static bool HasInteracted = false;
        private static int TickRate = 0;

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(() => CheckRedAlert(), "Checking for Red Alert Info"),
                    new(() => Mission_TurninV2(), "Turning in the mission to the moon gods", Utils.TaskConfig),
                    new(() => GoldCheck(), "Checking if Gold Check Task needs to be completed"),
                    new(() => CommandCheck(), "Checking for post mission commands"),
                    new(() => JobSwapCheck(), "Checking for necessary job swap")
                );
        }

        public static bool CheckRedAlert()
        {
            string tag = "Red Alert Check";

            var id = CosmicHelper.CurrentLunarMission;
            if (CosmicHelper.SheetMissionDict.TryGetValue(id, out var sheetInfo))
            {
                if (sheetInfo.IsCritical)
                {
                    IceLogging.Verbose("Critical mission was found, checking for location info", tag);

                    if (GatheringUtil.CriticalSpots.TryGetValue(sheetInfo.Critical_MapKey, out var criticalInfo) && criticalInfo.WorldCords != Vector3.Zero)
                    {
                        if (Player.DistanceTo(criticalInfo.WorldCords) < 75)
                        {
                            IceLogging.Verbose("We're close enough to the base location that we don't need to do any fancy traveling, going to check if we need to interact", tag);
                            P.TaskManager.Insert(() => RedAlert_CloseToTurnin(), "Checking to make sure we're close enough");
                        }
                        else
                        {
                            IceLogging.Verbose("We're far enough away that we need to consider taking the npc for getting there, so going to do so");
                            P.TaskManager.Insert(() => Task_NavmeshMove.Enqueue_RedAlertNavmesh(criticalInfo.WorldCords, distance: 75, missionId: id), "Checking to make sure we're close enough");
                        }
                    }
                    else
                    {
                        // 納品地点の座標が未登録(Auxesia 等)。その惑星に赤警報NPC(レフレダ)が登録されていれば、
                        // 職業名でメニューを選んでワープする動的方式で任務地へ向かう。
                        if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.RedAlert, out _))
                        {
                            if (EzThrottler.Throttle("Auxesia RA route msg", 2000))
                                IceLogging.Info("緊急ミッション(座標未登録)。レフレダで職業に応じた任務地を選んでワープします", tag);
                            P.TaskManager.Insert(() => RedAlert_AuxesiaTravel(id), "RedAlert: レフレダ職業選択→ワープ");
                        }
                        else
                        {
                            if (EzThrottler.Throttle("No recorded site: 2000"))
                                IceLogging.Error("There is currently not a preset destination that we have recorded, so this means it's a new red alert. Please give me time to add this", tag);

                            P.TaskManager.Insert(() => RedAlert_CloseToTurnin(), "Checking to make sure we have a turnin that is close");
                        }
                    }
                }
                else
                {
                    IceLogging.Info("We don't need to worry about a turnin point, so we're good. Continuing on");
                }
            }
            else
            {
                IceLogging.Info($"Somehow we found a mission that doesn't exist? Please report this: {id}", tag);
            }

            return true;
        }
        public static bool RedAlert_CloseToTurnin()
        {
            string tag = "Red Alert: Traveling to turnin";

            if (Utils.TryGetObjectCollectionPoint() is { } collectionPoint)
            {
                if (!Task_NavmeshMove.Task_NavTo(collectionPoint.Position, false, 4).Value)
                {
                    return false;
                }
                else
                {
                    if (P.Navmesh.IsRunning())
                    {
                        if (EzThrottler.Throttle("Telling navmesh to stop"))
                            P.Navmesh.Stop();

                        return false;
                    }

                    IceLogging.Info("We've reached a point where we can turnin, doing so", tag);
                    return true;
                }
            }
            else
            {
                if (EzThrottler.Throttle("Null collection point found"))
                    IceLogging.Verbose("You're not close to the collection point, we need to get closer", tag);
            }

            return false;
        }
        // ジョブID(8〜18) → ClassJob シートの名前(日本語クライアントなら「鍛冶師」等)。レフレダのメニュー選択肢と照合する。
        private static string GetJobJpName(uint jobId)
        {
            try
            {
                var cj = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(jobId);
                return cj?.Name.ExtractText() ?? "";
            }
            catch { return ""; }
        }

        // 納品地点の座標が未登録の惑星(Auxesia 等)向けの緊急ミッション移動。
        // 座標をハードコードせず、レフレダのメニューで「ミッションの職業名を含む選択肢」を選んでワープする。
        // ワープ後は物資集積所(納品オブジェクト)が見えるので true を返し、後続の納品/採取処理に任せる。
        private static DateTime _auxesiaTravelStart = DateTime.MinValue;
        private static DateTime _auxesiaNoMatchSince = DateTime.MinValue;
        private const double AuxesiaTravelTimeoutSeconds = 120.0; // NPC に辿り着けない等で無限に粘らない
        private const double AuxesiaNoMatchTimeoutSeconds = 8.0;  // 職業名に一致する選択肢が無い時にメニューを閉じるまでの猶予
        public static bool? RedAlert_AuxesiaTravel(uint missionId)
        {
            string tag = "[RedAlert Auxesia]";
            if (!NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.RedAlert, out var lefleda))
                return true; // レフレダ未登録 → 何もできないので後続へ

            // ワープのロード中は Player が取れず、距離計算で落ちるので待つ
            if (!Player.Available)
                return false;

            if (_auxesiaTravelStart == DateTime.MinValue)
                _auxesiaTravelStart = DateTime.Now;
            if ((DateTime.Now - _auxesiaTravelStart).TotalSeconds > AuxesiaTravelTimeoutSeconds)
            {
                IceLogging.Warning($"レフレダ経由の任務地移動が {AuxesiaTravelTimeoutSeconds:F0}秒以内に完了しないため打ち切ります", tag);
                _auxesiaTravelStart = DateTime.MinValue;
                _auxesiaNoMatchSince = DateTime.MinValue;
                return true;
            }

            // ワープ後: 納品オブジェクト(物資集積所)が出現していれば到着
            if (Utils.TryGetObjectCollectionPoint() != null)
            {
                _auxesiaTravelStart = DateTime.MinValue;
                _auxesiaNoMatchSince = DateTime.MinValue;
                return true;
            }

            // レフレダのメニュー(SelectString): ミッションの職業名を含む選択肢を選ぶ
            if (GenericHelpers.TryGetAddonMaster<SelectString>(out var ss) && ss.IsAddonReady)
            {
                string jobName = "";
                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mi) && mi.Jobs.Count > 0)
                    jobName = GetJobJpName(mi.Jobs[0]);

                var entries = new List<string>();
                foreach (var e in ss.Entries) entries.Add(e.Text ?? "");
                int pick = -1;
                if (!string.IsNullOrEmpty(jobName))
                    for (int i = 0; i < entries.Count; i++)
                        if (entries[i].Contains(jobName)) { pick = i; break; }

                if (pick >= 0)
                {
                    _auxesiaNoMatchSince = DateTime.MinValue;
                    if (EzThrottler.Throttle("Auxesia RA select", 500))
                        ss.Entries[pick].Select();
                    return false;
                }

                // 一致する選択肢が無い(別NPCのメニュー/職業名の表記違い)。しばらく待っても無ければメニューを閉じて後続へ委ねる。
                if (_auxesiaNoMatchSince == DateTime.MinValue)
                    _auxesiaNoMatchSince = DateTime.Now;
                if (EzThrottler.Throttle("Auxesia RA nomatch", 2000))
                    IceLogging.Info($"緊急ミッション mission={missionId} job='{jobName}' に一致する選択肢が見つかりません: [{string.Join(" | ", entries)}]", tag);
                if ((DateTime.Now - _auxesiaNoMatchSince).TotalSeconds > AuxesiaNoMatchTimeoutSeconds)
                {
                    IceLogging.Error($"レフレダのメニューに職業 '{jobName}' の選択肢が無いため、メニューを閉じて通常の納品確認へ進みます", tag);
                    GenericHandlers.FireCallback("SelectString", true, -1);
                    _auxesiaTravelStart = DateTime.MinValue;
                    _auxesiaNoMatchSince = DateTime.MinValue;
                    return true;
                }
                return false;
            }
            // 確認ダイアログ → はい
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>(out var yn) && yn.IsAddonReady)
            {
                if (EzThrottler.Throttle("Auxesia RA yes", 300)) yn.Yes();
                return false;
            }
            // 会話 → クリック送り
            if (GenericHelpers.TryGetAddonMaster<Talk>(out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Auxesia RA talk", 100)) talk.Click();
                return false;
            }

            // メニューが出ていない → レフレダへ移動して話しかける
            if (Player.DistanceTo(lefleda.Location_Circle) < 5)
            {
                if (Utils.TryGetObjectByDataId(lefleda.NpcId, out var npc) && npc != null)
                {
                    if (Player.Mounted) { Utils.Dismount(); return false; }
                    if (EzThrottler.Throttle("Auxesia RA interact", 500))
                    {
                        Utils.TargetgameObject(npc);
                        Utils.InteractWithObject(npc);
                    }
                }
                return false;
            }

            Task_NavmeshMove.Task_NavTo(lefleda.Location_Circle, false, 3.0f);
            return false;
        }

        public static bool? Mission_TurninV2()
        {
            string tag = "[Mission Turnin]";
            var id = CosmicHelper.CurrentLunarMission;

            if (id == 0)
            {
                CosmicHelper.Task_UpdateRelicMissionInfo();

                PathfoundToRed = false;
                HasInteracted = false;

                // Complete the timer and get duration
                var duration = P.MissionTimer.CompleteMission();

                if (P.AutoHook.Installed)
                {
                    P.AutoHook.DeleteAllAnonymousPresets();
                }

                UpdateScoreInfo();
                Mission_Settings.TurninState = TurninState.None;

                CosmicHelper.Task_UpdateRelicMissionInfo();

                if (Mission_Settings.StopAfterCurrent)
                {
                    IceLogging.Debug($"Stop after current was enabled. Stopping now", "[Task Turnin]");
                    SchedulerMain.State = IceState.Idle;
                    return true;
                }
                else
                {
                    IceLogging.Debug($"Stop after current wasn't enabled. Grabbing another mission", "[Task Turnin]");
                    SchedulerMain.State = IceState.Start;
                    return true;
                }
            }
            else
            {
                if (CosmicHelper.SheetMissionDict.TryGetValue(id, out var sheetInfo))
                {
                    PreviousMissionId = id;

                    if (sheetInfo.IsCritical)
                    {
                        Mission_Settings.TurninState = TurninState.Critical;
                        if (!PathfoundToRed)
                        {
                            PathfoundToRed = true;
                            P.Navmesh.Stop();
                        }

                        var collectionPoint = Utils.TryGetObjectCollectionPoint();
                        if (!Task_NavmeshMove.Task_NavTo(collectionPoint.Position, false, 4).Value)
                        {
                            return false;
                        }

                        if (EzThrottler.Throttle("Log Throttle", 1000))
                        {
                            IceLogging.Debug("Attempting to turnin/chekcing if we need to navmesh stop");
                        }

                        if (P.Navmesh.IsRunning())
                        {
                            if (EzThrottler.Throttle("Telling navmesh to stop"))
                                P.Navmesh.Stop();

                            return false;
                        }

                        if (!HasInteracted)
                        {
                            if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.OccupiedInEvent])
                            {
                                HasInteracted = true;
                            }
                            else
                            {
                                if (EzThrottler.Throttle("Interacting with thing", 500))
                                {
                                    Utils.TargetgameObject(collectionPoint);
                                    Utils.InteractWithObject(collectionPoint);
                                }
                            }
                        }
                        else
                        {
                            if (EzThrottler.Throttle("Telling it to wait this much before turning it off", 6000))
                            {
                                TickRate += 1;
                            }
                            if (TickRate > 1)
                            {
                                TickRate = 0;
                                HasInteracted = false;
                            }
                        }
                    }
                    else
                    {
                        if ((uint)Player.Job == 18 && Svc.Condition[ConditionFlag.Gathering])
                        {
                            if (EzThrottler.Throttle("Stop fishing so we can turn in this mission!", 2000))
                                Task_DualClass.StopFishing();

                            return false;
                        }
                        if (GenericHelpers.TryGetAddonMaster<Gathering>("Gathering", out var gather) && gather.IsAddonReady)
                        {
                            if (EzThrottler.Throttle("Closing the gathering window"))
                                GenericHandlers.FireCallback("Gathering", true, -1);

                            return false;
                        }
                        else if (GenericHelpers.TryGetAddonMaster<GatheringMasterpiece>("GatheringMasterpiece", out var gathMasterpiece) && gathMasterpiece.IsAddonReady)
                        {
                            if (EzThrottler.Throttle("Closing the collectable menu"))
                                GenericHandlers.FireCallback("GatheringMasterpiece", true, -1);

                            return false;
                        }
                        else if (GenericHelpers.TryGetAddonMaster<WKSRecipeNotebook>("WKSRecipeNotebook", out var WksRecipe) && WksRecipe.IsAddonReady)
                        {
                            if (EzThrottler.Throttle("Closing the crafting menu"))
                                GenericHandlers.FireCallback("WKSRecipeNotebook", true, -1);

                            return false;
                        }

                        if (EzThrottler.Throttle("Setting Turnin State", 2000))
                        {
                            var rank = Task_CheckScore.CurrentRank();
                            Mission_Settings.TurninState = (int)rank switch
                            {
                                6 => TurninState.Master_Score,
                                3 => TurninState.Gold,
                                2 => TurninState.Silver,
                                _ => TurninState.Bronze,
                            };
                        }

                        if (Player.IsBusy)
                            return false;

                        if (EzThrottler.Throttle("Report Mission"))
                        {
                            ReportMission();
                        }
                    }
                }
            }

            return false;
        }
        private static unsafe void ReportMission()
        {
            if (EzThrottler.Throttle("Previous Score Set"))
                PreviousScore = ScoreCheck();

            var WKSInstance = WKSManager.Instance();
            WKSInstance->MissionModule->ReportMission();
        }
        public static bool? JobSwapCheck()
        {
            if (CosmicHelper.SheetMissionDict[PreviousMissionId].Jobs.Count == 2)
            {
                if (Player.Job != (Job)Mission_Settings.SelectedJob && Mission_Settings.SelectedJob != 0)
                {
                    if (EzThrottler.Throttle("Swapping to crafter job", 1000))
                        GearsetHandler.TaskClassChange((Job)Mission_Settings.SelectedJob);

                    return false;
                }
                else
                {
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        public static unsafe bool? GoldCheck()
        {
            var managerPtr = WKSManager.Instance();
            if (managerPtr == null) return false;

            var isGold = managerPtr->IsMissionGolded(PreviousMissionId);

            // ミッション境界では PreviousMissionId==0 のことがあり、直接添字だと SheetMissionDict[0] が
            // KeyNotFoundException を投げて GoldCheck が完了できず、毎tick再試行でエラー多発かつ
            // 下の状態遷移に進めずスタックする。TryGetValue にして無ければ Gold掃除を飛ばす。
            CosmicHelper.SheetMissionDict.TryGetValue(PreviousMissionId, out var sheetInfo);

            if (C.RemoveAfterGold && isGold && sheetInfo != null)
            {
                List<uint> seqMissions = new();
                foreach (var mission in sheetInfo.SequenceMissions_Next)
                    seqMissions.Add(mission);
                foreach (var mission in sheetInfo.SequenceMissions_Previous)
                    seqMissions.Add(mission);
                seqMissions.Add(PreviousMissionId);

                if (seqMissions.All(x => managerPtr->IsMissionGolded(x)))
                {
                    foreach (var mission in seqMissions)
                    {
                        if (CosmicHelper.SheetMissionDict.TryGetValue(mission, out var missionInfo))
                        {
                            bool special = missionInfo.IsCritical || missionInfo.IsProvisional;

                            if (!special && C.KeepARanks)
                                continue;

                            if (C.MissionConfig.TryGetValue(mission, out var mc))
                                mc.Enabled = false;
                        }
                    }
                    C.Save();
                }
            }
            if (C.RemoveAfterGold && !isGold && sheetInfo != null)
            {
                foreach (var prevMission in sheetInfo.SequenceMissions_Previous)
                    if (C.MissionConfig.TryGetValue(prevMission, out var mc2))
                        mc2.Enabled = true;

                C.Save();
            }

            IceLogging.Info("Gold Check is complete, and checking to see what state we need to be in post cleanup");
            if (Mission_Settings.StopAfterCurrent)
            {
                IceLogging.Info("We're stopping after this mission", "[Gold Check Task]");
                Mission_Settings.StopAfterCurrent = false;
                SchedulerMain.State = IceState.Idle;

                if (C.PlaySoundAlert)
                    _ = SoundPlayer.PlaySoundAsync();
            }
            else
            {
                IceLogging.Info("We're continuing after this mission", "[Gold Check Task]");
                SchedulerMain.State = IceState.Start;
            }

            return true;
        }
        public static unsafe bool? CommandCheck()
        {
            string tag = "Turnin Mission: Command Check";

            // レベリング中はミッションごとに最強装備を行う(Stylist があれば /stylist、無ければゲームのおすすめ装備)。
            // レベルが上がって購入済みの上位装備が着けられるようになった時点で自動で更新するため。
            if (Mission_Settings.Mode == ModeSelect.LevelMode && C.LevelingGear_AutoEquipBest)
            {
                IceLogging.Info("Leveling: equipping the best gear after the mission", tag);
                Task_RelicTurnin.EnqueueEquipBestGear();
            }

            foreach (var task in C.PostMissionCommands)
            {
                IceLogging.Info($"Queueing up the following command:\n" +
                    $"{task.command}\n" +
                    $"Delay: {task.Delay}", tag);
                P.TaskManager.Enqueue(() => ExecuteCommand(task.command));
                if (task.Delay > 0)
                    P.TaskManager.EnqueueDelay(task.Delay);
            }
            return true;
        }
        public static bool? ExecuteCommand(string command)
        {
            Svc.Commands.ProcessCommand(command);
            IceLogging.Info($"Command has been processed: {command}", "Turnin Mission: Execute Command");
            return true;
        }
        public static unsafe int ScoreCheck()
        {
            var wksManager = WKSManager.Instance();
            if (wksManager == null || wksManager->ResearchModule == null || !wksManager->ResearchModule->IsLoaded)
                return 0;

            var scores = wksManager->State.Scores;
            return scores[(int)(uint)Player.Job - 8];
        }
        public static void UpdateScoreInfo()
        {
            var multiplier = 1;
            var turnin = Mission_Settings.TurninState;
            if (turnin == TurninState.Gold)
                multiplier = 5;
            else if (turnin == TurninState.Silver)
                multiplier = 4;

            var scoreDifference = (ScoreCheck() - PreviousScore);
            if (scoreDifference > 0)
            {
                scoreDifference = scoreDifference / multiplier;
                IceLogging.Debug($"Base Mission score is: {scoreDifference}");
                C.ScoreKeeper[PreviousMissionId] = (uint)scoreDifference;

                if (scoreDifference < 1000)
                {
                    if (CosmicHelper.SheetMissionDict.TryGetValue(PreviousMissionId, out var missionInfo))
                    {
                        missionInfo.ClassScore = (uint)scoreDifference;
                    }
                    C.Save();
                }
            }
            PreviousScore = 0;
        }
        public static bool? ClearAllPostTask()
        {
            P.TaskManager.Tasks.Clear();
            IceLogging.Info("All task post turning in mission have been cleared. We should have a clean slate now");
            return true;
        }
    }
}
