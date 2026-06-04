using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using ICE.Sounds;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using System.Collections.Generic;
using System.Linq;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static FFXIVClientStructs.FFXIV.Client.Game.WKS.WKSManager;

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

                    if (CosmicHelper.CriticalLocations.TryGetValue(id, out var location) && location.RawLocation != Vector3.Zero)
                    {
                        if (Player.DistanceTo(location.RawLocation) < 75)
                        {
                            IceLogging.Verbose("We're close enough to the base location that we don't need to do any fancy traveling, going to check if we need to interact", tag);
                            P.TaskManager.Insert(() => RedAlert_CloseToTurnin(), "Checking to make sure we're close enough");
                        }
                        else
                        {
                            IceLogging.Verbose("We're far enough away that we need to consider taking the npc for getting there, so going to do so");
                            P.TaskManager.Insert(() => Task_NavmeshMove.Enqueue_RedAlertNavmesh(location.RawLocation, distance: 75, missionId: id), "Checking to make sure we're close enough");
                        }
                    }
                    else
                    {
                        // CriticalLocations未登録(Auxesia等)。Lefledaが登録された惑星なら、職業名でメニューを選ぶ動的方式で移動する。
                        bool hasLefleda = NpcData.MoonNpcs.TryGetValue(Player.Territory.RowId, out var planet)
                                          && planet.ContainsKey(NpcData.NpcType.RedAlert);
                        if (hasLefleda)
                        {
                            if (EzThrottler.Throttle("Auxesia RA route msg", 2000))
                                IceLogging.Info("緊急ミッション(座標未登録)。Lefledaで職業に応じた任務地を選んでワープします(動的)", tag);
                            P.TaskManager.Insert(() => RedAlert_AuxesiaTravel(id), "Auxesia RedAlert: Lefleda職業選択→ワープ");
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

            // === 一時診断: 緊急ミッションの納品地点を動的に特定するため、マップマーカー/オブジェクトを記録する ===
            // (Auxesia緊急ミッションは CriticalLocations 未登録のためここで詰まる。マーカーから動的移動できるか調べる)
            if (EzThrottler.Throttle("RedAlertDiag", 2000))
            {
                try
                {
                    var id = CosmicHelper.CurrentLunarMission;
                    string mname = CosmicHelper.SheetMissionDict.TryGetValue(id, out var si) ? si.Name : "?";
                    var pos = Player.Object?.Position ?? Vector3.Zero;
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[RedAlert] terr={Player.Territory.RowId} mission={id}('{mname}') playerPos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})\n");
                    var markers = Scheduler.Tasks.Task_ArtifactSearch.GetAllEventMarkers();
                    sb.Append($"  markers({markers.Count}): ");
                    foreach (var m in markers)
                        sb.Append($"[icon={m.IconId} pos=({m.Position.X:F1},{m.Position.Y:F1},{m.Position.Z:F1})] ");
                    sb.Append("\n");
                    var cp = Utils.TryGetObjectCollectionPoint();
                    if (cp != null)
                        sb.Append($"  collectionPoint: name='{cp.Name}' dataId={cp.DataId} pos=({cp.Position.X:F1},{cp.Position.Y:F1},{cp.Position.Z:F1})\n");
                    else
                        sb.Append("  collectionPoint: (null=未ロード/遠い)\n");
                    System.IO.File.AppendAllText(@"\\rio-pc\DevPlugins\master_diag.log", sb.ToString());
                }
                catch { }
            }

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
        // ジョブID(8〜18)→ JP名(鍛冶師/調理師/漁師 等)。Lefledaメニューの選択肢テキストと照合するため。
        private static string GetJobJpName(uint jobId)
        {
            try
            {
                var cj = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(jobId);
                return cj?.Name.ExtractText() ?? "";
            }
            catch { return ""; }
        }

        // Auxesia(及びCriticalLocations未登録の惑星)の緊急ミッション納品移動。
        // 座標ハードコード不要の動的方式: Lefledaのメニューで「ミッションの職業名を含む選択肢」を選んでワープ →
        // ワープ後は物資集積所(納品オブジェクト)が見えるので true を返し、後続の Mission_TurninV2 に納品を任せる。
        public static bool? RedAlert_AuxesiaTravel(uint missionId)
        {
            string tag = "[RedAlert Auxesia]";
            var territoryId = Player.Territory.RowId;
            if (!NpcData.MoonNpcs.TryGetValue(territoryId, out var planet)
                || !planet.TryGetValue(NpcData.NpcType.RedAlert, out var lefleda))
                return true; // Lefleda未登録 → 何もできないので後続へ

            // ワープ後: 納品オブジェクト(物資集積所)が出現していれば到着 → 納品は Mission_TurninV2 が行う
            if (Utils.TryGetObjectCollectionPoint() != null)
                return true;

            // Lefledaメニュー(SelectString): ミッションの職業名を含む選択肢を選ぶ
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
                    if (EzThrottler.Throttle("Auxesia RA select", 500))
                    {
                        try { System.IO.File.AppendAllText(DiagLog, $"[RedAlert Auxesia] mission={missionId} job='{jobName}' → 選択肢[{pick}]='{entries[pick]}' を選択\n"); } catch { }
                        ss.Entries[pick].Select();
                    }
                }
                else
                {
                    if (EzThrottler.Throttle("Auxesia RA nomatch", 2000))
                        try { System.IO.File.AppendAllText(DiagLog, $"[RedAlert Auxesia] mission={missionId} job='{jobName}' に一致する選択肢なし: [{string.Join(" | ", entries)}]\n"); } catch { }
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

            // メニューが出ていない → Lefledaへ移動して話しかける
            if (Player.DistanceTo(lefleda.Location_Circle) < 5)
            {
                var npc = Svc.Objects.FirstOrDefault(x => x.DataId == lefleda.NpcId);
                if (npc != null)
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
            else
            {
                Task_NavmeshMove.Task_NavTo(lefleda.Location_Circle, false, 3.0f);
                return false;
            }
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

                // Log the results
                if (C.MissionConfig.TryGetValue(PreviousMissionId, out var config))
                {
                    if (config.BestTime != double.MaxValue)
                        IceLogging.Info($"Mission [{PreviousMissionId}] [{CosmicHelper.SheetMissionDict[PreviousMissionId].Name}] completed in {duration:mm\\:ss\\.ff} | Best: {TimeSpan.FromSeconds(config.BestTime):mm\\:ss\\.ff} | Avg: {TimeSpan.FromSeconds(config.AverageTime):mm\\:ss\\.ff}", $"{tag} [Mission Timer]");
                }

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
                            Mission_Settings.TurninState = rank switch
                            {
                                MissionRank.Gold => TurninState.Gold,
                                MissionRank.Silver => TurninState.Silver,
                                MissionRank.Bronze => TurninState.Bronze,
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
            var WKSInstance = WKSManager.Instance();
            WKSInstance->MissionModule->ReportMission();
        }

        // ============================================================================
        // === 一時診断: 緊急ミッション(レッドアラート)の納品フロー全体を捕捉する ===
        // 目的: どのNPCにアクセスし/どんな選択肢が出て/選択時にどんなシグナルが飛び/その後どこへワープし/
        //       納品ノードがどこか、を全部ログに残し、ICE単独で同じ操作を再現できるデータを集める。
        // 性能低下は許容(ユーザー了承)。ICE.Loadから RegisterRedAlertDiag() を呼ぶ。
        // ============================================================================
        private const string DiagLog = @"\\rio-pc\DevPlugins\master_diag.log";
        private static bool _redAlertDiagRegistered = false;
        private static System.Numerics.Vector3 _lastDiagPos = System.Numerics.Vector3.Zero;
        private static long _lastDiagPosTick = 0;
        private static long _diagActiveUntil = 0; // メニュー操作後この時刻まで座標を細かく記録(ワープ追跡)

        // 現在の月ミッションを "mission=ID('名前')[Critical]" 形式で返す(ログ対応付け用)
        private static string DiagMissionTag()
        {
            try
            {
                var id = CosmicHelper.CurrentLunarMission;
                if (id == 0) return "mission=0(なし)";
                if (CosmicHelper.SheetMissionDict.TryGetValue(id, out var si))
                    return $"mission={id}('{si.Name}'){(si.IsCritical ? "[緊急]" : "")}";
                return $"mission={id}(?)";
            }
            catch { return "mission=?"; }
        }

        public static void RegisterRedAlertDiag()
        {
            if (_redAlertDiagRegistered) return;
            try
            {
                Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "SelectString", OnDiagSelectStringSetup);
                Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostReceiveEvent, "SelectString", OnDiagSelectStringEvt);
                Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "Talk", OnDiagTalkSetup);
                Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "SelectYesno", OnDiagYesnoSetup);
                _redAlertDiagRegistered = true;
            }
            catch { }
        }

        private static void OnDiagSelectStringSetup(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            try
            {
                _diagActiveUntil = Environment.TickCount64 + 20000; // 以後20秒は座標を細かく追う
                string target = Svc.Targets.Target?.Name?.TextValue ?? "(ターゲット無し)";
                var pos = Player.Object?.Position ?? System.Numerics.Vector3.Zero;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[RAdiag] === SelectString(NPCメニュー)開く === {DiagMissionTag()} NPC='{target}' playerPos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2})\n");
                if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
                {
                    int i = 0;
                    foreach (var e in ss.Entries) { sb.Append($"[RAdiag]     選択肢[{i}] = '{e.Text}'\n"); i++; }
                }
                System.IO.File.AppendAllText(DiagLog, sb.ToString());
            }
            catch { }
        }

        private static void OnDiagSelectStringEvt(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            try
            {
                if (args is Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonReceiveEventArgs e)
                {
                    int t = (int)e.AtkEventType;
                    if (t == 8 || t == 9) return; // MouseOver/Out(ホバー)は除外
                    _diagActiveUntil = Environment.TickCount64 + 20000;
                    System.IO.File.AppendAllText(DiagLog, $"[RAdiag] SelectString選択シグナル {DiagMissionTag()} type={t}({e.AtkEventType}) param={e.EventParam}  ← ICEはこのindexで Entries[param].Select() すれば同じ\n");
                }
            }
            catch { }
        }

        private static void OnDiagTalkSetup(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            try
            {
                _diagActiveUntil = Environment.TickCount64 + 20000;
                string target = Svc.Targets.Target?.Name?.TextValue ?? "(ターゲット無し)";
                System.IO.File.AppendAllText(DiagLog, $"[RAdiag] Talk(会話)開く NPC='{target}'\n");
            }
            catch { }
        }

        private static void OnDiagYesnoSetup(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
        {
            try
            {
                _diagActiveUntil = Environment.TickCount64 + 20000;
                System.IO.File.AppendAllText(DiagLog, "[RAdiag] SelectYesno(確認ダイアログ)開く\n");
            }
            catch { }
        }

        // PlayerHandlers.Tick から毎フレーム呼ぶ。緊急ミッション中、またはメニュー操作後20秒間、
        // プレイヤー座標(ワープ追跡)とターゲット(納品ノードへのアクセス先)を細かく記録する。
        public static void RedAlertDiagTick()
        {
            try
            {
                if (!PlayerHelper.IsInCosmicZone()) return;
                var lp = Player.Object;
                if (lp == null) return;

                // 記録対象か判定: 緊急ミッション中 or メニュー操作後20秒以内
                var id = CosmicHelper.CurrentLunarMission;
                bool critical = id != 0 && CosmicHelper.SheetMissionDict.TryGetValue(id, out var si) && si.IsCritical;
                bool active = critical || Environment.TickCount64 < _diagActiveUntil;
                if (!active) { _lastDiagPos = lp.Position; return; }

                long now = Environment.TickCount64;
                if (now - _lastDiagPosTick < 400) return;
                var pos = lp.Position;

                // ワープ検知(前回サンプルから大きく飛んだ)
                if (_lastDiagPos != System.Numerics.Vector3.Zero && System.Numerics.Vector3.Distance(_lastDiagPos, pos) > 20f)
                    System.IO.File.AppendAllText(DiagLog, $"[RAdiag] ★ワープ検知 ({_lastDiagPos.X:F2},{_lastDiagPos.Y:F2},{_lastDiagPos.Z:F2}) → ({pos.X:F2},{pos.Y:F2},{pos.Z:F2})\n");

                _lastDiagPosTick = now;
                _lastDiagPos = pos;

                var tgt = Svc.Targets.Target;
                string tinfo = tgt != null
                    ? $" │ target='{tgt.Name?.TextValue}' dataId={tgt.DataId} tgtPos=({tgt.Position.X:F2},{tgt.Position.Y:F2},{tgt.Position.Z:F2}) dist={System.Numerics.Vector3.Distance(pos, tgt.Position):F1}"
                    : "";
                string cflag = critical ? "緊急中" : "メニュー後";
                System.IO.File.AppendAllText(DiagLog, $"[RAdiag] [{cflag}] {DiagMissionTag()} pos=({pos.X:F2},{pos.Y:F2},{pos.Z:F2}){tinfo}\n");
            }
            catch { }
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

            var sheetInfo = CosmicHelper.SheetMissionDict[PreviousMissionId];

            if (C.RemoveAfterGold && isGold)
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

                            C.MissionConfig[mission].Enabled = false;
                        }
                    }
                    C.Save();
                }
            }
            if (C.RemoveAfterGold && !isGold)
            {
                foreach (var prevMission in sheetInfo.SequenceMissions_Previous)
                    C.MissionConfig[prevMission].Enabled = true;

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

            if (Mission_Settings.Mode == ModeSelect.LevelMode && Utils.HasPlugin("Stylist"))
            {
                var jobId = (uint)Player.Job;

                if (CosmicHelper.CrafterJobList.Contains(jobId))
                {
                    IceLogging.Info("Executing command [/stylist crafter]");
                    ExecuteCommand("/stylist crafter");
                }
                else if (CosmicHelper.GatheringJobList.Contains(jobId))
                {
                    IceLogging.Info("Executing command [/stylist gatherer]");
                    ExecuteCommand("/stylist gatherer");
                }
                P.TaskManager.EnqueueDelay(500);
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
        }

        public static bool? ClearAllPostTask()
        {
            P.TaskManager.Tasks.Clear();
            IceLogging.Info("All task post turning in mission have been cleared. We should have a clean slate now");
            return true;
        }
    }
}
