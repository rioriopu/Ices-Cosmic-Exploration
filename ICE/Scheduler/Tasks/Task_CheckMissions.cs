using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using ICE.Ui.Debug_Tabs.Debug_Ui;
using ICE.Utilities.GatheringHelper;
using ICE.Utilities.GatheringHelper.RouteLoader;
using System.Collections.Generic;
using System.Linq;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_CheckMissions
    {
        public enum MissionKind
        {
            Critical,
            Weather,
            Timed,
            Sequence,
            Ex,
            Master,
            A,
            B,
            C,
            D,
            Unknown,
        }

        private static Dictionary<MissionKind, List<uint>> MissionLibrary = new()
        {
            [MissionKind.Critical] = new(),
            [MissionKind.Weather] = new(),
            [MissionKind.Timed] = new(),
            [MissionKind.Sequence] = new(),
            [MissionKind.Ex] = new(),
            [MissionKind.Master] = new(),
            [MissionKind.A] = new(),
            [MissionKind.B] = new(),
            [MissionKind.C] = new(),
            [MissionKind.D] = new(),
            [MissionKind.Unknown] = new(),
        };

        private static readonly Random _random = new Random();

        public static void Enqueue()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(() => RefreshMissionLibrary(), "Refreshing the mission library"),
                    new(() => OpenMissionUi(), "Opening Mission Ui"),
                    new(() => CheckTabs(), "Checking tabs for valid missions")
                );
        }
        private static void ReOpenMissionUi(string tag)
        {
            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionUi) && missionUi.IsAddonReady)
                return;

            if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var moonHud) && moonHud.IsAddonReady)
            {
                if (EzThrottler.Throttle("Opening the mission ui"))
                {
                    IceLogging.Info("Opening the moon mission selection hud", tag);
                    moonHud.Mission();
                }
            }
        }

        private static readonly MissionKind[] HuntSpecialMissionKinds =
            [MissionKind.Critical, MissionKind.Weather, MissionKind.Timed, MissionKind.Sequence];

        private static readonly MissionKind[] StandardMissionKinds =
            [MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D];

        private static int EnabledStandardMissionCount()
        {
            var count = 0;
            foreach (var rank in StandardMissionKinds)
                count += MissionLibrary[rank].Count;
            return count;
        }

        /// <summary>
        /// Gold completion grind only: idle-wait when special missions remain ungolded,
        /// none are on the board, and there are no standard missions left to reroll for.
        /// </summary>
        private static bool WaitingForSpecialMissions() =>
            Mission_Settings.Mode == ModeSelect.MissionGoldMode
            && HuntSpecialMissionKinds.Any(kind => MissionLibrary[kind].Count > 0)
            && EnabledStandardMissionCount() == 0;

        private static void EnterWaitForSpecialMissions(string tag)
        {
            if (SchedulerMain.State != IceState.Waiting)
            {
                IceLogging.Info("Gold completion grind: waiting for a timed, weather, or critical mission to appear on the board.", tag);
                SchedulerMain.State = IceState.Waiting;
            }

            CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);
            P.TaskManager.Tasks.Clear();
        }

        public static void EnqueueWaitRecheck()
        {
            P.TaskManager.Enqueue(() => WaitForSpecialMissionRecheck(), "Waiting for special mission availability");
        }

        private static bool? WaitForSpecialMissionRecheck()
        {
            string tag = "[Check Missions: Wait for Special]";

            if (!EzThrottler.Throttle("Recheck special missions", 15_000))
                return false;

            CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);
            IceLogging.Verbose("Rechecking mission board for timed/weather/critical missions", tag);
            SchedulerMain.State = IceState.GrabMission;
            return true;
        }
        private static MissionKind LibraryInfo(KeyValuePair<uint, CosmicHelper.CosmicInfo> mission)
        {
            MissionKind entry = MissionKind.Unknown;
            var attribute = mission.Value.Attributes;
            var rank = mission.Value.Rank;

            if (attribute.HasFlag(MissionAttributes.ProvisionalWeather))
                entry = MissionKind.Weather;
            else if (attribute.HasFlag(MissionAttributes.ProvisionalTimed))
                entry = MissionKind.Timed;
            else if (attribute.HasFlag(MissionAttributes.ProvisionalSequential))
                entry = MissionKind.Sequence;
            else if (attribute.HasFlag(MissionAttributes.Critical))
                entry = MissionKind.Critical;
            else if (mission.Value.IsMaster)
                entry = MissionKind.Master;
            else if (rank != 0)
            {
                entry = rank switch
                {
                    5 => MissionKind.Ex,
                    4 => MissionKind.A,
                    3 => MissionKind.B,
                    2 => MissionKind.C,
                    1 => MissionKind.D,
                    _ => MissionKind.Unknown,
                };
            }

            return entry;
        }
        public static bool? RefreshMissionLibrary()
        {
            string tag = "Task Check Mission: Refresh Mission Library";

            foreach (var entry in MissionLibrary)
            {
                entry.Value.Clear();
            }

            var playerTerritory = Player.Territory.RowId;

            var enabledPerMoon = string.Join("\n",
                CosmicMoonRegistry.All.Select(m =>
                    $"{m.DisplayName} [{m.TerritoryId}] = [{CosmicMoonRegistry.CountEnabledMissions(m.TerritoryId)}]"));

            IceLogging.Info("This is just general message to let me know WHAT planet you're on, and where you have things enabled\n" +
                "If you're not running things that requires these to be enabled, you can ignore this if you're reading this.\n" +
                $"{enabledPerMoon}\n" +
                $"Current TerritoryID: {playerTerritory}");

            var modeSelected = Mission_Settings.Mode;
            foreach (var mission in CosmicHelper.SheetMissionDict)
            {
                if (mission.Value.TerritoryId != Player.Territory.RowId)
                    continue;

                bool provisional = mission.Value.IsProvisional;

                var missionId = mission.Key;

                if (C.MissionConfig.TryGetValue(missionId, out var config))
                {
                    if (modeSelected == ModeSelect.LevelMode && CosmicHelper.QuickLevelList.Contains(mission.Key))
                    {
                        var job = Mission_Settings.SelectedJob;
                        var jobLevel = Player.GetLevel((Job)job);
                        var missionLevel = mission.Value.Level;

                        if (!mission.Value.Jobs.Contains(job))
                            continue;

                        // Short end of it all, making sure to see what tier the player should be doing
                        // Taking the players level and making sure it matches to the tier
                        // 90+ = 90
                        // 50-89 = 50
                        // 10-49 = 10
                        int playerTier = jobLevel >= 90 ? 90 : jobLevel >= 50 ? 50 : 10;

                        if (missionLevel != playerTier)
                            continue;
                        else
                        {
                            MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.RelicMode)
                    {
                        if (provisional)
                            continue;

                        if (mission.Value.IsCritical && !C.Relic_IncludeCriticals)
                            continue;

                        var jobLevel = Math.Min(Player.GetLevel((Job)mission.Value.Jobs.First()), Player.GetLevel((Job)mission.Value.Jobs.Last()));
                        if (jobLevel < mission.Value.Level)
                            continue;

                        if (C.XPRelicOnlyEnabled)
                        {
                            if (config.Enabled && mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                        else
                        {
                            if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.Standard)
                    {
                        if (!config.Enabled)
                            continue;

                        var jobLevel = Math.Min(Player.GetLevel((Job)mission.Value.Jobs.First()), Player.GetLevel((Job)mission.Value.Jobs.Last()));
                        if (jobLevel < mission.Value.Level)
                            continue;

                        if (provisional)
                        {
                            if (C.GrindAllProvisionals)
                            {
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            }
                            else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                            {
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            }
                        }
                        else if (mission.Value.Attributes.HasFlag(MissionAttributes.Critical))
                        {
                            if (C.GrindOffClassRedAlert)
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                        else
                        {
                            if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                    }
                    else if (modeSelected == ModeSelect.MissionGoldMode)
                    {
                        if (MissionGolded(missionId))
                            continue;

                        if (mission.Value.Attributes.HasFlag(MissionAttributes.Critical))
                        {
                            if (C.GrindOffClassRedAlert)
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                        }
                        else if (provisional)
                        {
                            if (C.GrindAllProvisionals || mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                            {
                                if (mission.Value.SequenceMissions_Previous.Count() != 0 || mission.Value.SequenceMissions_Next.Count() != 0)
                                {
                                    foreach (var prevSeqMission in mission.Value.SequenceMissions_Previous)
                                    {
                                        var seqMission = CosmicHelper.SheetMissionDict.Where(x => x.Key == prevSeqMission).FirstOrDefault();
                                        if (!MissionLibrary[LibraryInfo(seqMission)].Contains(prevSeqMission))
                                            MissionLibrary[LibraryInfo(seqMission)].Add(prevSeqMission);
                                    }
                                    foreach (var nextSeqMission in mission.Value.SequenceMissions_Next)
                                    {
                                        var seqMission = CosmicHelper.SheetMissionDict.Where(x => x.Key == nextSeqMission).FirstOrDefault();
                                        if (!MissionLibrary[LibraryInfo(seqMission)].Contains(nextSeqMission))
                                            MissionLibrary[LibraryInfo(seqMission)].Add(nextSeqMission);
                                    }
                                }
                                MissionLibrary[LibraryInfo(mission)].Add(missionId);
                            }
                        }
                        else if (mission.Value.Jobs.Contains(Mission_Settings.SelectedJob))
                            MissionLibrary[LibraryInfo(mission)].Add(missionId);
                    }
                }
                else
                {
                    IceLogging.Error("We're missing a mission from the config, please report back so I can fix this.\n" +
                        $"MissionID: {mission.Key} | Job (First) {mission.Value.Jobs.First()} | Rank: {mission.Value.Rank}");
                }
            }

            if (MissionLibrary.All(x => x.Value.Count == 0))
            {
                if (modeSelected == ModeSelect.RelicMode && C.XPRelicOnlyEnabled)
                {
                    IceLogging.ChatInfo("\"Only selected missions\" is enabled for Relic Grind, but no selected missions match your current job. Please select missions for this job, switch jobs, or disable the option.", "[I.C.E.]");
                    if (C.PlaySoundAlert)
                    {
                        _ = SoundPlayer.PlaySoundAsync();
                    }
                }
                else
                {
                    IceLogging.Verbose("We currently have no viable missions... which is odd. Please make sure you have some enabled, or report back if this is incorrect\n" +
                        $"Config Mode: {C.SelectedMode} | Mode going into this: {Mission_Settings.Mode}", tag);
                }

                SchedulerMain.State = IceState.Idle;
                P.TaskManager.Tasks.Clear();
                return true;
            }
            else
            {
                IceLogging.Verbose("We've reached the end of the mission sorter, going to report back what our current mission counts are at:", tag);
                foreach (var key in MissionLibrary)
                {
                    IceLogging.Verbose($"[{key.Key}] = {key.Value.Count()}", tag);
                }
                IceLogging.Verbose("Going to run the sorter one more time to make sure that the priority is set for all of these (it should but ya never know)", tag);
                foreach (var key in MissionLibrary.Keys.ToList())
                {
                    MissionLibrary[key] = MissionLibrary[key]
                        .OrderBy(x =>
                        {
                            var jobs = CosmicHelper.SheetMissionDict[x].Jobs;
                            var bestIndex = jobs
                                .Select(job => C.JobPrio.IndexOf(job))
                                .Where(i => i >= 0)
                                .DefaultIfEmpty(int.MaxValue)
                                .Min();
                            return bestIndex;
                        })
                        .ToList();
                }

                IceLogging.Verbose($"Mission finder says we have a valid mission list. So we gonna go find one", tag);
                IceLogging.Verbose($"Stardard tab missions job: {Mission_Settings.SelectedJob}");
                return true;
            }
        }
        public static bool? OpenMissionUi()
        {
            string tag = "[Task Check Mission: Open Mission UI]";

            if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talkUi) && talkUi.IsAddonReady)
            {
                if (EzThrottler.Throttle("Closing the talk"))
                {
                    IceLogging.Info("Talk ui was visible, clicking through", tag);
                    talkUi.Click();
                }

                return false;
            }

            if (CosmicHandler.CanQueryMissionsWithoutUi())
            {
                CosmicHandler.EnsureStandardMissionTab(Mission_Settings.SelectedJob);

                if (WaitingForSpecialMissions())
                {
                    IceLogging.Verbose("Mission agent is active — reading the board without opening WKSMission UI", tag);
                    return true;
                }
            }

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var hud) && hud.IsAddonReady)
            {
                IceLogging.Info("The Mission Selection Ui is visible! Continuing on", tag);
                return true;
            }

            ReOpenMissionUi(tag);

            return false;
        }
        public static bool? CheckTabs()
        {
            string tag = "Check Missions: Check Tabs";
            var priority = C.MissionTypePrio;

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var x) && x.IsAddonReady
                || CosmicHandler.CanQueryMissionsWithoutUi())
            {
                foreach (var type in C.MissionTypePrio)
                {
                    switch (type)
                    {
                        case MissionTypes.Critical:
                        {
                            if (MissionLibrary[MissionKind.Critical].Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Critical], type), "Checking Critical tab for missions");
                            }
                            break;
                        }
                        case MissionTypes.Provisional:
                        {
                            List<uint> provisionals = new();
                            foreach (var ProvisionalPrio in C.MissionPrio)
                            {
                                MissionKind key = ProvisionalPrio switch
                                {
                                    ProvisionalTypes.ProvisionalWeather => MissionKind.Weather,
                                    ProvisionalTypes.ProvisionalSequential => MissionKind.Sequence,
                                    ProvisionalTypes.ProvisionalTimed => MissionKind.Timed,
                                    _ => MissionKind.Unknown
                                };

                                if (MissionLibrary.TryGetValue(key, out var missionList))
                                {
                                    foreach (var jobId in C.JobPrio)
                                    {
                                        // Add missions that match this provisional type AND this job
                                        foreach (var missionId in missionList)
                                        {
                                            if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var missionInfo) && missionInfo.Jobs.Contains(jobId) && !provisionals.Contains(missionId))
                                            {
                                                provisionals.Add(missionId);
                                            }
                                        }
                                    }
                                }
                            }
                            if (provisionals.Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(provisionals, type), "Checking Provisional tab for missions");
                            }
                            break;
                        }
                        case MissionTypes.Standard:
                        {
                            var mode = Mission_Settings.Mode;
                            if (mode is ModeSelect.MissionGoldMode)
                            {
                                List<uint> basicMissions = new();
                                List<MissionKind> MissionRanks = new() { MissionKind.D, MissionKind.C, MissionKind.B, MissionKind.A, MissionKind.Ex };
                                foreach (var rank in MissionRanks)
                                {
                                    if (MissionLibrary.TryGetValue(rank, out var missionList))
                                    {
                                        foreach (var mission in missionList)
                                        {
                                            if (!basicMissions.Contains(mission))
                                                basicMissions.Add(mission);
                                        }
                                    }
                                }
                                P.TaskManager.Enqueue(() => CheckMissions(basicMissions, type, Mission_Settings.SelectedJob));
                                /*
                                foreach (var job in C.JobPrio)
                                {
                                    if (job == Mission_Settings.SelectedJob)
                                        continue;
                                    else
                                        P.TaskManager.Enqueue(() => CheckMissions(basicMissions, type, job));
                                }
                                */
                                break;
                            }
                            else
                            {
                                List<uint> basicMissions = new();
                                List<MissionKind> MissionRanks = new() { MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
                                foreach (var rank in MissionRanks)
                                {
                                    if (MissionLibrary.TryGetValue(rank, out var missionList))
                                    {
                                        foreach (var mission in missionList)
                                        {
                                            if (!basicMissions.Contains(mission))
                                                basicMissions.Add(mission);
                                        }
                                    }
                                }
                                P.TaskManager.Enqueue(() => CheckMissions(basicMissions, type), "Checking Basic Mission tab for missions");
                                break;
                            }
                        }
                        case MissionTypes.DroneSearch:
                        {
                            if (C.Cosmodrone_Run && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var hub) && hub.HasCosmodrome)
                            {
                                P.TaskManager.Enqueue(() => Task_ArtifactSearch.RefreshMapInfo(), "Inserting Drone Task");
                            }
                            break;
                        }
                        case MissionTypes.ToolMastery:
                        {
                            if (MissionLibrary[MissionKind.Master].Count > 0)
                            {
                                P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Master], type), "Checking for master missions");
                            }
                            break;
                        }
                    }
                }

                // Tool Mastery missions live in their own in-game tab (no dedicated getter),
                // so handle them explicitly regardless of MissionTypePrio ordering.
                if (MissionLibrary[MissionKind.Master].Count > 0)
                {
                    P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Master], MissionTypes.ToolMastery), "Checking Tool Mastery tab for missions");
                }

                P.TaskManager.Enqueue(() => FindReroll(), "Find mission to reroll for");
            }
            else
            {
                ReOpenMissionUi(tag);
            }
            return true;
        }
        private static bool? CheckMissions(List<uint> missionList, MissionTypes type, uint Goldjob = 0)
        {
            string tag = "[Check Missions: Queue]";
            void LogInfo(uint missionId)
            {
                var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
                bool provisional = sheetInfo.IsProvisional;
                var redAlert = sheetInfo.IsCritical;
                string jobs = string.Join(", ", sheetInfo.Jobs);

                IceLogging.Info($"We found a mission! We're going to exit out of this task and grab the following: \n " +
                    $"[Id] = {missionId}\n" +
                    $"[Selected Job] = {Mission_Settings.SelectedJob}\n" +
                    $"[Mission Job] = {jobs}\n" +
                    $"Red Alert: {redAlert}\n" +
                    $"Provisional: {provisional}", tag);
            }

            if (CosmicHandler.CanQueryMissionsWithoutUi() || (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady))
            {
                var basicMissionList = CosmicHandler.Basic_AvailableMissions();
                // 掲示板には受注レベル未満/ランク未解放のミッションも(ロック表示で)並ぶ。受けられないものは候補から外し、
                // ランク判定(highestRank)も「受けられるミッション」で行う。受注に失敗したミッションも一定時間は除外する。
                var lockedBasic = CosmicHandler.Basic_LockedMissions();
                // ゲームはレベル不足をフラグに出さない(Locked はメニュー表示用、ConditionLocked はサーバ側の条件)ため、
                // 受注レベル(ミッションの Level: 10/50/90/100)を満たさないものも ICE 側で「ロック中」として扱う
                uint filterJob = Goldjob != 0 ? Goldjob : Mission_Settings.SelectedJob;
                int filterLv = Player.GetLevel((Job)filterJob);
                foreach (var id in basicMissionList)
                    if (CosmicHelper.SheetMissionDict.TryGetValue(id, out var si) && si.Level > filterLv)
                        lockedBasic.Add(id);
                var lockedRanks = new HashSet<uint>();
                foreach (var rank in basicMissionList.Select(x => CosmicHelper.SheetMissionDict.TryGetValue(x, out var s) ? s.Rank : 0u).Distinct())
                {
                    var ofRank = basicMissionList.Where(x => CosmicHelper.SheetMissionDict.TryGetValue(x, out var s) && s.Rank == rank).ToList();
                    if (ofRank.Count > 0 && ofRank.All(x => lockedBasic.Contains(x)))
                        lockedRanks.Add(rank);
                }
                if (lockedBasic.Count > 0 && EzThrottler.Throttle("Locked missions log", 10000))
                    IceLogging.Info($"掲示板のロック中ミッション(受注不可): [{string.Join(",", lockedBasic)}] ロック中ランク: [{string.Join(",", lockedRanks.Select(RelicFallback.RankName))}]", tag);
                basicMissionList = basicMissionList.Where(x => !lockedBasic.Contains(x) && !IsUnacceptable(x)).ToList();
                var specialMissionList = CosmicHandler.Provisional_AvailableMissions();
                var criticalMissions = CosmicHandler.Critical_AvailableMissions();
                var masteryMissions = CosmicHandler.Mastery_AvailableMissions();
                var mode = Mission_Settings.Mode;

                var job = Goldjob != 0 ? Goldjob : Mission_Settings.SelectedJob;

                if (CorrectJobTab(job))
                {
                    // 受けられる(ロックされていない)ミッションの最高ランク = 解放済みランク。レリックモードの一時レベリングの復帰判定に使う
                    if (type == MissionTypes.Standard && basicMissionList.Count > 0)
                        RelicFallback.Observe(job, basicMissionList.Max(x => CosmicHelper.SheetMissionDict[x].Rank));

                    if (mode == ModeSelect.LevelMode)
                    {
                        var levelingMission = missionList.FirstOrDefault();
                        IceLogging.Verbose($"Leveling Mission: Job: {Mission_Settings.SelectedJob} | Mission: {levelingMission} | Level: {CosmicHelper.SheetMissionDict[levelingMission].Level}", debugOnly: true);
                        if (basicMissionList.Contains(levelingMission))
                        {
                            LogInfo(levelingMission);
                            Insert_GrabMissionTask(levelingMission);
                            return true;
                        }

                        IceLogging.Verbose($"We seem to have not found the mission. Going to double check to make sure we have the tab unlocked", tag);

                        var highestRank = basicMissionList.Max(x => CosmicHelper.SheetMissionDict[x].Rank);
                        var level = Player.GetLevel((Job)Mission_Settings.SelectedJob);
                        uint missionId = 0;

                        if (level >= 50 && highestRank < 2)
                        {
                            IceLogging.Verbose("We need to unlock the Lv. 50 Missions [C Rank] so we get better exp gains", tag);
                            missionId = basicMissionList
                                .Where(x => CosmicHelper.Unlock_MissionList.Contains(x))
                                .Where(x => CosmicHelper.SheetMissionDict[x].Drank)
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus is CosmicHelper.Status.None)
                                .FirstOrDefault();
                            IceLogging.Verbose($"Lv. 50 Mission: {missionId}", tag);
                        }
                        else if (level >= 90 && highestRank < 3)
                        {
                            IceLogging.Verbose("We need to unlock the Lv. 90 Missions [B Rank] so we get better exp gains", tag);
                            missionId = basicMissionList
                                .Where(x => CosmicHelper.Unlock_MissionList.Contains(x))
                                .Where(x => CosmicHelper.SheetMissionDict[x].CRank)
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus is CosmicHelper.Status.None)
                                .FirstOrDefault();
                            IceLogging.Verbose($"Lv. 90 Mission: {missionId}", tag);
                        }

                        if (missionId != 0)
                        {
                            IceLogging.Verbose("We found a mission that we need to complete for one reason or another, going to queue it up for leveling!", tag);
                            LogInfo(missionId);
                            Insert_GrabMissionTask(missionId);
                            return true;
                        }
                        else
                        {
                            IceLogging.Verbose("For one reason or another, we seem to have reached the bottom. Which either means rerolling for specific mission or just rerolling for unlocking purposes", tag);
                            return true;
                        }
                    }
                    else if (mode == ModeSelect.RelicMode)
                    {
                        var relicInfo = CosmicHelper.Cosmic_ClassInfo();
                        var classInfo = relicInfo[job];

                        var jobLv = Player.GetLevel((Job)job);

                        var urgency = new Dictionary<int, float>();
                        IceLogging.Verbose("Relic mode was enabled. So going to do checks to see what exp we need", tag);
                        IceLogging.Verbose($"Current Stage is the max stage? {classInfo.Stage_Current == classInfo.Stage_Next}", tag);
                        IceLogging.Verbose($"Exp Current Tallies: [Check before finding missions]", tag);
                        foreach (var exp in classInfo.CurrentExp)
                        {
                            IceLogging.Verbose($"Kind: [{exp.Key}] | Current: {exp.Value.Current} / Needed: {exp.Value.Needed} | Max: {exp.Value.Max}", tag);
                            if (classInfo.Stage_Current != classInfo.Stage_Next)
                                urgency[exp.Key] = exp.Value.Needed > 0 ? 1f - (float)exp.Value.Current / exp.Value.Needed : 0f;
                            else
                                urgency[exp.Key] = 1f - (float)exp.Value.Current / exp.Value.Max;
                        }
                        if (urgency.Count() == 0 || urgency.All(x => x.Value <= 0))
                        {
                            IceLogging.Verbose("We seem to be still grinding out relic exp (either by choice or cause someone didn't turnin) so we're going to just assign it to go for maxing exp", tag);
                            foreach (var exp in classInfo.CurrentExp)
                                urgency[exp.Key] = 1f - (float)exp.Value.Current / exp.Value.Max;
                        }
                        
                        if (urgency.All(x => x.Value <= 0))
                        {
                            IceLogging.Verbose("We seem to be completed with the exp, but also, I don't have a mode setup for score farming yet. So setting the last exp value to be 1 so it just grabs a mission", tag);
                            var lastEntry = urgency.LastOrDefault();
                            urgency[lastEntry.Key] = 1;
                        }

                        IceLogging.Verbose("Going to check to see if we need to complete a specific mission...", tag);

                        var highestRank = basicMissionList.Max(x => CosmicHelper.SheetMissionDict[x].Rank);

                        // 必要なコスモデータの種類が、未解放ランク(またはレベル不足)のミッションでしか得られないなら、
                        // そのミッションを受けに行こうとせず、条件を満たすまで一時的にレベリングモードへ切り替える。
                        if (C.SelectedMode == ModeSelect.RelicMode && classInfo.Stage_Current != classInfo.Stage_Next)
                        {
                            var neededTypes = classInfo.CurrentExp
                                .Where(e => e.Value.Needed > 0 && e.Value.Current < e.Value.Needed)
                                .Select(e => e.Key)
                                .ToList();
                            // 候補 = レリックモードのライブラリに残った通常ミッション(緊急/暫定を除く)のうち、掲示板でロック中のランクや
                            // 受注に失敗したものを除いたもの。ここに無いものは選ばれない
                            var selectable = MissionLibrary
                                .Where(kv => kv.Key is MissionKind.D or MissionKind.C or MissionKind.B or MissionKind.A or MissionKind.Ex)
                                .SelectMany(kv => kv.Value)
                                .Where(x => !lockedBasic.Contains(x) && !IsUnacceptable(x)
                                            && !(CosmicHelper.SheetMissionDict.TryGetValue(x, out var sx) && lockedRanks.Contains(sx.Rank)))
                                .ToList();
                            uint reqRank = 0, reqLevel = 0;
                            string blockedTypes = "", detail = "";
                            bool blocked = neededTypes.Count > 0
                                && RelicFallback.IsBlocked(job, jobLv, highestRank, neededTypes, selectable, out reqRank, out reqLevel, out blockedTypes, out detail);
                            IceLogging.Info($"レリック判定 v{P.GetType().Assembly.GetName().Version}: 必要種類=[{string.Join(",", neededTypes.Select(t => CosmicHelper.ExpDictionary.TryGetValue(t, out var n) ? n : t.ToString()))}] Lv{jobLv} 掲示板最高ランク={RelicFallback.RankName(highestRank)} 候補{selectable.Count}件 → {(blocked ? "受注できるミッションが無い → レベリングへ" : "続行")} {detail}", tag);
                            if (blocked)
                            {
                                // レベルもランクも満たしているのに候補に入らない(受注失敗で除外中、設定で無効 等)ならレベリングでは解決しない。
                                // 往復ループにせず、理由を通知して停止する
                                if (jobLv >= reqLevel && highestRank >= reqRank)
                                {
                                    IceLogging.ChatError(Task_BuyLevelingGear.IsJapanese
                                        ? $"レリックモード: コスモデータ{blockedTypes}を得られるミッションは Lv/ランクの条件を満たしていますが候補に入っていません（受注に失敗して除外中か、設定で無効）。設定とミッション一覧を確認してください。停止します"
                                        : $"Relic mode: missions giving {blockedTypes} meet the level/rank requirement but are not selectable (accept failed or disabled). Check settings; stopping", "[I.C.E.]");
                                    SchedulerMain.State = IceState.Idle;
                                    P.TaskManager.Tasks.Clear();
                                    return true;
                                }
                                IceLogging.Info($"レリックモード: 必要なコスモデータ{blockedTypes}を得られるミッションは{RelicFallback.RankName(reqRank)}クラス(Lv{reqLevel})以上で、現在は Lv{jobLv}/解放ランク{RelicFallback.RankName(highestRank)}。レベリングモードへ切り替えます", tag);
                                if (!RelicFallback.Begin(job, reqRank, reqLevel, blockedTypes))
                                {
                                    // 直前に復帰したばかりで同じ理由の再切替 → 往復ループなので停止
                                    SchedulerMain.State = IceState.Idle;
                                    P.TaskManager.Tasks.Clear();
                                    return true;
                                }
                                Mission_Settings.Mode = ModeSelect.LevelMode;
                                P.TaskManager.Tasks.Clear();
                                SchedulerMain.State = IceState.Start;
                                return true;
                            }
                        }

                        bool TryQueueFirstIncomplete(Func<uint, bool> rankFilter, string rankLabel)
                        {
                            var mission = basicMissionList
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus < CosmicHelper.Status.Completed)
                                .Where(x => rankFilter(x))
                                .FirstOrDefault();

                            if (mission != 0)
                            {
                                IceLogging.Verbose($"Found an incomplete mission, queuing it now [{rankLabel}]", tag);
                                Insert_GrabMissionTask(mission);
                                return true;
                            }
                            return false;
                        }

                        bool TryQueueFirstNonGold(Func<uint, bool> rankFilter, string rankLabel)
                        {
                            var mission = basicMissionList
                                .Where(x => CosmicHelper.SheetMissionDict[x].CompletionStatus < CosmicHelper.Status.Gold)
                                .Where(x => rankFilter(x))
                                .FirstOrDefault();

                            if (mission != 0)
                            {
                                IceLogging.Verbose($"Found a mission that can be golded, queuing it [{rankLabel}]", tag);
                                Insert_GrabMissionTask(mission);
                                return true;
                            }
                            return false;
                        }

                        bool isDRank(uint x) => CosmicHelper.SheetMissionDict[x].Drank;
                        bool isCRank(uint x) => CosmicHelper.SheetMissionDict[x].CRank;
                        bool isBRank(uint x) => CosmicHelper.SheetMissionDict[x].BRank;

                        if (jobLv >= 100 && highestRank < 4)
                        {
                            IceLogging.Verbose("Hey! Lv 100 Missions still need to be unlocked, so going to check to see what need to do unlock those..", tag);
                            if (highestRank < 2)
                            {
                                IceLogging.Verbose("ABSOLUTELY no ranks are unlocked yet (We're at D Rank Currently) Going to start with that and work our way up.", tag);
                                if (TryQueueFirstIncomplete(isDRank, "D Rank")) return true;
                            }
                            else if (highestRank < 3)
                            {
                                IceLogging.Verbose("Status Report. C Ranks are unlocked, but missing B Ranks, so we're going to aim to complete a C Rank.", tag);
                                if (TryQueueFirstIncomplete(isCRank, "C Rank")) return true;
                            }
                            else
                            {
                                IceLogging.Verbose("Woooooo B Ranks unlocked! Checking to see if there's a gold need to be completed, or just general completions.", tag);

                                var goldCount = CosmicHelper.SheetMissionDict
                                    .Where(x => x.Value.TerritoryId == Player.Territory.RowId)
                                    .Where(x => x.Value.BRank)
                                    .Where(x => x.Value.CompletionStatus == CosmicHelper.Status.Gold)
                                    .Where(x => x.Value.Jobs.Contains(job))
                                    .Count();

                                var completedStatus = CosmicHelper.SheetMissionDict
                                    .Where(x => x.Value.TerritoryId == Player.Territory.RowId)
                                    .Where(x => x.Value.BRank)
                                    .Where(x => x.Value.CompletionStatus > CosmicHelper.Status.None)
                                    .Where(x => x.Value.Jobs.Contains(job))
                                    .Count();
                                IceLogging.Verbose($"Status | Gold [{goldCount} / 3] | Completed: [{completedStatus} / 5]", tag);

                                if (goldCount < 3)
                                {
                                    IceLogging.Verbose($"Missing Gold to help unlock B Ranks... so going to find one with that ideally", tag);
                                    if (TryQueueFirstNonGold(isBRank, "B Rank")) return true;
                                }

                                if (completedStatus < 5)
                                {
                                    IceLogging.Verbose($"We just need to complete more B Rank Missions (So close...).", tag);
                                    if (TryQueueFirstIncomplete(isBRank, "B Rank")) return true;
                                }
                            }
                        }
                        else if (jobLv >= 90 && highestRank < 3)
                        {
                            IceLogging.Verbose("We've hit Lv 90, and we STILL don't have B ranks unlocked, so going to focus that down.", tag);
                            if (TryQueueFirstIncomplete(isCRank, "C Rank")) return true;
                        }
                        else if (jobLv >= 50 && highestRank < 2)
                        {
                            IceLogging.Verbose("We've atleast hit Lv. 50, and Absolutely no ranks unlocked right now besides D Ranks, going to focus on getting that done.", tag);
                            if (TryQueueFirstIncomplete(isDRank, "D Rank")) return true;
                        }

                        // D〜B ランクの未達成ミッションが掲示板に出ていれば、経験値効率より優先して受注する。
                        // 同じ高効率ミッションばかり受けていると、次ランクの解放条件(そのランクのミッションを規定数達成)を
                        // 満たせず上位ランクを受注できなくなるため。上位ランクほど経験値も多いので、B→C→D の順で選ぶ。
                        // 対象は Relic Grind の絞り込み(ジョブ/レベル/Only Enabled)を通ったミッションのみ。
                        if (C.Relic_PrioritizeIncomplete)
                        {
                            var incompleteLower = missionList
                                .Where(x => basicMissionList.Contains(x))
                                .Select(x => (id: x, info: CosmicHelper.SheetMissionDict[x]))
                                .Where(t => (t.info.Drank || t.info.CRank || t.info.BRank) && t.info.CompletionStatus == CosmicHelper.Status.None)
                                .Where(t => t.info.Jobs.All(j => Player.GetLevel((Job)j) >= t.info.Level))
                                .OrderByDescending(t => t.info.Rank)
                                .Select(t => t.id)
                                .FirstOrDefault();
                            if (incompleteLower != 0)
                            {
                                IceLogging.Info($"Relic Grind: 未達成の {(CosmicHelper.SheetMissionDict[incompleteLower].BRank ? "B" : CosmicHelper.SheetMissionDict[incompleteLower].CRank ? "C" : "D")}ランクミッション {incompleteLower} を優先して受注します(ランク解放条件のため)", tag);
                                LogInfo(incompleteLower);
                                Insert_GrabMissionTask(incompleteLower);
                                return true;
                            }
                        }

                        IceLogging.Verbose($"Relic Mode, Exp Requirements/Results", tag);
                        foreach (var exp in urgency)
                        {
                            IceLogging.Verbose($"{exp.Key} : Value: {exp.Value:N2}", tag);
                        }

                        var filteredList = missionList.Where(x => CosmicHelper.SheetMissionDict[x].RelicXpInfo.Any(kvp => urgency.ContainsKey(kvp.Key) && urgency[kvp.Key] > 0));
                        if (filteredList.Count() == 0)
                        {
                            if (EzThrottler.Throttle("No viable missions throttle"))
                                IceLogging.Info("We've hit a point where somehow, there's no possible missions that could be grabbed to help you increase your exp to the point it's needed\n" +
                                                "So... this is an interesting spot... check to make sure that you're on the right planet to ", tag);

                            return true;
                        }
                        else
                        {
                            uint bestMissionId = 0;
                            float bestScore = float.NegativeInfinity;

                            foreach (var missionId in filteredList)
                            {
                                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var sheetInfo) && (basicMissionList.Contains(missionId) || specialMissionList.Contains(missionId)))
                                {
                                    bool allLeveled = true;
                                    foreach (var classes in sheetInfo.Jobs)
                                    {
                                        var classLv = Player.GetLevel((Job)classes);
                                        IceLogging.Verbose($"{classes}: Lv: {classLv}");
                                        allLeveled &= classLv >= sheetInfo.Level;
                                    }

                                    if (!allLeveled)
                                    {
                                        IceLogging.Verbose($"Skipping Mission: {missionId} due to not high enough lv [Player: {jobLv} | Mission: {sheetInfo.Level}].\n");
                                        continue;
                                    }


                                    float score = 0;
                                    foreach (var reward in sheetInfo.RelicXpInfo)
                                    {
                                        if (urgency.TryGetValue(reward.Key, out var info))
                                        {
                                            float contribution = info * reward.Value;
                                            if (contribution > 0)
                                            {
                                                score += contribution;
                                            }
                                        }
                                    }
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestMissionId = missionId;
                                    }
                                }
                            }

                            if (bestMissionId != 0)
                            {
                                LogInfo(bestMissionId);
                                Insert_GrabMissionTask(bestMissionId);
                                return true;
                            }
                            else
                            {
                                IceLogging.Info("We've searched through all the missions and none had the exp we needed, which means time to reoll WOOOO!\n" +
                                    $"Best Score: {bestScore}\n" +
                                    $"Best Mission (not): {bestMissionId}", tag);

                                return true;
                            }
                        }
                    }
                    else if (mode is ModeSelect.Standard or ModeSelect.MissionGoldMode)
                    {
                        if (type is MissionTypes.Standard)
                        {
                            IceLogging.Verbose($"Checking Standard missions.\n" +
                                $"Loaded mission Count: {missionList.Count()}\n" +
                                $"Amount of viable missions: {basicMissionList.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (basicMissionList.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Info("No missions were found for basic missions tab. Continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.Provisional)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of viable missions: {specialMissionList.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (specialMissionList.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Verbose($"No missions were found for: {type}. Continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.Critical)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of available missions: {criticalMissions.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (criticalMissions.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Info("No missions were found for the critical missions, so continuing on", tag);
                            return true;
                        }
                        else if (type is MissionTypes.ToolMastery)
                        {
                            IceLogging.Verbose($"Checking missions for the following mode:\n" +
                                $"Mode: {type}\n" +
                                $"Loaded mission count: {missionList.Count()}\n" +
                                $"Amount of available missions: {masteryMissions.Count()}", tag);

                            foreach (var missionId in missionList)
                            {
                                if (masteryMissions.Contains(missionId))
                                {
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }
                            return true;
                        }
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Dumb dumb message"))
                            IceLogging.Verbose("Not a valid mode was found. ICE. FIX THIS", tag);
                    }
                }
            }
            else
            {
                ReOpenMissionUi(tag);
            }

            return false;
        }
        private static void Insert_GrabMissionTask(uint missionId)
        {
            P.TaskManager.Tasks.Clear();

            // Extract materia between missions if spiritbond is ready and next mission is not EX+
            if (C.SelfSpiritbondGather && Task_Spiritbond.IsSpiritbondReadyAny())
            {
                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var nextMission) && nextMission.Rank < 6)
                {
                    IceLogging.Info($"Next mission rank {nextMission.Rank} is below EX+, extracting materia first");
                    P.TaskManager.Enqueue(() => Task_Spiritbond.ExtractMateria(), "Extracting materia before next mission");
                }
            }

            if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var sheetInfo))
            {
                bool isCollectable = sheetInfo.Attributes.HasFlag(MissionAttributes.Collectables);

            }

            P.TaskManager.EnqueueMulti
                (
                    new(() => CheckForMovementRequired(missionId), "Checking to see if we need to move to mission"),
                    new(() => Mission_ChangeJob(missionId), "Changing to correct job for mission"),
                    new(() => GrabMission(missionId), "Grabbing mission to initate")
                );
        }
        private static bool? Mission_ChangeJob(uint missionId)
        {
            IceLogging.Verbose("Starting to change job");

            var mission = CosmicHelper.SheetMissionDict[missionId];
            var jobId = mission.Jobs.First();

            if ((uint)Player.Job == jobId)
                return true;
            else
            {
                if (EzThrottler.Throttle("Swapping to job for mission"))
                {
                    GearsetHandler.TaskClassChange((Job)jobId);
                    IceLogging.Debug($"Swapping to job: {jobId}");
                }
                return false;
            }
        }
        private static Vector3 randomFishingHole = Vector3.Zero;
        private static bool? CheckForMovementRequired(uint missionId)
        {
            string tag = "[Check Missions: Movement Check]";

            var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
            var missionConfig = C.MissionConfig[missionId];

            IceLogging.Info($"[MoveCheck] id={missionId} attrs=[{sheetInfo.Attributes}] gather={sheetInfo.IsGatherMission} fish={sheetInfo.IsFishMission} unsupported={UnsupportedMissions.Ids.Contains(missionId)} mapPos=({sheetInfo.MapPosition.X},{sheetInfo.MapPosition.Y})", tag);

            if (UnsupportedMissions.Ids.Contains(missionId))
            {
                IceLogging.Info("Mission is currently in manual mode, or not supported. So not going to pathfind to it.", tag);
                return true;
            }
            else if (!P.Navmesh.Installed)
            {
                IceLogging.Error("HEY. YOU DIDN'T READ THE HELP ME PAGE. AND NOW YOU'RE MISSING NAVMESH. So... yeah... if things break this is why");
                return true;
            }
            else if (sheetInfo.IsGatherMission)
            {
                var route = sheetInfo.Gather_MapKey;

                var gatherInfo = GatheringRouteLoader.GetRoute(route);

                if (gatherInfo == null || gatherInfo.Nodes.Count == 0)
                {
                    IceLogging.Error("Hey, so this is actually missing the information for it. So going to just actually add it to the unsupported mission list", tag);
                    UnsupportedMissions.Ids.Add(missionId);
                    return true;
                }
                else
                {
                    var startNode = gatherInfo.Nodes[0];

                    foreach (var node in gatherInfo.Nodes)
                    {
                        if (Player.DistanceTo(node.Position) < 5)
                        {
                            IceLogging.Info("We're close enough to the node! So continuing onto grabbing the mission", tag);
                            return true;
                        }
                    }

                    IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now", tag);
                    var randomPosition = Task_NavmeshMove.Gather_RandomFanPosition(startNode);
                    Task_NavmeshMove.Enqueue_NavmeshTask(randomPosition);

                    return true;
                }
            }
            else if (sheetInfo.IsFishMission)
            {
                var location = sheetInfo.MapPosition;
                var territory = sheetInfo.TerritoryId;
                if (!GatheringUtil.MoonFishingLocations.TryGetValue(territory, out var zoneFishing)
                    || !zoneFishing.TryGetValue(location, out var fishingHole)
                    || fishingHole.Count == 0)
                {
                    // 釣り場座標が未収録のフラグは、諦める前にフラグ周辺を動的に探索して岸の立ち位置を探す。
                    var dynamicResult = TryDynamicFishingMove(sheetInfo, territory, location, tag);
                    if (dynamicResult.HasValue)
                        return dynamicResult.Value;
                    IceLogging.Error("We've seemed to have ran into a problem with the fishing hole... either it's missing spots, or it doesn't exist. Please report back to me on this with logs leading up to this\n" +
                        $"Mission ID: {missionId} | Map Position: {location} | Moon Territory: {territory}\n" +
                        $"Adding to the unsupported list so it's marked on your side for now", tag);
                    UnsupportedMissions.Ids.Add(missionId);
                    return true;
                }

                var customFishingHole = C.Personal_FishLocation.Where(x => x.MapCoords == location).FirstOrDefault();
                if (customFishingHole != null)
                {
                    var fishingLoc = customFishingHole.WorldPosition;

                    if (fishingLoc != null)
                    {
                        if (Player.DistanceTo(fishingLoc.Value) < 3)
                        {
                            IceLogging.Info($"We have a custom fishing hole set, and we're close to it. {fishingLoc.Value}", tag);
                            randomFishingHole = Vector3.Zero;
                            return true;
                        }
                        else
                        {
                            IceLogging.Verbose($"We have a custom fishing hole set, and we're not within fishing range. Queueing up moving to it: {fishingLoc.Value}");
                            Task_NavmeshMove.Enqueue_NavmeshTask(fishingLoc.Value);
                            randomFishingHole = Vector3.Zero;
                            return true;
                        }
                    }
                }

                foreach (var fishingSpot in fishingHole)
                {
                    if (Player.DistanceTo(fishingSpot.FishingSpot) < 3)
                    {
                        IceLogging.Info($"We've reached our fishing spot! We are current at: {fishingSpot.FishingSpot}", tag);
                        randomFishingHole = Vector3.Zero;
                        return true;
                    }
                }

                if (randomFishingHole == Vector3.Zero)
                {
                    var _random = new Random();
                    var randomIndex = _random.Next(fishingHole.Count);
                    if (EzThrottler.Throttle("Setting fishing hole destination"))
                    {
                        IceLogging.Debug($"Random number spot said we're going to the following fishing hole #: {randomIndex}");
                        randomFishingHole = fishingHole[randomIndex].FishingSpot;
                    }
                }
                else
                {
                    IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now");
                    Task_NavmeshMove.Enqueue_NavmeshTask(randomFishingHole);
                    randomFishingHole = Vector3.Zero;
                    return true;
                }
            }
            else if (C.PersonalReturnSpot)
            {
                if (sheetInfo.Attributes.HasFlag(MissionAttributes.Critical))
                {
                    IceLogging.Info($"We are currently aimed to do a critical mission, and we're on a crafter(?) so we're not going to move from our spot", tag);
                    return true;
                }
                else
                {
                    var territory = Player.Territory.RowId;
                    if (C.CrafterLocations.TryGetValue(territory, out var location))
                    {
                        IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now");
                        Task_NavmeshMove.Enqueue_NavmeshTask(location);
                        return true;
                    }
                    else
                    {
                        IceLogging.Debug("No location is set for this place, so continuing on", tag);
                        return true;
                    }
                }
            }
            else
            {
                IceLogging.Info("Mission was not a gathering or critical mission. Navmesh moving was not necessary. Moving onto next step", tag);
                return true;
            }

            return false;
        }
        private static int retryCheck = 0;
        // 受注を試みても受注できなかったミッション(ランク未解放・レベル不足など)。一定時間は候補から外す。
        private static readonly Dictionary<uint, DateTime> _unacceptable = new();
        private const double UnacceptableExpireMinutes = 30;
        private static int _initiateAttempts = 0;
        private static uint _initiateMissionId = 0;
        private const int MaxInitiateAttempts = 5;

        internal static bool IsUnacceptable(uint missionId)
        {
            if (!_unacceptable.TryGetValue(missionId, out var at))
                return false;
            if ((DateTime.Now - at).TotalMinutes < UnacceptableExpireMinutes)
                return true;
            _unacceptable.Remove(missionId);
            return false;
        }

        private static bool? GrabMission(uint missionId, bool reroll = false)
        {
            string tag = "[Check Missions: Grab Mission]";

            if (_initiateMissionId != missionId)
            {
                _initiateMissionId = missionId;
                _initiateAttempts = 0;
            }

            if (CosmicHelper.CurrentLunarMission != 0)
            {
                retryCheck = 0;
                _initiateAttempts = 0;
                Mission_Settings.ResetNodeCounter();

                if (reroll)
                {
                    SchedulerMain.State = IceState.AbandonMission;
                    Task_AbandonMission.ForceAbandon = true;
                }
                else
                {
                    SchedulerMain.State = IceState.ExecutingMission;
                    Task_AbandonMission.ForceAbandon = false;
                }
                Mission_Settings.nodeTotal = 0;
                P.TaskManager.Tasks.Clear();
                IceLogging.Debug($"State upon exiting: {SchedulerMain.State}");
                return true;
            }
            else
            {
                if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady)
                {
                    List<uint> viableMissions = new();
                    viableMissions.Add(missionId);

                    var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
                    var allmissions = CosmicHandler.All_AvailableMissions();
                    if (allmissions.Contains(missionId))
                    {
                        if (EzThrottler.Throttle("Selecting Mission", 1000))
                        {
                            // 受注を何度試みても受注状態にならない(ロック中など)ミッションは諦めて候補から外す
                            if (_initiateAttempts >= MaxInitiateAttempts)
                            {
                                IceLogging.Warning($"ミッション {missionId} を {MaxInitiateAttempts} 回受注しようとしても受注できませんでした(ランク未解放/レベル不足の可能性)。{UnacceptableExpireMinutes:F0}分間は候補から外して選び直します", tag);
                                _unacceptable[missionId] = DateTime.Now;
                                _initiateAttempts = 0;
                                P.TaskManager.Tasks.Clear();
                                return true;
                            }
                            _initiateAttempts++;
                            InitiateMission(missionId);
                        }
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Reporting Current Missions"))
                        {
                            IceLogging.Verbose("We couldn't find the mission? So we're reporting back all visible missions currently", tag);
                            foreach (var mission in allmissions.OrderBy(x => CosmicHelper.SheetMissionDict[x].Rank))
                            {
                                var allMissionInfo = CosmicHelper.SheetMissionDict[mission];
                                string jobs = string.Join(", ", allMissionInfo.Jobs);
                                IceLogging.Verbose($"Job: [{jobs}] | ID: [{mission}] [{allMissionInfo.Name}] | Rank: [{allMissionInfo.Rank}]", tag);
                            }

                            if (sheetInfo.Jobs.Count > 1)
                            {
                                if (EzThrottler.Throttle("Swapping tabs"))
                                {
                                    IceLogging.Verbose("We seem to be on a dual class mission, and it also seems like we're on the gathering class... and missing it from that list. So we're just gonna swap", tag);
                                    CorrectJobTab(sheetInfo.Jobs[0]);
                                }
                            }

                            if (FrameThrottler.Throttle("Counter added", 8))
                                retryCheck += 1;

                            if (retryCheck >= 4)
                            {
                                IceLogging.Verbose($"Mission could no longer be found: {missionId}, retrying the process", tag);
                                retryCheck = 0;
                                P.TaskManager.Tasks.Clear();
                                return true;
                            }
                        }
                    }

                }
                else
                {
                    ReOpenMissionUi(tag);
                }
            }

            return false;
        }
        private static unsafe void InitiateMission(uint missionId)
        {
            var WKSInstance = WKSManager.Instance();
            if (WKSInstance != null)
            {
                WKSInstance->MissionModule->InitiateMission((ushort)missionId);
            }
        }
        private static bool? FindReroll()
        {
            string tag = "[Check Missions: Find Reroll]";

            if (WaitingForSpecialMissions())
            {
                EnterWaitForSpecialMissions(tag);
                return true;
            }

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady)
            {
                var testMission = missionInfo.StellerMissions.FirstOrDefault();
                uint missionToAbandon = 0;
                if (testMission != null)
                {
                    var attribute = CosmicHelper.SheetMissionDict[testMission.MissionId].Attributes;
                    bool nonStandard = attribute.HasFlag(MissionAttributes.ProvisionalSequential) || attribute.HasFlag(MissionAttributes.ProvisionalTimed) 
                                    || attribute.HasFlag(MissionAttributes.ProvisionalWeather) || attribute.HasFlag(MissionAttributes.Critical);
             
                    if (nonStandard)
                    {
                        if (FrameThrottler.Throttle("Selecting proper tab", 8))
                        {
                            missionInfo.BasicMissions();
                        }
                        return false;
                    }
                    else
                    {
                        List<uint> AExRank = new List<uint>();
                        List<uint> ARank = new List<uint>();
                        List<uint> BRank = new List<uint>();
                        List<uint> CRank = new List<uint>();
                        List<uint> DRank = new List<uint>();

                        IceLogging.Info($"We're abandoning mission... so this should be the right tab for this: Rank: {CosmicHelper.SheetMissionDict[testMission.MissionId].Rank}");

                        // Track mission appearance counts
                        foreach (var mission in missionInfo.StellerMissions)
                        {
                            var missionId = mission.MissionId;

                            // Increment appearance count
                            if (!Mission_Settings.missionApperenceCount.ContainsKey(missionId))
                                Mission_Settings.missionApperenceCount[missionId] = 0;
                            Mission_Settings.missionApperenceCount[missionId]++;

                            var rank = CosmicHelper.SheetMissionDict[missionId].Rank;
                            IceLogging.Verbose($"Checking: {missionId} | Rank: {rank}");

                            switch (rank)
                            {
                                case 6: // Master, treated as EX+ tier
                                case 5: AExRank.Add(missionId); break;
                                case 4: ARank.Add(missionId); break;
                                case 3: BRank.Add(missionId); break;
                                case 2: CRank.Add(missionId); break;
                                case 1:
                                default: DRank.Add(missionId); break;
                            }
                        }

                        bool CheckARanks = (MissionLibrary[MissionKind.Ex].Count > 0 || MissionLibrary[MissionKind.A].Count > 0) && (AExRank.Count > 0 || ARank.Count > 0);
                        bool CheckBRanks = (MissionLibrary[MissionKind.B].Count > 0 && BRank.Count > 0);
                        bool CheckCRanks = (MissionLibrary[MissionKind.C].Count > 0 && CRank.Count > 0);
                        bool CheckDRanks = (MissionLibrary[MissionKind.D].Count > 0 && DRank.Count > 0);

                        IceLogging.Verbose($"[Ex] = {AExRank.Count()}\n" +
                            $"[A] = {ARank.Count()}\n" +
                            $"[B] = {BRank.Count()}\n" +
                            $"[C] = {CRank.Count()}\n" +
                            $"[D] = {DRank.Count()}", tag);

                        List<MissionKind> ranks = new() { MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
                        var enabledCount = 0;
                        foreach (var rank in ranks)
                        {
                            enabledCount += MissionLibrary[rank].Count();
                        }

                        if (enabledCount == 0)
                        {
                            IceLogging.Info("We don't have any basic missions enabled under the following class\n" +
                                $"{Mission_Settings.SelectedJob}. So we're just going to clear -> Reset (Assuming we're checking for timed and such)");
                            P.TaskManager.Tasks.Clear();
                            return true;
                        }

                        var random = new Random();
                        void ShuffleList<T>(List<T> list, Random rnd)
                        {
                            for (int i = list.Count - 1; i > 0; i--)
                            {
                                int j = rnd.Next(i + 1);
                                (list[i], list[j]) = (list[j], list[i]);
                            }
                        }

                        ShuffleList(AExRank, random);
                        ShuffleList(ARank, random);
                        ShuffleList(BRank, random);
                        ShuffleList(CRank, random);
                        ShuffleList(DRank, random);

                        // small function to find a frequent mission that might be locking us
                        uint FindFrequentMission(List<uint> missionList, int threshold = 3)
                        {
                            foreach (var missionId in missionList)
                            {
                                if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mission))
                                {
                                    if (mission.Jobs.Count == 2)
                                    {
                                        if (!(Player.GetLevel((Job)mission.Jobs[0]) >= 100 && Player.GetLevel((Job)mission.Jobs[1]) >= 100))
                                            continue;
                                    }
                                }

                                if (Mission_Settings.missionApperenceCount.TryGetValue(missionId, out int count) && count >= threshold)
                                {
                                    return missionId;
                                }
                            }
                            return 0;
                        }

                        if (CheckARanks)
                        {
                            // Check for frequent missions in A/AEx ranks first
                            uint frequentAEx = FindFrequentMission(AExRank, Mission_Settings.rerollThreshold);
                            uint frequentA = FindFrequentMission(ARank, Mission_Settings.rerollThreshold);

                            if (AExRank.Count > 2)
                            {
                                if (frequentAEx != 0)
                                {
                                    missionToAbandon = frequentAEx;
                                    IceLogging.Debug($"Abandoning frequently appearing AEX mission (appeared {Mission_Settings.missionApperenceCount[frequentAEx]} times)", tag);
                                    Mission_Settings.previousAbandonRank = 5;
                                }
                                else
                                {
                                    IceLogging.Debug($"Only AEX Rank missions are available. Forcing an AEX rank to be accepted");
                                    missionToAbandon = AExRank.First();
                                    Mission_Settings.previousAbandonRank = 5;
                                }
                            }
                            else if (ARank.Count > 2)
                            {
                                if (frequentA != 0)
                                {
                                    missionToAbandon = frequentA;
                                    IceLogging.Debug($"Abandoning frequently appearing A mission (appeared {Mission_Settings.missionApperenceCount[frequentA]} times)", tag);
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                                else
                                {
                                    IceLogging.Debug($"Only A Rank missions are available. Forcing an A rank to be accepted", tag);
                                    missionToAbandon = ARank.First();
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                            }
                            else
                            {
                                if (Mission_Settings.previousAbandonRank == 5)
                                {
                                    if (frequentA != 0)
                                    {
                                        missionToAbandon = frequentA;
                                        IceLogging.Debug($"Abandoning frequently appearing A mission (appeared {Mission_Settings.missionApperenceCount[frequentA]} times)", tag);
                                        Mission_Settings.previousAbandonRank = 4;
                                    }
                                    else
                                    {
                                        missionToAbandon = ARank.First();
                                        IceLogging.Debug($"Abandoning Rank 4 Mission.");
                                        Mission_Settings.previousAbandonRank = 4;
                                    }
                                }
                                else if (Mission_Settings.previousAbandonRank == 4)
                                {
                                    if (frequentAEx != 0)
                                    {
                                        missionToAbandon = frequentAEx;
                                        IceLogging.Debug($"Abandoning frequently appearing AEX mission (appeared {Mission_Settings.missionApperenceCount[frequentAEx]} times)", tag);
                                        Mission_Settings.previousAbandonRank = 5;
                                    }
                                    else
                                    {
                                        missionToAbandon = AExRank.First();
                                        IceLogging.Debug($"Abandoning Rank 5 Mission", tag);
                                        Mission_Settings.previousAbandonRank = 5;
                                    }
                                }
                                else
                                {
                                    missionToAbandon = ARank.First();
                                    IceLogging.Debug($"Starting off w/ abandoning an A rank", tag);
                                    Mission_Settings.previousAbandonRank = 4;
                                }
                            }
                        }
                        else if (CheckBRanks)
                        {
                            uint frequentB = FindFrequentMission(BRank, Mission_Settings.rerollThreshold);
                            if (frequentB != 0)
                            {
                                missionToAbandon = frequentB;
                                IceLogging.Debug($"Abandoning frequently appearing B mission (appeared {Mission_Settings.missionApperenceCount[frequentB]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = BRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 3;
                        }
                        else if (CheckCRanks)
                        {
                            uint frequentC = FindFrequentMission(CRank, Mission_Settings.rerollThreshold);
                            if (frequentC != 0)
                            {
                                missionToAbandon = frequentC;
                                IceLogging.Debug($"Abandoning frequently appearing C mission (appeared {Mission_Settings.missionApperenceCount[frequentC]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = CRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 2;
                        }
                        else if (CheckDRanks)
                        {
                            uint frequentD = FindFrequentMission(DRank, Mission_Settings.rerollThreshold);
                            if (frequentD != 0)
                            {
                                missionToAbandon = frequentD;
                                IceLogging.Debug($"Abandoning frequently appearing D mission (appeared {Mission_Settings.missionApperenceCount[frequentD]} times)", tag);
                            }
                            else
                            {
                                missionToAbandon = DRank.First();
                            }
                            Mission_Settings.previousAbandonRank = 1;
                        }
                        else if (Mission_Settings.Mode == ModeSelect.LevelMode)
                        {
                            if (MissionLibrary[MissionKind.B].Count > 0)
                            {
                                IceLogging.Debug("Leveling mode is active. Need to find a valid C or D Rank mission", tag);
                                var mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 50).FirstOrDefault();

                                if (mission != null)
                                {
                                    missionToAbandon = mission.MissionId;
                                }
                                else
                                {
                                    mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 10).FirstOrDefault();

                                    if (mission != null)
                                        missionToAbandon = mission.MissionId;
                                }
                            }
                            else if (MissionLibrary[MissionKind.C].Count > 0)
                            {
                                var mission = missionInfo.StellerMissions.Where(m => CosmicHelper.SheetMissionDict[m.MissionId].Level == 10).FirstOrDefault();
                                if (mission != null)
                                    missionToAbandon = mission.MissionId;
                            }
                        }

                        if (missionToAbandon != 0)
                        {
                            P.TaskManager.EnqueueMulti
                                (
                                    new(() => Mission_ChangeJob(missionToAbandon)),
                                    new(() => GrabMission(missionToAbandon, true))
                                );
                            return true;
                        }
                    }
                }
                else
                {
                    if (FrameThrottler.Throttle("Selecting proper tab", 8))
                    {
                        missionInfo.BasicMissions();
                    }
                    return false;
                }
            }
            else
            {
                ReOpenMissionUi(tag);
            }

            return false;
        }
        private static unsafe bool MissionGolded(uint id)
        {
            var managerPtr = WKSManager.Instance();
            if (managerPtr == null) return false;

            var isGolded = managerPtr->IsMissionGolded(id);

            return isGolded;
        }

        // functions that are used across things
        private static unsafe bool CorrectJobTab(uint job, byte categoryTab = 0)
        {
            var agent = AgentWKSMission.Instance();
            if (agent == null)
            {
                if (EzThrottler.Throttle("AgentWKSMission Error", 2000))
                    IceLogging.Error("AgentWKSMission has returned null. CS code might need an update...", "Task: Check Mission | Open Job Tab");

                return false;
            }

            return AgentWKSMissionEx.SetSelectedJobTab(agent, (byte)job, categoryTab);
        }
        private static void Notes()
        {
            /*
             * This is kind of my place to just... figure out how tf the logic is going to work. 
             * Right now, the logic is 
             * 1: Store all the missions in the dictionary.
             *   - This doesn't matter if what kind of mode, we're just storing it. It should... allow for re-rolling of missions even when in relic mode on weird edge cases (aka, only selected missions for some reason)
             * 2: Added in logic for checking each tab, and adding drone checking somewhere in there. 
             *   - The way this works should be: Check each tab for a mission. If one exist in that place, we're just going to clear the queue -> just proceed to the grab mission task 
             *   - If not, then it continues onto the next kind
             *   - Drone mode is in there as a general "Hey, we gonna check to see if we can open a drone/have a drone running -> find it between missions (this is nice cause it allows users to dictate when they're going to go looking for a box in case of weather. red alert...)
             * 3: If we get to this point in the queue and we STILL haven't grabbed a mission, it means that we need to reroll for one. 
             *   - Logic will be the same here as before. Check to see what ones need to be rerolled if possible
             *
            */
        }
        private static FishingDebug _fishRay = null;
        private static Vector3 dynamicFishingHole = Vector3.Zero;

        // 釣り場座標(MoonFishingLocations)が未収録のフラグ向けのフォールバック。
        // フラグ周辺をグリッド状に探索し、釣り可能な岸の立ち位置を探して移動する。
        // 戻り値 null = 動的探索でも解決できない(呼び出し側で未対応として登録する)。
        private static bool? TryDynamicFishingMove(CosmicHelper.CosmicInfo sheetInfo, uint territory, Vector2 location, string tag)
        {
            var center = Utils.FlagToWorld(territory, location);
            if (!center.HasValue)
                return null;

            _fishRay ??= new FishingDebug();
            float radius = Math.Clamp(sheetInfo.Radius, 15f, 30f); // 池を想定し、過大な半径は抑える

            // まだフラグ付近に居ない → 周辺の地形を読み込ませるためにフラグへ近づく。
            if (Player.DistanceTo(center.Value) > radius + 5f)
            {
                if (EzThrottler.Throttle("Fishing dynamic move-to-flag", 1000))
                    IceLogging.Verbose($"釣り(動的): フラグ付近へ移動します {center.Value} (半径{radius:N0})", tag);
                Task_NavmeshMove.Enqueue_NavmeshTask(center.Value, true, radius);
                return true;
            }

            // 現在地から釣れるなら完了。実際の釣行や向き調整は釣りタスクが担当する。
            if (_fishRay.IsFishable() || _fishRay.FindFishableLocation(out _, 36))
            {
                IceLogging.Info("釣り(動的): 釣り可能な水面を確認しました。", tag);
                dynamicFishingHole = Vector3.Zero;
                return true;
            }

            // 探索で決めた目的地へ移動中なら継続する。到達してもまだ釣れない場合はリセットして再探索する。
            if (dynamicFishingHole != Vector3.Zero)
            {
                if (Player.DistanceTo(dynamicFishingHole) < 2.5f)
                {
                    dynamicFishingHole = Vector3.Zero;
                }
                else
                {
                    Task_NavmeshMove.Enqueue_NavmeshTask(dynamicFishingHole, true, 1.5f);
                    return true;
                }
            }

            // 釣り可能な岸の立ち位置を探し、見つかれば移動する。
            if (FishingDynamicSearch.TryFindStand(center.Value, radius, _fishRay, out var standPos, out _))
            {
                IceLogging.Info($"釣り(動的): 釣り可能な立ち位置を発見しました。移動します {standPos}", tag);
                dynamicFishingHole = standPos;
                Task_NavmeshMove.Enqueue_NavmeshTask(standPos, true, 1.5f);
                return true;
            }

            return null; // 見つからない → 呼び出し側で未対応として登録する
        }
    }
}
