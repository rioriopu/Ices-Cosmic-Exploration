using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Sounds;
using ICE.Ui.DebugWindowTabs;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using System.Collections.Generic;
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
            Master,  // マスターシップミッション (Unknown2==true, 各職「MASTER：」3つ)
            ExPlus,  // LevelGroup=6 (「EX+」高機能製作/採集ミッション)
            Ex,
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
            [MissionKind.Master] = new(),
            [MissionKind.ExPlus] = new(),
            [MissionKind.Ex] = new(),
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
        private static int GrabMission_Counter = 0;
        // マスターシップ最優先 pre-handler でタブ3に在るのに有効マスターが MissionList に出ない場合の待機開始時刻(ms)。
        // 0=未待機。MasterWaitTimeoutMs を超えたら通常ミッション選択へフォールスルーし、COSMO MISSIONS画面での無限停止を防ぐ。
        private static long _masterWaitSince = 0;
        private const long MasterWaitTimeoutMs = 8000;
        private static void ReOpenMissionUi(string tag)
        {
            if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var moonHud) && moonHud.IsAddonReady)
            {
                if (EzThrottler.Throttle("Opening the mission ui"))
                {
                    IceLogging.Info("Opening the moon mission selection hud", tag);
                    moonHud.Mission();
                }
            }
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
            else if (attribute.HasFlag(MissionAttributes.Mastership))
                entry = MissionKind.Master; // マスターシップ(LG6+Unknown2)。EX+(LG6)とはここで分離
            else if (rank != 0)
            {
                entry = rank switch
                {
                    6 => MissionKind.ExPlus, // EX+ (マスターシップ)。従来は_=>Unknownに落ちて選択候補から除外されていた
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

            // マスター待機タイマをリセット。惑星移動/モード変更/再開でタブ3滞留の古い計測が残ると、
            // 次のマスター判定で即タイムアウト誤判定して再出現を待たずにフォールスルーするのを防ぐ。
            _masterWaitSince = 0;

            foreach (var entry in MissionLibrary)
            {
                entry.Value.Clear();
            }

            var playerTerritory = Player.Territory.RowId;

            var SinusCount = CosmicHelper.SheetMissionDict
                .Where(x => C.MissionConfig[x.Key].Enabled)
                .Where(x => x.Value.TerritoryId == 1237);
            var PhaennaCount = CosmicHelper.SheetMissionDict
                .Where(x => C.MissionConfig[x.Key].Enabled)
                .Where(x => x.Value.TerritoryId == 1291);
            var OizysCount = CosmicHelper.SheetMissionDict
                .Where(x => C.MissionConfig[x.Key].Enabled)
                .Where(x => x.Value.TerritoryId == 1310);
            var AuxesiaCount = CosmicHelper.SheetMissionDict
                .Where(x => C.MissionConfig[x.Key].Enabled)
                .Where(x => x.Value.TerritoryId == 1319);

            IceLogging.Info("This is just general message to let me know WHAT planet you're on, and where you have things enabled\n" +
                "If you're not running things that requires these to be enabled, you can ignore this if you're reading this.\n" +
                $"Sinus [1237] = [{SinusCount.Count()}]\n" +
                $"Phaenna [1291] = [{PhaennaCount.Count()}]\n" +
                $"Oizys [1310] = [{OizysCount.Count()}]\n" +
                $"Auxesia [1319] = [{AuxesiaCount.Count()}]\n" +
                $"Current TerritoryID: {playerTerritory}");

            var modeSelected = Mission_Settings.Mode;
            foreach (var mission in CosmicHelper.SheetMissionDict)
            {
                if (mission.Value.TerritoryId != Player.Territory.RowId)
                    continue;

                bool provisional = mission.Value.IsProvisional;

                var missionId = mission.Key;

                // 未開拓エリア等でノードが見つからずunsupported登録されたミッションは候補から除外し、再選択しない。
                // これにより採取不能ミッションを自動スキップして別ミッションへ進める。
                if (UnsupportedMissions.Ids.Contains(missionId))
                    continue;

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

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var hud) && hud.IsAddonReady)
            {
                IceLogging.Info("The Mission Selection Ui is visible! Continuing on", tag);
                return true;
            }
            else
            {
                ReOpenMissionUi(tag);
            }

            return false;
        }
        public static bool? CheckTabs()
        {
            string tag = "Check Missions: Check Tabs";
            var priority = C.MissionTypePrio;

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var x) && x.IsAddonReady)
            {
                foreach (var type in C.MissionTypePrio)
                {
                    switch (type)
                    {
                        case MissionTypes.Critical:
                            {
                                // === 緊急(Red Alert)タブ棒立ち対策 ===
                                // MissionLibrary[Critical] は静的シートデータから作られるため、Red Alert が発生していなくても常に非空になる。
                                // 従来はこの非空判定だけで CheckMissions(Critical) を enqueue → OpenCorrectTab が Critical タブへ切替を試みるが、
                                // 緊急非発生時の Critical タブにはジョブサブタブが解決せず OpenCorrectTab が永久 false → COSMO MISSIONS画面で棒立ちになった。
                                // GetCriticalMissions API で「今 Red Alert で実際に受注可能な緊急」を取得し、それが在る場合のみ Critical チェックへ進む。
                                if (AgentWKSMissionEx.HasCriticalApi)
                                {
                                    var liveCriticals = CosmicHandler.Critical_AvailableMissions();
                                    var grabbableCriticals = MissionLibrary[MissionKind.Critical].Where(m => liveCriticals.Contains(m)).ToList();
                                    if (EzThrottler.Throttle("CriticalTabDiag", 5000))
                                        IceLogging.Info($"[緊急診断] CheckTabs: 候補(static)={MissionLibrary[MissionKind.Critical].Count}件 live(RedAlert)={liveCriticals.Count}件 grabbable={grabbableCriticals.Count}件 SelectedJob={Mission_Settings.SelectedJob} GrindOffClassRedAlert={C.GrindOffClassRedAlert}", tag);
                                    if (grabbableCriticals.Count > 0)
                                    {
                                        P.TaskManager.Enqueue(() => CheckMissions(grabbableCriticals, type), "Checking Critical tab for missions");
                                    }
                                }
                                else
                                {
                                    // API シグネチャ未取得時のみ従来挙動へフォールバック(Red Alert 判定不能なため)。
                                    if (EzThrottler.Throttle("CriticalTabDiag", 5000))
                                        IceLogging.Info($"[緊急診断] CheckTabs(fallback): GetCriticalMissions未取得。MissionLibrary[Critical]={MissionLibrary[MissionKind.Critical].Count}件 SelectedJob={Mission_Settings.SelectedJob}", tag);
                                    if (MissionLibrary[MissionKind.Critical].Count > 0)
                                    {
                                        P.TaskManager.Enqueue(() => CheckMissions(MissionLibrary[MissionKind.Critical], type), "Checking Critical tab for missions");
                                    }
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
                                List<uint> basicMissions = new();
                                List<MissionKind> MissionRanks = new() { MissionKind.Master, MissionKind.ExPlus, MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
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
                        case MissionTypes.DroneSearch:
                            {
                                // ドローン自動探査の起動。旧IsInOizys()ではOizys限定でAuxesiaで起動しなかった→IsInDroneZone()で両対応。
                                if (C.Cosmodrone_Run && PlayerHelper.IsInDroneZone())
                                {
                                    P.TaskManager.Enqueue(() => Task_ArtifactSearch.RefreshMapInfo(), "Inserting Drone Task");
                                }
                                break;
                            }
                    }
                }

                P.TaskManager.Enqueue(() => FindReroll(), "Find mission to reroll for");
            }
            else
            {
                ReOpenMissionUi(tag);
            }
            return true;
        }
        private static bool? CheckMissions(List<uint> missionList, MissionTypes type)
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

            if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var missionInfo) && missionInfo.IsAddonReady)
            {
                var basicMissionList = CosmicHandler.Basic_AvailableMissions();
                var specialMissionList = CosmicHandler.Provisional_AvailableMissions();
                var visibleMissions = CosmicHandler.VisibleMissions();
                var mode = Mission_Settings.Mode;

                // === ドローン探索優先 (pre-handler) ===
                // ドローン自動探索が稼働中(Cosmodrone_Run)で、まだ使えるドローン(箱所持 or 自動購入で買える)がある間は、
                // ミッション grab を見送ってドローンの掘削+鑑定サイクルを優先する。
                // ドローンと製作ミッション(マスター含む)を両方Enabledにしていると、掘削直後に製作 grab が鑑定を奪い、
                // 採掘地で製作を始めてしまう(カエデへ行かない)問題への対策。ドローンが尽きたら通常のミッションへ復帰する。
                // 有効化したマスターシップミッションがあるか(ドローン優先の判定とマスター優先pre-handler両方で使う)
                bool anyMasterEnabled = MissionLibrary[MissionKind.Master].Any(m => C.MissionConfig.TryGetValue(m, out var mc) && mc.Enabled);

                if (C.Cosmodrone_Run && PlayerHelper.IsInDroneZone())
                {
                    var dBoxId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var dbi) ? dbi.boxId : 0u;
                    bool hasBox = dBoxId != 0 && PlayerHelper.GetItemCount(dBoxId, out var dBoxCnt) && dBoxCnt > 0;
                    bool canBuy = C.Cosmodrone_Buy && Task_ArtifactSearch.CanBuyDroneBoxes();
                    // MasterPriorityOverDrone がONかつ有効マスターがある場合は、ドローンに譲らずマスター受注を優先する(ユーザー要望)。
                    bool masterTakesPriority = C.MasterPriorityOverDrone && anyMasterEnabled;
                    if ((hasBox || canBuy) && !masterTakesPriority)
                    {
                        if (EzThrottler.Throttle("DronePriorityYield", 2000))
                            IceLogging.Verbose("ドローン稼働中(箱所持/購入可)のためミッション grab を見送り(ドローン優先)", tag);
                        return true; // grabせず次(ドローンタスク)へ譲る
                    }
                    if ((hasBox || canBuy) && masterTakesPriority && EzThrottler.Throttle("MasterOverDroneMsg", 5000))
                        IceLogging.Info("MasterPriorityOverDrone: 有効なマスターミッションがあるため、ドローンより優先してマスター受注へ進みます", tag);
                }

                // === マスターシップ最優先 (pre-handler) ===
                // マスターシップミッション(「MASTER：」, Unknown2)は SelectedTab=3 のタブでのみ MissionList に出現する
                // (GetBasicMissions/GetProvisionalMissions には含まれない。実機診断で確定)。
                // ユーザーがUIで有効化したMASTERミッションがある場合のみ、タブ3へ移動し最優先で受注する。
                // (config.Enabledで限定するので、RelicMode等で未有効化なら従来動作を維持する)
                if (type == MissionTypes.Standard && anyMasterEnabled)
                {
                    int curTab = -99;
                    try { curTab = AgentWKSMissionEx.selectedTab(); } catch { }

                    if (curTab != 3)
                    {
                        if (FrameThrottler.Throttle("Selecting mastership tab", 8))
                        {
                            missionInfo.MastershipMissions();    // ボタン20 (マスターシップタブ)
                            AgentWKSMissionEx.SetSelectedTab(3); // バックアップ: SelectedTab=3 直書き
                        }
                        return false; // タブ切替待ち
                    }

                    // タブ3: マスターシップミッションが MissionList(VisibleMissions) に出る
                    var visMaster = CosmicHandler.VisibleMissions();
                    var masterMission = MissionLibrary[MissionKind.Master]
                        .Where(m => C.MissionConfig.TryGetValue(m, out var c) && c.Enabled)
                        .FirstOrDefault(m => visMaster.Contains(m));
                    if (masterMission != 0)
                    {
                        _masterWaitSince = 0; // 見つかったので待機タイマをリセット
                        IceLogging.Info($"マスターシップミッションを最優先で受注します: {masterMission}", tag);
                        LogInfo(masterMission);
                        Insert_GrabMissionTask(masterMission);
                        return true;
                    }

                    // タブ3だが有効マスターが MissionList(VisibleMissions) に見当たらない。
                    // マスター完了直後の再出現待ち or クールダウンの可能性がある。一定時間出てこなければ
                    // 待機を打ち切り、通常ミッション選択へフォールスルーする。
                    // (これが無いと return false で永久ループし、COSMO MISSIONS画面をタブ3で開いたまま固まる不具合になる)
                    if (_masterWaitSince == 0)
                        _masterWaitSince = Environment.TickCount64;
                    long masterWaited = Environment.TickCount64 - _masterWaitSince;
                    if (masterWaited < MasterWaitTimeoutMs)
                    {
                        if (EzThrottler.Throttle("MasterWaitMsg", 3000))
                            IceLogging.Info($"有効マスターが MissionList に未反映。再出現を待機中... ({masterWaited / 1000}s / {MasterWaitTimeoutMs / 1000}s)", tag);
                        return false; // まだ待機(MissionList更新待ち)
                    }
                    // タイムアウト: マスターは現在受注不可(完了/クールダウンと判断)。タイマをリセットし通常ミッション選択へ進む。
                    _masterWaitSince = 0;
                    if (EzThrottler.Throttle("MasterWaitTimeout", 5000))
                        IceLogging.Info("有効マスターが規定時間内に出現しませんでした(完了/クールダウンと判断)。通常ミッション選択へ移行します", tag);
                    // フォールスルー: 下の OpenCorrectTab(...) 以降の通常ミッション処理へ進む
                }

                if (OpenCorrectTab(missionList, missionInfo))
                {
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
                        var job = Mission_Settings.SelectedJob;
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
                                    if (jobLv < sheetInfo.Level)
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
                            // [緊急診断] ギャザラー緊急受注の不具合切り分け(dalamud.logのみ・性能影響なし)。
                            // 緊急(候補)が在るのにvisibleMissions(UI上のCriticalタブ表示分)に出ない=タブ/ジョブサブタブ
                            // 未選択 or 表示反映前。SelectedJob・候補・UI表示の突き合わせを記録する。
                            IceLogging.Info($"[緊急診断] Criticalタブ評価: SelectedJob={Mission_Settings.SelectedJob} 候補missionList=[{string.Join(",", missionList)}] UI表示visible=[{string.Join(",", visibleMissions)}]", tag);

                            foreach (var missionId in missionList)
                            {
                                if (visibleMissions.Contains(missionId))
                                {
                                    IceLogging.Info($"[緊急診断] 緊急ミッション {missionId} を受注します", tag);
                                    LogInfo(missionId);
                                    Insert_GrabMissionTask(missionId);
                                    return true;
                                }
                            }

                            IceLogging.Info($"[緊急診断] 緊急候補({missionList.Count})は在るがUI(Criticalタブ)に未表示のため受注できず。タブ/ジョブサブタブ選択待ちか、Job不一致(候補のJobsにSelectedJob={Mission_Settings.SelectedJob}が含まれるか要確認)", tag);
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
        // 現在「接近中(grabしようとして移動中)」のミッションID。0=なし。
        // ミッションへ向かう途中でスタック(到達不能)した際、ハードストップ(Task_NavmeshMove)から
        // どのミッションをスキップすべきか参照するために公開する。
        public static uint CurrentGrabTarget = 0;

        // 未開拓エリア等で採取ノードが見つからない/到達できないミッションをunsupported登録し、現在のgrabシーケンスを
        // 中止してミッション選択(GrabMission)からやり直す。RefreshMissionLibraryがunsupportedを除外するので別ミッションが選ばれる。
        // ミッションはまだ掴んでいない(CheckForMovementRequiredはGrabMissionの前段)ので放棄(Abandon)は不要。
        // フラグ中心の周囲(半径内)を中心→外周のリング状にサンプリングする。各候補点を歩行可能面へスナップし、
        // そこから全周(rotationSteps)レイキャストして、釣り可能な水面を cast できる最初の立ち位置を返す。
        // ハードコード座標に依存しない動的釣り場探索の中核。プレイヤーがフラグ付近に居る前提(コリジョンが
        // ストリームイン済み)で呼ぶこと。
        private static bool TryFindDynamicFishingStand(Vector3 center, float radius, out Vector3 standPosition)
        {
            standPosition = Vector3.Zero;
            if (_fishRay == null || !P.Navmesh.Installed)
                return false;

            const int rings = 5;          // 中心から外周へのリング数
            const int perRing = 16;       // 各リングの方位サンプル数
            const int rotationSteps = 24; // 各立ち位置での全周レイキャスト分割数
            float angleStep = (2f * MathF.PI) / rotationSteps;

            for (int r = 0; r <= rings; r++)
            {
                float dist = radius * r / rings;
                int count = r == 0 ? 1 : perRing; // 中心は1点のみ
                for (int a = 0; a < count; a++)
                {
                    float bearing = (2f * MathF.PI) * a / count;
                    var probe = new Vector3(
                        center.X + (dist * MathF.Cos(bearing)),
                        center.Y,
                        center.Z + (dist * MathF.Sin(bearing)));

                    // 候補点を歩行可能面へスナップ(高低差のある地形に対応するため縦方向は広めに取る)。
                    var floor = P.Navmesh.PointOnFloor(probe, false, 10f);
                    if (!floor.HasValue)
                        continue;
                    var stand = floor.Value;

                    for (int i = 0; i < rotationSteps; i++)
                    {
                        if (_fishRay.IsFishableAt(stand, i * angleStep, out _))
                        {
                            standPosition = stand;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public static void SkipUnsupportedAndReselect(uint missionId)
        {
            if (missionId != 0)
                UnsupportedMissions.Ids.Add(missionId);
            CurrentGrabTarget = 0;
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            P.TaskManager.Tasks.Clear();
            SchedulerMain.State = IceState.GrabMission;
        }
        // 指定ミッションをunsupported登録するだけ(状態変更なし)。ハードストップの拠点脱出時に汚染ミッションを除外するため。
        public static void MarkUnsupported(uint id)
        {
            if (id != 0)
                UnsupportedMissions.Ids.Add(id);
        }
        // 既に取得済みの現行ミッション遂行中に到達不能でスタックした場合、そのミッションをunsupported登録して放棄し、
        // 別ミッションへ進む(ハードストップから呼ばれる)。RefreshMissionLibraryがunsupportedを除外するので再選択されない。
        public static void MarkCurrentMissionUnsupportedAndAbandon()
        {
            var id = CosmicHelper.CurrentLunarMission;
            if (id != 0)
                UnsupportedMissions.Ids.Add(id);
            CurrentGrabTarget = 0;
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            P.TaskManager.Tasks.Clear();
            Task_AbandonMission.ForceAbandon = true;
            SchedulerMain.State = IceState.AbandonMission;
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
        // 動的釣り探索用のレイキャスト判定器(遅延生成・シグネチャスキャンを1回だけ行う)
        private static FishingDebug? _fishRay;
        private static bool? CheckForMovementRequired(uint missionId)
        {
            string tag = "[Check Missions: Movement Check]";

            // このミッションへ向かって移動中であることを記録。途中でスタックしたらハードストップがこれをスキップ対象にする。
            CurrentGrabTarget = missionId;

            var sheetInfo = CosmicHelper.SheetMissionDict[missionId];
            var missionConfig = C.MissionConfig[missionId];

            if (missionConfig.ManualMode || UnsupportedMissions.Ids.Contains(missionId))
            {
                IceLogging.Info("Mission is currently in manual mode, or not supported. So not going to pathfind to it.", tag);
                return true;
            }
            else if (!P.Navmesh.Installed)
            {
                IceLogging.Error("HEY. YOU DIDN'T READ THE HELP ME PAGE. AND NOW YOU'RE MISSING NAVMESH. So... yeah... if things break this is why");
                return true;
            }
            else if (sheetInfo.IsCritical)
            {
                // 緊急(Critical/Red Alert)ミッションは赤警報の任務地で行い、専用NPC(レフレダ)でワープして入る。
                // 受注前にここで通常フラグへ向かわせると、任務地にノードが無く0件→unsupported登録→別ミッションへ流れて
                // 受注すらされず、Gather側のレフレダワープ処理に到達しなかった(実機報告)。緊急はここでは移動せず受注へ進め、
                // 任務地への移動(レフレダワープ)は受注後の実行側(Task_Gather/Task_TurninMission)に任せる。
                if (EzThrottler.Throttle("Critical no-premove", 2000))
                    IceLogging.Info("緊急ミッション: 受注前の移動はスキップし、受注後にレフレダで任務地へワープします", tag);
                return true;
            }
            else if (sheetInfo.IsGatherMission)
            {
                var missionTerritory = sheetInfo.TerritoryId;
                var mapId = sheetInfo.MapPosition;
                // 静的yamlルートが無ければ実機ノードから動的生成(Auxesia等の未yamlゾーン対応)
                var gatherInfo = GatheringRouteLoader.GetRouteOrDynamic(missionTerritory, mapId);

                if (gatherInfo.Count == 0)
                {
                    // ノードがまだ読み込まれていない可能性。ミッションフラグへ向かってノードをストリームインさせる
                    var flagWorld = GatheringRouteLoader.FlagToWorld(missionTerritory, mapId);
                    if (flagWorld.HasValue)
                    {
                        if (Player.DistanceTo(flagWorld.Value) < 15f)
                        {
                            IceLogging.Error("Arrived at the mission flag but found no gathering nodes nearby. Marking unsupported and re-selecting another mission.", tag);
                            SkipUnsupportedAndReselect(missionId);
                            return true;
                        }

                        IceLogging.Verbose("No authored route and no nodes loaded yet; heading to the mission flag so gathering nodes stream in.", tag);
                        // 到達判定を12mに緩める(既定2m)。未開拓/マップ端のミッションはFlagToWorldが低い不達点を指し、
                        // 2mまで近づけず移動タスクで永久に詰まる→上の「15m以内ならunsupported」判定に辿り着けずスタックする。
                        // 12mで到達扱いにすれば、次サイクルで15m判定が発火しミッションをスキップして別ミッションへ進める。
                        Task_NavmeshMove.Enqueue_NavmeshTask(flagWorld.Value, distance: 12f);
                        return true;
                    }

                    IceLogging.Error("Could not resolve a route, live nodes, or the mission flag location. Marking unsupported and re-selecting another mission.", tag);
                    SkipUnsupportedAndReselect(missionId);
                    return true;
                }
                else
                {
                    var startNode = gatherInfo[0];

                    foreach (var node in gatherInfo)
                    {
                        if (Player.DistanceTo(node.Position) < 5)
                        {
                            IceLogging.Info("We're close enough to the node! So continuing onto grabbing the mission", tag);
                            return true;
                        }
                    }

                    IceLogging.Verbose("If we've gotten this far, that means we need to figure out a path to go to the node. Doing so now", tag);
                    Task_NavmeshMove.Enqueue_NavmeshTask(startNode.LandZone);
                    return true;
                }
            }
            else if (sheetInfo.IsFishMission)
            {
                var location = sheetInfo.MapPosition;
                var territory = sheetInfo.TerritoryId;
                float radius = MathF.Max(sheetInfo.Radius, 15f);

                // ① カスタム釣り穴(Personal_FishLocation)が設定されていれば最優先で使用する。
                var customFishingHole = C.Personal_FishLocation.Where(x => x.MapCoords == location).FirstOrDefault();
                if (customFishingHole?.WorldPosition is { } customLoc)
                {
                    if (Player.DistanceTo(customLoc) < 3)
                    {
                        IceLogging.Info($"カスタム釣り穴に到達しました。 {customLoc}", tag);
                        randomFishingHole = Vector3.Zero;
                        return true;
                    }
                    IceLogging.Verbose($"カスタム釣り穴へ移動します: {customLoc}", tag);
                    Task_NavmeshMove.Enqueue_NavmeshTask(customLoc);
                    return true;
                }

                // ② 動的探索: ハードコード座標(MoonFishingLocations)に依存せず、ミッションフラグ中心を基点に
                //    レイキャストで「釣り可能な水面がある立ち位置」を探す。Auxesia等の未収録ゾーンでも動作する。
                var center = GatheringRouteLoader.FlagToWorld(territory, location);
                if (!center.HasValue)
                {
                    if (EzThrottler.Throttle("Fishing flag unresolved", 5000))
                        IceLogging.Error($"釣り: フラグのワールド座標を解決できませんでした。unsupported登録します。 Mission:{missionId} Flag:{location} Territory:{territory}", tag);
                    SkipUnsupportedAndReselect(missionId);
                    return true;
                }

                _fishRay ??= new FishingDebug();

                // ②-a まだ基点付近に居ない → 基点へ移動して周辺ジオメトリ(コリジョン)をストリームインさせる。
                if (Player.DistanceTo(center.Value) > radius + 5f)
                {
                    if (EzThrottler.Throttle("Fishing dynamic move-to-flag", 1000))
                        IceLogging.Verbose($"釣り: フラグ中心へ移動して地形をストリームインさせます {center.Value} (半径{radius:N0})", tag);
                    Task_NavmeshMove.Enqueue_NavmeshTask(center.Value, distance: 5f);
                    return true;
                }

                // ②-b 現在地から釣り可能なら完了。実際の釣行・向き調整はTask_Fishingが担当する。
                if (_fishRay.IsFishable() || _fishRay.FindFishableLocation(out _, 36))
                {
                    IceLogging.Info("釣り: 釣り可能な水面を確認。受注/釣行へ進みます。", tag);
                    randomFishingHole = Vector3.Zero;
                    return true;
                }

                // ②-c 探索で決めた目的地へ移動中なら継続。到達済みでまだ釣れない場合はリセットして再探索する。
                if (randomFishingHole != Vector3.Zero)
                {
                    if (Player.DistanceTo(randomFishingHole) < 2.5f)
                    {
                        randomFishingHole = Vector3.Zero;
                    }
                    else
                    {
                        Task_NavmeshMove.Enqueue_NavmeshTask(randomFishingHole, distance: 1.5f);
                        return true;
                    }
                }

                // ②-d 半径内をサンプリングして釣り可能な立ち位置を探索し、見つかれば移動する。
                if (TryFindDynamicFishingStand(center.Value, radius, out var standPos))
                {
                    IceLogging.Info($"釣り: 釣り可能な立ち位置を発見。移動します {standPos}", tag);
                    randomFishingHole = standPos;
                    Task_NavmeshMove.Enqueue_NavmeshTask(standPos, distance: 1.5f);
                    return true;
                }

                // ②-e フラグ半径内に釣り可能な水面が見つからない → unsupported登録。
                if (EzThrottler.Throttle("Fishing no water found", 5000))
                    IceLogging.Error($"釣り: フラグ半径内に釣り可能な水面が見つかりませんでした。unsupported登録します。 Mission:{missionId}", tag);
                SkipUnsupportedAndReselect(missionId);
                return true;
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
        }
        private static int retryCheck = 0;
        private static bool? GrabMission(uint missionId, bool reroll = false)
        {
            string tag = "[Check Missions: Grab Mission]";

            if (CosmicHelper.CurrentLunarMission != 0)
            {
                retryCheck = 0;
                Mission_Settings.ResetNodeCounter();
                // 採取系 static 計測(タイムアウト/スキル使用回数等)を新ミッション開始前に一括クリアし、
                // 前ミッションの残骸が次ミッション直後に誤動作するのを防ぐ。
                Task_Gather.ResetGatherState();

                if (reroll)
                {
                    SchedulerMain.State = IceState.AbandonMission;
                    Task_AbandonMission.ForceAbandon = true;
                }
                else
                {
                    SchedulerMain.State = IceState.ExecutingMission;
                    Task_AbandonMission.ForceAbandon = false;
                    // ミッションを実際に受注できたので、リロール用の出現回数/直前放棄ランクの累積をクリア
                    // (惑星/ジョブをまたいだ古い累積で誤った頻出判定をしないため)。
                    Mission_Settings.missionApperenceCount.Clear();
                    Mission_Settings.previousAbandonRank = 0;
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

                    if (OpenCorrectTab(viableMissions, missionInfo))
                    {
                        IceLogging.Verbose("On the correct tab, we're going to see the total mission count", tag);
                        var allmissions = CosmicHandler.AllMissions();
                        IceLogging.Verbose($"All mission count: {allmissions.Count()} | Goal: {missionId}");
                        foreach (var mission in allmissions.OrderBy(x => CosmicHelper.SheetMissionDict[x].Rank))
                        {
                            var sheetInfo = CosmicHelper.SheetMissionDict[mission];
                            IceLogging.Verbose($"ID: [{mission}] | Rank: [{sheetInfo.Rank}]", tag, true);
                        }


                        if (allmissions.Contains(missionId))
                        {
                            if (EzThrottler.Throttle("Selecting Mission"))
                                InitiateMission(missionId);
                        }
                        else
                        {
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
                    else
                    {
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
                                case 6: // EX+ (マスターシップ) も最上位グループ(AEx)として扱う
                                case 5: AExRank.Add(missionId); break;
                                case 4: ARank.Add(missionId); break;
                                case 3: BRank.Add(missionId); break;
                                case 2: CRank.Add(missionId); break;
                                case 1:
                                default: DRank.Add(missionId); break;
                            }
                        }

                        bool CheckARanks = (MissionLibrary[MissionKind.Master].Count > 0 || MissionLibrary[MissionKind.ExPlus].Count > 0 || MissionLibrary[MissionKind.Ex].Count > 0 || MissionLibrary[MissionKind.A].Count > 0) && (AExRank.Count > 0 || ARank.Count > 0);
                        bool CheckBRanks = (MissionLibrary[MissionKind.B].Count > 0 && BRank.Count > 0);
                        bool CheckCRanks = (MissionLibrary[MissionKind.C].Count > 0 && CRank.Count > 0);
                        bool CheckDRanks = (MissionLibrary[MissionKind.D].Count > 0 && DRank.Count > 0);

                        IceLogging.Verbose($"[Ex] = {AExRank.Count()}\n" +
                            $"[A] = {ARank.Count()}\n" +
                            $"[B] = {BRank.Count()}\n" +
                            $"[C] = {CRank.Count()}\n" +
                            $"[D] = {DRank.Count()}", tag);

                        List<MissionKind> ranks = new() { MissionKind.Master, MissionKind.ExPlus, MissionKind.Ex, MissionKind.A, MissionKind.B, MissionKind.C, MissionKind.D };
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

                        var random = _random; // クラス共有の Random を再利用(ローカル new によるシード重複/無駄生成を回避)
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
        private static int JobTab(uint job)
        {
            int jobUnlocked = 0;
            Dictionary<uint, int> jobTab = new();
            for (int i = 0; i < CosmicHelper.SupportedJobs.Count(); i++)
            {
                var currentJob = CosmicHelper.SupportedJobs[i];
                var level = Player.GetLevel((Job)currentJob);
                if (level != 0)
                {
                    IceLogging.Verbose($"{currentJob} - tab: {jobUnlocked}");
                    jobTab[currentJob] = jobUnlocked;
                    jobUnlocked++;
                }
                else
                {
                    jobTab[currentJob] = 0;
                }
            }

            return jobTab[job];
        }
        private static unsafe bool OpenCorrectTab(List<uint> missionList, WKSMission missionAddon)
        {
            string tag = "Opening Correct Tab";

            var hudInfo = CosmicHandler.HudInfo();

            // goalTab: 0=Basic / 1=Provisional(マスターシップミッションタブ) / 2=Critical。
            // Critical最優先、次にProvisional(天候/時間/シーケンシャル)。
            // 従来はProvisionalタブ(中間tab1)を一切開かず、マスターシップ/Provisionalミッションを
            // 受注できなかった(OpenCorrectTabがgoalTab=0/2しか取らず、非Criticalは常にBasicタブへ強制)。修正。
            // 全ミッションがマスターシップ(GrabMissionが単一MASTERを渡す場合)ならタブ3。
            // 混在リスト(Standard巡回)はマスターシップ判定しない(pre-handler側で処理済)。
            bool allMastership = missionList.Count > 0 && missionList.All(m => CosmicHelper.SheetMissionDict.TryGetValue(m, out var mi) && mi.IsMastership);
            var goalTab = 0;
            if (allMastership)
            {
                goalTab = 3;
            }
            else
            {
                foreach (var mission in missionList)
                {
                    var info = CosmicHelper.SheetMissionDict[mission];
                    if (info.IsCritical)
                    {
                        goalTab = 2;
                        break;
                    }
                    if (info.IsProvisional)
                        goalTab = 1;
                }
            }

            // goalTab==3(マスターシップ)は hudInfo.SelectedTabIndex が追随しないことがあるため agent->SelectedTab で判定。
            int currentTab = hudInfo.SelectedTabIndex;
            if (goalTab == 3)
            {
                try { currentTab = AgentWKSMissionEx.selectedTab(); } catch { }
            }
            if (currentTab != goalTab)
            {
                if (FrameThrottler.Throttle("Selecting job", 8))
                {
                    IceLogging.Verbose($"Selecting mission category tab {goalTab} (0=Basic / 1=Provisional / 2=Critical / 3=Mastership)", tag);
                    if (goalTab == 2)
                        missionAddon.CriticalMissions();
                    else if (goalTab == 3)
                    {
                        missionAddon.MastershipMissions();
                        AgentWKSMissionEx.SetSelectedTab(3);
                    }
                    else if (goalTab == 1)
                        missionAddon.ProvisionalMissions();
                    else
                        missionAddon.BasicMissions();
                }
                return false;
            }
            else
            {
                // SelectedJobIndex のインデックス空間が環境で異なりうる(生の ClassJob 順 SelectedJob-8 か、
                // 習得済みジョブを詰めた JobTab() か)。どちらかに一致すれば「正しいジョブサブタブに居る」とみなし、
                // 中間ジョブ未習得時などに恒久的に不一致となって OpenCorrectTab が永久 false→棒立ちするのを防ぐ。
                var selectedJobTab = (int)Mission_Settings.SelectedJob - 8;
                var packedJobTab = JobTab(Mission_Settings.SelectedJob);
                if (hudInfo.SelectedJobIndex != selectedJobTab && hudInfo.SelectedJobIndex != packedJobTab)
                {
                    if (FrameThrottler.Throttle("Tab swapping", 8))
                    {
                        IceLogging.Verbose("We're not on the standard tab, so we're going to initate swapping to it", tag);
                        // 公式0.0.78.1より移植: UIクリック(SelectClass[].Select())はindexズレで選択が永久不成立→棒立ちしうる。
                        // ゲーム内部API(SetSelectedJobTab)でジョブサブタブを決定的に選択する。上段タブ(basic/critical)は
                        // 本メソッドが別管理しているので selectBasicTab:false で温存。agent取得不可時は従来のUIクリックにフォールバック。
                        var agent = AgentWKSMission.Instance();
                        if (agent != null && AgentWKSMissionEx.SetSelectedJobTab(agent, (byte)Mission_Settings.SelectedJob, selectBasicTab: false))
                        {
                            // API成功
                        }
                        else
                        {
                            missionAddon.SelectClass[JobTab(Mission_Settings.SelectedJob)].Select();
                        }
                    }
                    return false;
                }
                else
                {
                    IceLogging.Verbose($"Job tab is on the correct one. Goal was job: {Mission_Settings.SelectedJob}", tag);
                    return true;
                }
            }
        }
    }
}
