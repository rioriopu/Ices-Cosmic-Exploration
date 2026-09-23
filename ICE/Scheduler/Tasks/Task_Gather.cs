using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using ICE.Utilities.GatheringHelper.RouteLoader;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.ConfigFiles.Config;
using static ICE.Ui.MainUi.Settings.GatherSettings;
using MissionRank = FFXIVClientStructs.FFXIV.Client.Game.WKS.WKSMissionModule.MissionRank;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Gather
    {
        private static int _lastCollectability = -1;
        private static DateTime _lastCollectProgress = DateTime.MinValue;

        // ノード到達タイムアウト: 一定時間あるノードへ到達できない(登れない高所ノード等)場合、発現ノードへ向け直す。
        private static uint _lastGatherMissionId = 0;
        private static int _stuckNodeIndex = -1;
        private static DateTime _stuckNodeSince = DateTime.MinValue;
        private const double NodeSkipSeconds = 8.0;
        // 採取ノードが0件のまま続いた時間。枯渇/未読込で永久にスキャンし続けるのを防ぎ、一定時間で納品/放棄に決着させる。
        private static DateTime _zeroNodeSince = DateTime.MinValue;
        private const double ZeroNodeResolveSeconds = 30.0;
        // 到着済み(navmesh停止)だが採取窓が開かない状態のタイムアウト。navmesh停止中は移動のスタック検知が効かないため。
        private static int _arrivedNodeIndex = -1;
        private static DateTime _arrivedSince = DateTime.MinValue;
        private const double ArrivedInteractTimeoutSeconds = 12.0;
        // 発現ノードが付近に1つも無い状態が続いた時間。一定時間で Stellar Return により拠点へ戻って仕切り直す。
        private static DateTime _noLiveNodeSince = DateTime.MinValue;
        private const double NoLiveNodeDejonSeconds = 25.0;

        // 読み込み済みルートのノードを書き換えないよう、実位置だけ差し替えた複製を作る
        private static NodeInfo CloneNodeAt(NodeInfo src, Vector3 position) => new()
        {
            NodeId = src.NodeId, Position = position, LandZone = src.LandZone,
            RadiusStart = src.RadiusStart, RadiusEnd = src.RadiusEnd,
            MinDistance = src.MinDistance, MaxDistance = src.MaxDistance, FanHeight = src.FanHeight,
        };

        // 次ノードへ進める共通処理(各タイムアウト計測もリセット)
        private static void AdvanceToNextNode(int nodeCount)
        {
            Mission_Settings.nodeCounter++;
            if (Mission_Settings.nodeCounter >= nodeCount || nodeCount <= 0)
                Mission_Settings.nodeCounter = 0;
            _stuckNodeIndex = -1;
            _arrivedNodeIndex = -1;
        }

        // 採取に関わる計測/状態を一括リセットする。新しいミッションの開始時に呼び、前ミッションの計測が残って誤動作するのを防ぐ。
        public static void ResetGatherState()
        {
            _noLiveNodeSince = DateTime.MinValue;
            _zeroNodeSince = DateTime.MinValue;
            _arrivedSince = DateTime.MinValue;
            _arrivedNodeIndex = -1;
            _stuckNodeSince = DateTime.MinValue;
            _stuckNodeIndex = -1;
            Mission_Settings.ResetSkillUseAmount();
        }

        // 採取で詰まった時(到着したのに採取窓が開かない/ノードが枯渇・未スポーン/到達不能)の共通解決処理。
        // ①実際に発現している(光っている)採取ポイントが付近に在れば、そこへ向け直す。
        // ②発現ポイントが一定時間1つも無ければ、Stellar Return で拠点へ戻って状況をリセットする。
        private static void ResolveStuckGatherNode(List<NodeInfo> gatherInfo, string reason)
        {
            // ① プレイヤー付近(120m)またはルート座標付近(50m)の発現ノードを座標ベースで探す。
            //    ルートの NodeId 一致は要求しない(動的/静的ルートの NodeId が実ノードと一致しない場合でも拾えるように)。
            var live = Svc.Objects
                .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable)
                .Where(o => Player.DistanceTo(o.Position) < 120f
                            || gatherInfo.Any(g => Vector3.Distance(g.Position, o.Position) < 50f))
                .OrderBy(o => Player.DistanceTo(o.Position))
                .FirstOrDefault();

            if (live != null)
            {
                _noLiveNodeSince = DateTime.MinValue;
                _arrivedNodeIndex = -1;
                _stuckNodeIndex = -1;

                // ルートに同一 BaseId が在れば、そのindexに合わせて既存のルート移動・採取に乗せる。
                int idx = gatherInfo.FindIndex(g => g.NodeId == live.BaseId);
                if (idx >= 0)
                {
                    Mission_Settings.nodeCounter = idx;
                    if (EzThrottler.Throttle("StuckRedirectLog", 3000))
                        IceLogging.Info($"採取で詰まりました({reason})。発現中ノードへ向け直します(route idx={idx} BaseId={live.BaseId})", "[Gather: StuckResolve]");
                    return;
                }

                // ルートに無い実ノード → 近ければ直接ターゲット&インタラクト、遠ければそこへ移動する。
                if (Player.DistanceTo(live.Position) <= 6f)
                {
                    if (!Player.IsJumping && EzThrottler.Throttle("StuckDirectInteract", 500))
                    {
                        Utils.TargetgameObject(live);
                        Utils.InteractWithObject(live);
                    }
                    if (EzThrottler.Throttle("StuckRedirectLog", 3000))
                        IceLogging.Info($"採取で詰まりました({reason})。目の前の発現ノード(BaseId={live.BaseId})を直接採取します", "[Gather: StuckResolve]");
                }
                else
                {
                    if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                        P.Navmesh.Stop();
                    Task_NavmeshMove.Enqueue_NavmeshTask(live.Position, false, 3f);
                    if (EzThrottler.Throttle("StuckRedirectLog", 3000))
                        IceLogging.Info($"採取で詰まりました({reason})。発現中ノード(BaseId={live.BaseId})へ移動します({Player.DistanceTo(live.Position):F0}m)", "[Gather: StuckResolve]");
                }
                return;
            }

            // ② 発現ノードが付近に1つも無い。短時間は再スポーン/読込を待ちつつ次ノードへ進め、一定時間続いたら拠点へ戻る。
            if (_noLiveNodeSince == DateTime.MinValue)
                _noLiveNodeSince = DateTime.Now;

            if ((DateTime.Now - _noLiveNodeSince).TotalSeconds < NoLiveNodeDejonSeconds)
            {
                if (EzThrottler.Throttle("StuckWaitLog", 3000))
                    IceLogging.Info($"発現中の採取ポイントが付近に見当たりません({reason})。{NoLiveNodeDejonSeconds:F0}秒待っても出なければ拠点へ戻ります(経過 {(DateTime.Now - _noLiveNodeSince).TotalSeconds:F0}s)", "[Gather: StuckResolve]");
                AdvanceToNextNode(gatherInfo.Count);
                return;
            }

            _noLiveNodeSince = DateTime.MinValue;
            // 採取(Gather)状態の時だけ拠点へ戻る。報告/放棄/受注の最中に残存タスクとして走った場合に State を上書きしない。
            bool canStellarReturn = C.UseHubReturn
                && !(C.AvoidStellarReturn && !C.AvoidStellarReturnExceptHub)
                && CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out _)
                && SchedulerMain.State == IceState.Gather;
            if (canStellarReturn)
            {
                IceLogging.Warning($"発現中の採取ポイントが付近に{NoLiveNodeDejonSeconds:F0}秒以上見当たらないため、Stellar Return で拠点へ戻って仕切り直します({reason})。", "[Gather: StuckResolve]");
                _arrivedNodeIndex = -1;
                _stuckNodeIndex = -1;
                if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
                P.TaskManager.Tasks.Clear();
                SchedulerMain.State = IceState.HubReturn; // HubReturn → Stellar Return → Start → ミッション再選択
                return;
            }
            AdvanceToNextNode(gatherInfo.Count);
        }

        public static void Enqueue()
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return;
            // 新しいミッションに変わったら、前ミッションの採取タイムアウト計測やスキル使用回数を持ち越さない。
            if (_lastGatherMissionId != CosmicHelper.CurrentLunarMission)
            {
                _lastGatherMissionId = CosmicHelper.CurrentLunarMission;
                ResetGatherState();
            }
            // === 緊急(Red Alert)採取: 受注後、任務地へ移動してから採取する ===
            // 緊急採取は赤警報の任務地で行う。まだ任務地(物資集積所が見える場所)に居ない場合は通常のフラグへ向かわず、
            // 座標登録済みなら赤警報NPC経由のナビで、未登録(Auxesia 等)ならレフレダで職業を選んでワープする。
            var enqueueMissionInfo = CosmicHelper.CurrentMissionInfo;
            bool isCriticalGather = enqueueMissionInfo != null && enqueueMissionInfo.Attributes.HasFlag(MissionAttributes.Critical);
            if (!Svc.Condition[ConditionFlag.Gathering] && isCriticalGather && Utils.TryGetObjectCollectionPoint() == null)
            {
                if (GatheringUtil.CriticalSpots.TryGetValue(enqueueMissionInfo.Critical_MapKey, out var criticalLoc) && criticalLoc.WorldCords != Vector3.Zero)
                {
                    // 座標登録済みの惑星: 遠ければ赤警報NPC経由のナビで任務地へ
                    if (Player.DistanceTo(criticalLoc.WorldCords) >= 75)
                    {
                        if (EzThrottler.Throttle("CriticalGatherTravel", 2000))
                            IceLogging.Info("緊急採取: 任務地へ移動(赤警報NPC経由)してから採取します", "[Gather: CriticalTravel]");
                        Task_NavmeshMove.Enqueue_RedAlertNavmesh(criticalLoc.WorldCords, distance: 75, missionId: CosmicHelper.CurrentLunarMission);
                        return;
                    }
                }
                else if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.RedAlert, out _))
                {
                    // 座標未登録(Auxesia 等): レフレダのメニューで職業に応じた任務地を選んでワープ
                    if (EzThrottler.Throttle("CriticalGatherTravel", 2000))
                        IceLogging.Info("緊急採取: 専用NPC(レフレダ)で職業に応じた任務地へワープしてから採取します", "[Gather: CriticalTravel]");
                    P.TaskManager.Enqueue(() => Task_TurninMission.RedAlert_AuxesiaTravel(CosmicHelper.CurrentLunarMission), "緊急採取: レフレダ職業選択→ワープ");
                    return;
                }
            }
            if (Svc.Condition[ConditionFlag.Gathering])
            {
                IceLogging.Debug("Current in a gathering session");
                Task_CheckScore.Enqueue();
                P.TaskManager.Enqueue(() => GatherInteractV2(), "Interacting with gathering menu", Utils.TaskConfig);
            }
            else if (C.Gather_NoNav)
            {
                P.TaskManager.EnqueueDelay(200);
                if (CosmicHelper.SheetMissionDict[CosmicHelper.CurrentLunarMission].Attributes.HasFlag(MissionAttributes.ReducedItems))
                {
                    Task_CheckScore.Enqueue();
                    P.TaskManager.Enqueue(() => CheckReduceMission(), "Checking to see if we need to reduce items");
                    P.TaskManager.EnqueueDelay(500);
                    Task_CheckScore.Enqueue();
                }
                P.TaskManager.Enqueue(() => Mission_Settings.ResetCollectableState());
                P.TaskManager.Enqueue(() => UseFood());
            }
            else
            {
                IceLogging.Debug("Not currently gathering, starting fresh instead");
                P.TaskManager.EnqueueDelay(100);
                if (CosmicHelper.SheetMissionDict[CosmicHelper.CurrentLunarMission].Attributes.HasFlag(MissionAttributes.ReducedItems))
                {
                    Task_CheckScore.Enqueue();
                    P.TaskManager.Enqueue(() => CheckReduceMission(), "Checking to see if we need to reduce items");
                    P.TaskManager.EnqueueDelay(500);
                    Task_CheckScore.Enqueue();
                }
                else
                {
                    Task_CheckScore.Enqueue();
                }
                P.TaskManager.Enqueue(() => Mission_Settings.ResetCollectableState());
                P.TaskManager.Enqueue(() => UseFood());
                P.TaskManager.Enqueue(() => CheckCurrentLocation(), "Checking to see if gathering flags needs updated");
                P.TaskManager.Enqueue(() => PathandCheckNode());
            }
        }

        private static int GatherDelayThrottle = 0;
        private static Random random = new();

        public static bool? GatherInteractV2()
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return true;

            string tag = "Gather: Gather Interacting";

            bool CheckDelay()
            {
                if (C.Delay_Gather)
                {
                    var delay = random.Next(500, 1000);
                    if (EzThrottler.Throttle("Gather Delay", delay))
                        GatherDelayThrottle += 1;

                    if (GatherDelayThrottle < 2)
                    {
                        if (EzThrottler.Throttle("Waiting for throttle to pass by"))
                            IceLogging.Verbose("Gather Delay", tag);
                        return true;
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Ready for gathering"))
                            IceLogging.Verbose("No delay is activated for gathering, going to just go ahead and shoot", tag);
                        return false;
                    }

                }
                else
                {
                    return false;
                }
            }

            var missionInfo = CosmicHelper.CurrentMissionInfo;
            bool collectableItem = missionInfo.Attributes.HasFlag(MissionAttributes.Collectables);
            bool reduceItems = missionInfo.Attributes.HasFlag(MissionAttributes.ReducedItems);

            if (Svc.Condition[ConditionFlag.Gathering])
            {
                // We should always have this condition up while we're gathering. Even if a revisit happens
                if (!Svc.Condition[ConditionFlag.ExecutingGatheringAction])
                {
                    // This should prevent us from actually attempting to do another gathering action, while we are currently doing one
                    if (GenericHelpers.TryGetAddonMaster<Gathering>("Gathering", out var gather) && gather.IsAddonReady)
                    {
                        if (EzThrottler.Throttle("Log message"))
                        {
                            IceLogging.Debug($"Collectable: {collectableItem} | Reduce: {reduceItems}");
                        }

                        if (reduceItems || (collectableItem))
                        {
                            // We need to find an item where it's a collectable so we can just initiate the gathering window
                            var item = gather.GatheredItems.Where(x => x.IsCollectable).FirstOrDefault();
                            if (item != null)
                            {
                                if (EzThrottler.Throttle("Collectable item select"))
                                {
                                    item.Gather();
                                    Mission_Settings.Collectable_BuffCount = GatheringUtil.CollectStandardCharges();
                                    IceLogging.Debug($"Gathering {item.ItemName} for collectability");
                                }
                            }
                        }
                        else
                        {
                            var configId = C.MissionConfig[CosmicHelper.CurrentLunarMission].GProfileId;

                            // just a normal item to gather. so we're just going to do our normal gathering process
                            bool missingDur = gather.CurrentIntegrity != gather.TotalIntegrity;
                            var testItem = gather.GatheredItems.Where(x => x.ItemID != 0).FirstOrDefault();
                            // 採取窓が開いた瞬間や全アイテム枯渇のフレームでは ItemID!=0 のアイテムが0件のことがある。
                            // null のまま GatherChance を読むと NRE でタスクが落ちるため、次フレームで再評価する。
                            if (testItem == null)
                                return false;
                            int gatherChance = testItem.GatherChance;
                            int boonChance = testItem.BoonChance;
                            int playerGp = PlayerHelper.GetGp();

                            if (CheckDelay())
                                return false;

                            if (UseGatherAction(configId, gatherChance, boonChance, gather.CurrentIntegrity, gather.TotalIntegrity, playerGp))
                            {
                                return false;
                            }

                            // Find the item with the largest deficit
                            var itemToGather = CosmicHelper.CurrentMissionInfo.Gathering_Min
                                .Select(x => new
                                {
                                    ItemId = x.Key,
                                    Required = x.Value,
                                    Current = PlayerHelper.GetItemCount(x.Key, out var count) ? count : 0,
                                    Deficit = x.Value - (PlayerHelper.GetItemCount(x.Key, out count) ? count : 0)
                                })
                                .Where(x => x.Deficit > 0) // Only items we still need
                                .OrderByDescending(x => x.Deficit) // Sort by largest deficit first
                                .FirstOrDefault();

                            if (itemToGather != null)
                            {
                                if (EzThrottler.Throttle("Gathering Item"))
                                {
                                    gather.GatheredItems
                                        .FirstOrDefault(x => x.ItemID == itemToGather.ItemId)
                                        ?.Gather();
                                }
                                return false;
                            }
                            else
                            {
                                gather.GatheredItems
                                    .Where(x => x.ItemID != 0)
                                    .Where(x => !x.IsCollectable)
                                    .FirstOrDefault()
                                    .Gather();
                                return false;
                            }
                        }
                    }
                    else if (GenericHelpers.TryGetAddonMaster<GatheringMasterpiece>("GatheringMasterpiece", out var collectable) && collectable.IsAddonReady)
                    {
                        // this is all nice and tidy in one little function. Well that is split across 3 other ones but reguardless the general gathering task will be completed via this.
                        if (Mission_Settings.item_collectableId != collectable.ItemID)
                        {
                            IceLogging.Debug($"Setting Mission CollectableId to: {collectable.ItemID}", "[Gather: Collectable Interacting]");
                            Mission_Settings.item_collectableId = collectable.ItemID;
                        }

                        if (CheckDelay())
                            return false;

                        CollectableGather(collectable);
                    }
                }
                else
                {
                    IceLogging.Verbose("Currently executing a gathering action, waiting patiently", tag);
                    GatherDelayThrottle = 0;
                    return true;
                }
            }
            else
            {
                GreaterReachCount = 0;
                HadGreaterReach = false;
                GatherDelayThrottle = 0;
                // 採取セッションを抜けた(次ノードへ向かう)タイミングでスキル使用回数をリセットする。
                // しないとミッションをまたいで累積し、MaxUse 到達でスキルが二度と使われなくなる。
                Mission_Settings.ResetSkillUseAmount();
                return true;
            }

            return false;
        }
        public static unsafe void CollectableGather(GatheringMasterpiece collectable)
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return;

            var integrity = collectable.CurrentIntegrity;
            var collect_Current = collectable.CurrentCollectability;
            var collect_Max = collectable.MaxCollectability;
            var collect_highGrade = collectable.HighCollectability;
            var playerGp = PlayerHelper.GetGp();
            bool missingDur = integrity < collectable.TotalIntegrity;

            var config = C.MissionConfig[CosmicHelper.CurrentLunarMission];
            var isMaster = CosmicHelper.CurrentMissionInfo.IsMaster;

            // Track collectability progress to detect stuck rotations
            if (collect_Current != _lastCollectability)
            {
                _lastCollectability = collect_Current;
                _lastCollectProgress = DateTime.Now;
            }
            bool isStuck = _lastCollectProgress != DateTime.MinValue
                        && (DateTime.Now - _lastCollectProgress).TotalSeconds > 5;

            if (integrity > 1 && collect_Current < collect_highGrade && !isStuck)
            {
                // this is the rotation we should be aiming for in general...
                // this should cover all baselines
                if (!PlayerHelper.HasStatusId(3911))
                {
                    var currentCharge = GatheringUtil.CollectStandardCharges();

                    if (currentCharge != 0 && (currentCharge >= Mission_Settings.Collectable_BuffCount || (config.TurninGoal == TurninState.Gold && isMaster)))
                    {
                        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 27);
                    }
                    else if (CanUseCollectableAction("Scrutiny"))
                    {
                        UseCollectableBuff("Scrutiny");
                    }
                    else
                    {
                        UseCollectableAction("Meticulous");
                    }
                }
                else
                {
                    // We currently have the buff. Time to math out which would be the best for what we need.
                    var collect_Missing = collect_Max - collect_Current;
                    var brazenPower = collectable.BrazenPowerMax;
                    var meticulousPower = collectable.MeticulousPower;


                    if (CanUseCollectableAction("Scrutiny"))
                    {
                        UseCollectableBuff("Scrutiny");
                    }
                    else if (collect_Missing <= meticulousPower)
                    {
                        UseCollectableAction("Meticulous");
                    }
                    else
                    {
                        UseCollectableAction("Brazen");
                    }
                }
            }
            else
            {
                if (isStuck)
                    IceLogging.Debug("Collectable rotation stuck, falling through to collect");

                // Reset progress tracking when we start collecting
                _lastCollectability = -1;
                _lastCollectProgress = DateTime.MinValue;

                // if we've gotten this far, that means we're in a state that we should just be collecting
                if (collectable.CurrentIntegrity == 1 && CanUseCollectableAction("BonusIntegrityChance", missingDur))
                {
                    if (EzThrottler.Throttle("Integrity bonus"))
                        UseCollectableAction("BonusIntegrityChance");
                }

                else if (CanUseCollectableAction("BonusIntegrity", collectable.CurrentIntegrity == 1))
                {
                    if (EzThrottler.Throttle("Integrity bonus"))
                        UseCollectableAction("BonusIntegrity");
                }
                else
                {
                    UseCollectableAction("Collect");
                }
            }
        }
        public static bool? CheckCurrentLocation()
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return true;

            ThrottleMessage("- - - Check Gather Locations Task - - -", "[Check Gather Locations]");

            var zoneId = Player.Territory;
            var missionEntry = CosmicHelper.CurrentMissionInfo;
            // 静的ルート(JSON)を基本に、実際に出現している採取ポイントを併合する(静的ルートが無ければ動的に合成)。
            var gatherInfo = DynamicRouteLoader.GetRouteOrDynamic(missionEntry.Gather_MapKey, missionEntry.TerritoryId, missionEntry.MapPosition);

            // 指定ノードが記録されていれば、ルートをその1点に差し替える(記録した採取ポイントだけを回る)。
            if (TryGetDesignatedRoute(out var designatedRoute))
                gatherInfo = designatedRoute;

            if (gatherInfo != null)
            {
                if (Mission_Settings.previousRouteId != missionEntry.Gather_MapKey)
                {
                    // We're currently at a whole new area. So going to check the gathering nodes to see which one we're closest to
                    Mission_Settings.previousRouteId = missionEntry.Gather_MapKey;
                    var closestNodeIndex = gatherInfo.Select((node, index) => new { Node = node, Index = index })
                                                     .Where(x => Svc.Objects.Any(obj => obj.ObjectKind == ObjectKind.GatheringPoint && obj.IsTargetable && obj.BaseId == x.Node.NodeId))
                                                     .OrderBy(x =>
                                                     {
                                                         var gameObject = Svc.Objects.First(obj => obj.BaseId == x.Node.NodeId);
                                                         return Player.DistanceTo(gameObject.Position);
                                                     })
                                                     .Select(x => x.Index)
                                                     .FirstOrDefault(0);

                    Mission_Settings.nodeCounter = closestNodeIndex;
                }
                else
                {
                    // we're currently in a map location that has been previously recorded, so we're going to check to see if we're within range of any first
                    var closestDistance = gatherInfo.Where(x => Player.DistanceTo(x.Position) < 5).FirstOrDefault();
                    if (closestDistance == null)
                    {
                        // We're currently too far from any node
                        // 常に最寄りの採取可能ノードを選ぶ。設定に任せてindex順巡回にすると、
                        // 実際には出現していないルート座標へ歩いて空振りを繰り返す(座標がズレた月で多発)。
                        SetClosestTargetableNode(gatherInfo);
                        if (Mission_Settings.nodeCounter >= gatherInfo.Count)
                        {
                            // resetting it back to 0 because we're outside the normal index array
                            Mission_Settings.nodeCounter = 0;
                        }
                        return true;

                    }
                    else
                    {
                        // We're currently close to a node, time to check and see if it's a viable node, or if we need to pathfind to the next
                        var nodeId = closestDistance.NodeId;
                        var closestNode = Svc.Objects.Where(x => x.BaseId == nodeId && x.IsTargetable).FirstOrDefault();

                        if (closestNode != null)
                        {
                            // Node is targetable, set the counter to this node's index
                            var currentNodeIndex = gatherInfo.FindIndex(x => x.NodeId == nodeId);
                            if (currentNodeIndex >= 0)
                            {
                                Mission_Settings.nodeCounter = currentNodeIndex;
                            }
                            return true;
                        }
                        else
                        {
                            // ここでも最寄りの採取可能ノードを優先する。
                            SetClosestTargetableNode(gatherInfo);
                            return true;
                        }
                    }
                }
            }

            return false;
        }
        // 指定ノード採取: 記録した採取ポイント1点だけをルートとして返す。
        // 機能OFF・未記録・記録時と別の惑星にいる場合は false を返し、通常のルート採取に任せる。
        private static bool TryGetDesignatedRoute(out List<NodeInfo> route)
        {
            route = null;
            if (!C.DesignatedNodeEnabled)
                return false;
            if (C.DesignatedNodePos == Vector3.Zero || C.DesignatedNodeTerritory != Player.Territory.RowId)
                return false;

            route = new List<NodeInfo>
            {
                new NodeInfo
                {
                    NodeId = C.DesignatedNodeBaseId,
                    Position = C.DesignatedNodePos,
                    LandZone = C.DesignatedNodePos,
                }
            };
            return true;
        }

        /// <summary>現在地を指定ノードとして記録する(設定UIのボタンから呼ぶ)。</summary>
        /// <remarks>15m以内に採取ポイントがあれば、その BaseId も記録して採取対象を特定できるようにする。</remarks>
        public static void RecordDesignatedNode()
        {
            C.DesignatedNodePos = Player.Position;
            C.DesignatedNodeTerritory = Player.Territory.RowId;
            var nearest = Svc.Objects
                .Where(o => o.ObjectKind == ObjectKind.GatheringPoint)
                .OrderBy(o => Player.DistanceTo(o.Position))
                .FirstOrDefault();
            C.DesignatedNodeBaseId = (nearest != null && Player.DistanceTo(nearest.Position) <= 15f) ? nearest.BaseId : 0u;
            C.Save();
            IceLogging.Info($"[指定ノード] 記録しました: pos=({C.DesignatedNodePos.X:F1},{C.DesignatedNodePos.Y:F1},{C.DesignatedNodePos.Z:F1}) baseId={C.DesignatedNodeBaseId} territory={C.DesignatedNodeTerritory}", "[Gather: DesignatedNode]");
        }

        /// <summary>指定ノードの記録をクリアする。クリア後は通常のルート採取に戻る。</summary>
        public static void ClearDesignatedNode()
        {
            C.DesignatedNodePos = Vector3.Zero;
            C.DesignatedNodeBaseId = 0;
            C.DesignatedNodeTerritory = 0;
            C.Save();
            IceLogging.Info("[指定ノード] 記録をクリアしました", "[Gather: DesignatedNode]");
        }

        private static void SetClosestTargetableNode(List<NodeInfo> gatherInfo)
        {
            var closestIndex = gatherInfo.Select((node, index) => new { Node = node, Index = index })
                                         .Where(x => Svc.Objects.Any(obj => obj.ObjectKind == ObjectKind.GatheringPoint && obj.IsTargetable && obj.BaseId == x.Node.NodeId))
                                         .OrderBy(x =>
                                         {
                                             var gameObject = Svc.Objects.First(obj => obj.BaseId == x.Node.NodeId && obj.IsTargetable);
                                             return Player.DistanceTo(gameObject.Position);
                                         })
                                         .Select(x => x.Index)
                                         .FirstOrDefault(-1);

            if (closestIndex >= 0)
                Mission_Settings.nodeCounter = closestIndex;
            else
            {
                // No targetable nodes loaded in object table - fall back to closest route position,
                // but exclude nodes we know are depleted (in object table but not targetable)
                var fallbackIndex = gatherInfo.Select((node, index) => new { Node = node, Index = index })
                                              .Where(x =>
                                              {
                                                  var obj = Svc.Objects.FirstOrDefault(o => o.ObjectKind == ObjectKind.GatheringPoint && o.BaseId == x.Node.NodeId);
                                                  return obj == null || obj.IsTargetable;
                                              })
                                              .OrderBy(x => Player.DistanceTo(x.Node.Position))
                                              .Select(x => x.Index)
                                              .FirstOrDefault(-1);

                Mission_Settings.nodeCounter = fallbackIndex >= 0 ? fallbackIndex : 0;
            }
        }
        private const float SmartRoutingThreshold = 50f;
        public static bool? PathandCheckNode()
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return true;

            var zoneId = Player.Territory;
            var missionEntry = CosmicHelper.CurrentMissionInfo;
            // 静的ルート(JSON)を基本に、実際に出現している採取ポイントを併合する(静的ルートが無ければ動的に合成)。
            var gatherInfo = DynamicRouteLoader.GetRouteOrDynamic(missionEntry.Gather_MapKey, missionEntry.TerritoryId, missionEntry.MapPosition);

            // 指定ノードが記録されていれば、ルートをその1点に差し替える(記録した採取ポイントだけを回る)。
            if (TryGetDesignatedRoute(out var designatedRoute))
                gatherInfo = designatedRoute;

            if (gatherInfo == null || gatherInfo.Count == 0)
            {
                // ノード0件: まず採集エリア(フラグ)へ移動してノードを読み込ませる。拠点に居るまま採取状態に入り、
                // ノードが一切読み込まれず0件のまま放棄ループになるのを防ぐ。
                var flagWorld = Utils.FlagToWorld(missionEntry.TerritoryId, missionEntry.MapPosition);
                if (flagWorld.HasValue && Player.Available && Player.DistanceTo(flagWorld.Value) > 25f)
                {
                    if (EzThrottler.Throttle("ZeroNodeTravel", 2000))
                        IceLogging.Info($"採取ノード0件・採集エリアまで{Player.DistanceTo(flagWorld.Value):F0}m。採集エリアへ移動してノードを読み込ませます", "[Gather: ZeroNodeTravel]");
                    Task_NavmeshMove.Enqueue_NavmeshTask(flagWorld.Value, false, 12f);
                    _zeroNodeSince = DateTime.MinValue;
                    return true;
                }

                // 採集エリア付近に居るのに0件が続く → 無限スキャンを避け、一定時間で納品/放棄に決着させる。
                if (_zeroNodeSince == DateTime.MinValue)
                {
                    _zeroNodeSince = DateTime.Now;
                }
                else if ((DateTime.Now - _zeroNodeSince).TotalSeconds >= ZeroNodeResolveSeconds)
                {
                    var zeroRank = Task_CheckScore.CurrentRank();
                    if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                        P.Navmesh.Stop();
                    if (zeroRank >= MissionRank.Bronze && zeroRank <= MissionRank.Gold)
                    {
                        IceLogging.Info($"採取ノードが{ZeroNodeResolveSeconds}秒間0件(枯渇)でランク{zeroRank}に到達しているため、納品に移行します", "[Gather: ZeroNodeResolve]");
                        SchedulerMain.State = IceState.TurninMission;
                    }
                    else
                    {
                        IceLogging.Info($"採取ノードが{ZeroNodeResolveSeconds}秒間0件(枯渇)でランク未達のため、ミッションを放棄します", "[Gather: ZeroNodeResolve]");
                        SchedulerMain.State = IceState.AbandonMission;
                    }
                    P.TaskManager.Tasks.Clear();
                    _zeroNodeSince = DateTime.MinValue;
                    return true;
                }
                if (EzThrottler.Throttle("ZeroNodeWait", 5000))
                    IceLogging.Info($"採取ノードが見つかりません(mission {CosmicHelper.CurrentLunarMission})。再スキャン中", "[Gather: ZeroNodeResolve]");
                return false;
            }

            _zeroNodeSince = DateTime.MinValue;

            if (Mission_Settings.nodeCounter >= gatherInfo.Count)
                Mission_Settings.nodeCounter = 0;

            var location = gatherInfo[Mission_Settings.nodeCounter];

            // 実際に出現している(光っている)同一ノードの実位置へ向かう。ルート座標が実位置とズレていても確実に届く。
            var liveNode = Svc.Objects
                .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && o.BaseId == location.NodeId)
                .OrderBy(o => Player.DistanceTo(o.Position))
                .FirstOrDefault();
            if (liveNode != null && Vector3.Distance(liveNode.Position, location.Position) > 0.5f)
                location = CloneNodeAt(location, liveNode.Position);

            // 対象ノードが変わったら到達タイムアウトの計測をリセットする
            if (_stuckNodeIndex != Mission_Settings.nodeCounter)
            {
                _stuckNodeIndex = Mission_Settings.nodeCounter;
                _stuckNodeSince = DateTime.Now;
            }

            if (!Task_NavmeshMove.Task_GatherMove(location).Value)
            {
                // 一定時間そのノードに到達できない場合は、発現中のノードへ向け直す(登れない高所ノード等でのジャンプ連発を防ぐ)。
                if ((DateTime.Now - _stuckNodeSince).TotalSeconds >= NodeSkipSeconds)
                {
                    if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                        P.Navmesh.Stop();
                    Task_NavmeshMove.ResetGatherMove();
                    IceLogging.Info($"ノード {Mission_Settings.nodeCounter} に {NodeSkipSeconds}秒以内に到達できません(到達不能ノードの可能性)", "[Gather: NodeSkip]");
                    ResolveStuckGatherNode(gatherInfo, "ノードへ到達できない");
                    return false;
                }
                UseCordial();
                return false;
            }
            else
            {
                Task_NavmeshMove.ResetGatherMove();
                _stuckNodeIndex = -1;
                var rank = Task_CheckScore.CurrentRank();

                if (rank == MissionRank.Failed)
                {
                    IceLogging.Info($"We've managed to time out the mission. Going to attempt to turnin, and abandon if not", "[Gathering: Open Gathering Menu]");
                    SchedulerMain.State = IceState.AbandonMission;
                    P.TaskManager.Tasks.Clear();
                    return true;
                }
                else if (Svc.Condition[ConditionFlag.Gathering] && GenericHelpers.TryGetAddonMaster<Gathering>("Gathering", out var gather) && gather.IsAddonReady || GenericHelpers.TryGetAddonMaster<GatheringMasterpiece>("GatheringMasterpiece", out var collectable) && collectable.IsAddonReady)
                {
                    IceLogging.Info($"Gathering window is now visible, continuing onto GatheringInteraction Task", "[Gathering: OpenGatheringMenu]");
                    P.TaskManager.Insert(() => GatherInteractV2(), "Gathering at the node", Utils.TaskConfig);
                    Mission_Settings.nodeTotal += 1;
                    GreaterReachCount = 0;
                    _arrivedNodeIndex = -1;           // 採取開始 → 到着タイムアウト計測をリセット
                    _noLiveNodeSince = DateTime.MinValue; // 採取開始 → 発現ノード無しタイマーをリセット
                    return true;
                }
                else
                {
                    if (UseCordial())
                        return false;

                    // 到着済み(navmesh停止)状態のタイムアウト計測を開始/継続。対象ノードが変わったら計測をやり直す。
                    if (_arrivedNodeIndex != Mission_Settings.nodeCounter)
                    {
                        _arrivedNodeIndex = Mission_Settings.nodeCounter;
                        _arrivedSince = DateTime.Now;
                    }

                    Utils.TryGetObjectByDataId(location.NodeId, out var node);
                    if (node == null)
                    {
                        // 到着地点にノードが無い(未スポーン/枯渇で消滅)。何もしないとこのタスクが完了せず棒立ちになる。
                        IceLogging.Info($"到着地点にノード {location.NodeId} が存在しません(未スポーン/枯渇)", "[Gathering: OpenGatheringMenu]");
                        ResolveStuckGatherNode(gatherInfo, "到着地点にノードが存在しない");
                        return true;
                    }

                    if (!Player.IsJumping)
                    {
                        if (node.IsTargetable)
                        {
                            if (EzThrottler.Throttle("Target + Interacting w/ node"))
                            {
                                Utils.TargetgameObject(node);
                                Utils.InteractWithObject(node);
                            }
                        }
                        else
                        {
                            // 目の前のノードが枯渇(非targetable)。発現中のノードへ向け直す/無ければ拠点へ戻る。
                            IceLogging.Info($"The current node doesn't exist, continuing onto the next", "[Gathering: OpenGatheringMenu]");
                            ResolveStuckGatherNode(gatherInfo, "目の前のノードが枯渇している");
                            return true;
                        }
                    }

                    // 到着済みなのに一定時間 採取窓が開かない(相互作用範囲外/障害物/高所)。navmesh 停止中は移動の
                    // スタック検知が効かないため、ここで発現ノードへ向け直す/無ければ拠点へ戻って棒立ちを防ぐ。
                    if ((DateTime.Now - _arrivedSince).TotalSeconds >= ArrivedInteractTimeoutSeconds)
                    {
                        IceLogging.Info($"ノード {Mission_Settings.nodeCounter} に到着後 {ArrivedInteractTimeoutSeconds}秒採取できません(相互作用不成立ノードの可能性)", "[Gathering: ArrivedTimeout]");
                        ResolveStuckGatherNode(gatherInfo, "到着後に採取窓が開かない");
                        return true;
                    }
                }
            }

            return false;
        }

        public static uint GreaterReachCount = 0;
        public static bool HadGreaterReach = false;
        public static unsafe bool UseGatherAction(int profileId, int gatherChance, int? boonChance, int currentDur, int maxDur, int availableGp)
        {
            C.GatherProfiles.TryGetValue(profileId, out var gatherProfile);

            bool missingDur = currentDur != maxDur;

            if (Mission_Settings.Mode == ModeSelect.LevelMode)
            {
                if (EzThrottler.Throttle("Level grind message", 1000))
                    IceLogging.Debug("Leveling mode enabled, setting it to gatherProfile");
                gatherProfile = LevelProfile;
            }
            else if (gatherProfile == null)
            {
                gatherProfile = C.GatherProfiles[0];
                if (EzThrottler.Throttle("Null Profile Selected"))
                {
                    IceLogging.Error("Hey! We've somehow stumbled into a null profile being selected. Please make sure:\n" +
                                     "1: The mission you have selected has a gathering profile selected\n" +
                                     "2: If it does have one, try to click on it again\n" +
                                     "3: If that still doesn't work, let me know you're getting this error message.\n" +
                                     $"Expected profileId: {profileId} | Defaulted to the default profile");
                }
            }

            // 割当プロファイルに採取スキル(FieldMastery/Yield/Boon/Tidings)が1つも有効化されていない場合は、
            // スキル全有効の内蔵プロファイルを使う。既定プロファイル0は BonusIntegrityChance 以外すべて無効で、
            // マスター採取などでスキルを一切使わずに終わってしまうため。ユーザーが1つでも有効化していればそれを尊重する。
            if (Mission_Settings.Mode != ModeSelect.LevelMode && gatherProfile != null)
            {
                bool hasGatherSkill = gatherProfile.GatherBuffs.Buffs.Any(b => b.Value.Enabled
                    && (b.Key.StartsWith("FieldMastery") || b.Key.StartsWith("Yield") || b.Key.StartsWith("Boon") || b.Key == "Tidings"));
                if (!hasGatherSkill)
                {
                    if (EzThrottler.Throttle("SkillDefaultProfile", 5000))
                        IceLogging.Info("採取: プロファイルに採取スキルが未有効のため、スキル全有効の既定プロファイルを使用します", "[Gather]");
                    gatherProfile = LevelProfile;
                }
            }

            if (gatherChance != 100)
            {
                if (EzThrottler.Throttle("Helper Log"))
                {
                    IceLogging.Debug($"Gathering Chance: {gatherChance}", debugOnly: true);
                }
                uint MasteryBuff = GatheringUtil.GathActionDict["FieldMasteryI"].StatusId;

                string? SelectBestFieldMastery(int currentChance, int availableGp)
                {
                    int playerLevel = Player.Level;

                    bool MasteryIII = gatherProfile.GatherBuffs.Buffs["FieldMasteryIII"].Enabled
                                   && (gatherProfile.GatherBuffs.Buffs["FieldMasteryIII"].MinGp <= PlayerHelper.GetGp())
                                   && (PlayerHelper.GetGp() >= GatheringUtil.GathActionDict["FieldMasteryIII"].RequiredGp)
                                   && (playerLevel >= GatheringUtil.GathActionDict["FieldMasteryIII"].RequiredLv)
                                   && (gatherProfile.GatherBuffs.Buffs["FieldMasteryIII"].MaxUse == -1
                                       || Mission_Settings.SkillUseAmount["FieldMasteryIII"] < gatherProfile.GatherBuffs.Buffs["FieldMasteryIII"].MaxUse);
                    bool MasteryII = gatherProfile.GatherBuffs.Buffs["FieldMasteryII"].Enabled
                                  && (gatherProfile.GatherBuffs.Buffs["FieldMasteryII"].MinGp <= PlayerHelper.GetGp())
                                  && (PlayerHelper.GetGp() >= GatheringUtil.GathActionDict["FieldMasteryII"].RequiredGp)
                                  && (playerLevel >= GatheringUtil.GathActionDict["FieldMasteryII"].RequiredLv)
                                  && (gatherProfile.GatherBuffs.Buffs["FieldMasteryII"].MaxUse == -1
                                      || Mission_Settings.SkillUseAmount["FieldMasteryII"] < gatherProfile.GatherBuffs.Buffs["FieldMasteryII"].MaxUse);
                    bool MasteryI = gatherProfile.GatherBuffs.Buffs["FieldMasteryI"].Enabled
                                 && (gatherProfile.GatherBuffs.Buffs["FieldMasteryI"].MinGp <= PlayerHelper.GetGp())
                                 && (PlayerHelper.GetGp() >= GatheringUtil.GathActionDict["FieldMasteryI"].RequiredGp)
                                 && (playerLevel >= GatheringUtil.GathActionDict["FieldMasteryI"].RequiredLv)
                                 && (gatherProfile.GatherBuffs.Buffs["FieldMasteryI"].MaxUse == -1
                                     || Mission_Settings.SkillUseAmount["FieldMasteryI"] < gatherProfile.GatherBuffs.Buffs["FieldMasteryI"].MaxUse);

                    // Already at 100%? No skill needed, so continuing on
                    if (currentChance >= 100)
                        return null;

                    int neededBonus = 100 - currentChance;

                    // Find the cheapest skill that gets us to 100%
                    if (neededBonus <= 5 && availableGp >= 50 && MasteryI)
                        return "FieldMasteryI";

                    if (neededBonus <= 15 && availableGp >= 100 && MasteryII)
                        return "FieldMasteryII";

                    if (neededBonus <= 50 && availableGp >= 250 && MasteryIII)
                        return "FieldMasteryIII";

                    // If we can't reach 100%, use the best skill we can afford 
                    if (availableGp >= 250  && MasteryIII)
                        return "FieldMasteryIII";

                    if (availableGp >= 100 && MasteryII)
                        return "FieldMasteryII";

                    if (availableGp >= 50  && MasteryI)
                        return "FieldMasteryI";

                    return null; // Can't afford any skill
                }

                if (!PlayerHelper.HasStatusId(MasteryBuff) && (SelectBestFieldMastery(gatherChance, availableGp) != null))
                {
                    string? ActionName = SelectBestFieldMastery(gatherChance, availableGp);
                    if (ActionName != null)
                    {
                        if (EzThrottler.Throttle($"Using Gathering Action: {ActionName}", 100))
                        {
                            uint jobId = (uint)Player.Job;

                            IceLogging.Debug($"Using the following action: {ActionName} to gain some collectability from the node", debugOnly: true);
                            var actionId = GatheringUtil.GathActionDict[ActionName].ClassAction[jobId];
                            ActionManager.Instance()->UseAction(ActionType.Action, actionId);
                            Mission_Settings.SkillUseAmount[ActionName] += 1;
                        }
                        return true;
                    }
                }

                int playerLevel = Player.Level;
                uint TempMasteryBuffId = GatheringUtil.GathActionDict["FieldMasteryTemp"].StatusId;
                bool TempMasteryBuff = gatherProfile.GatherBuffs.Buffs["FieldMasteryTemp"].Enabled
                                    && (gatherProfile.GatherBuffs.Buffs["FieldMasteryTemp"].MinGp <= PlayerHelper.GetGp())
                                    && (PlayerHelper.GetGp() >= GatheringUtil.GathActionDict["FieldMasteryTemp"].RequiredGp)
                                    && (playerLevel >= GatheringUtil.GathActionDict["FieldMasteryTemp"].RequiredLv)
                                    && (gatherProfile.GatherBuffs.Buffs["FieldMasteryTemp"].MaxUse == -1);

                if (!PlayerHelper.HasStatusId(TempMasteryBuffId) && TempMasteryBuff)
                {
                    if (EzThrottler.Throttle($"Using Gathering Action: {"FieldMasteryTemp"}", 100))
                    {
                        uint jobId = (uint)Player.Job;

                        IceLogging.Debug($"Using the following action: {"FieldMasteryTemp"} to gain some collectability from the node", debugOnly: true);
                        var actionId = GatheringUtil.GathActionDict["FieldMasteryTemp"].ClassAction[jobId];
                        ActionManager.Instance()->UseAction(ActionType.Action, actionId);
                        Mission_Settings.SkillUseAmount["FieldMasteryTemp"] += 1;
                    }
                    return true;
                }
            }

            if (HadGreaterReach)
            {
                GreaterReachCount += 1;
                HadGreaterReach = false;
            }

            if (PlayerHelper.HasStatusId(4437) && (currentDur == 1 || currentDur == maxDur - 4) && PlayerHelper.GetGp() != PlayerHelper.MaxGp())
            {
                HadGreaterReach = true;

                if (EzThrottler.Throttle("Log Message for Collectable Action"))
                    IceLogging.Verbose($"Checking for action usage: Greater Reach");

                if (EzThrottler.Throttle("Using Greater Reach", 500))
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 27);
                return true;
            }

            // general logic for checking for the rest of the buffs now
            foreach (var buff in Mission_Settings.SkillUseAmount)
            {
                string action = buff.Key;
                // 解決済みの gatherProfile を渡す。渡さないと CanUseGatheringAction が元のプロファイルIDを見てしまい、
                // LevelProfile へ差し替えても通常スキルが発動しない。
                if (CanUseGatheringAction(action, profileId, missingDur, maxDur, currentDur, boonChance, overrideProfile: gatherProfile))
                {
                    var actionInfo = GatheringUtil.GathActionDict[action];
                    if (EzThrottler.Throttle($"Using Gathering Action: {action}"))
                    {
                        uint jobId = (uint)Player.Job;

                        IceLogging.Verbose($"Checking for action usage: {action}");


                        var actionId = GatheringUtil.GathActionDict[action].ClassAction[jobId];
                        ActionManager.Instance()->UseAction(ActionType.Action, actionId);
                        Mission_Settings.SkillUseAmount[action] += 1;
                    }

                    return true;
                }
            }

            return false;
        }
        public static bool CanUseGatheringAction(string actionName, int profileId, bool missingDur, int maxDur, int currentDur, int? boonChance = null, GatherProfile overrideProfile = null)
        {
            var actionInfo = GatheringUtil.GathActionDict[actionName];
            bool hasStatus = PlayerHelper.HasStatusId(actionInfo.StatusId);
            bool hasGp = PlayerHelper.GetGp() >= actionInfo.RequiredGp;
            var used = Mission_Settings.SkillUseAmount[actionName];
            bool properLvl = Player.Level >= actionInfo.RequiredLv;

            if (actionName == "BonusIntegrityChance")
            {
                return hasStatus && currentDur == 1;
            }

            // overrideProfile 指定時はそれを使う(スキル無しプロファイルを LevelProfile へ差し替えた場合に反映させるため)。
            var gatherBuff = (overrideProfile?.GatherBuffs ?? GatherProfile(profileId)).Buffs[actionName];

            return actionName switch
            {
                "BoonIncrease1" => gatherBuff.Enabled
                                && boonChance < 100
                                && !hasStatus
                                && !missingDur
                                && hasGp
                                && PlayerHelper.GetGp() >= gatherBuff.MinGp
                                && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                                && properLvl,
                "BoonIncrease2" => gatherBuff.Enabled
                                && boonChance < 100
                                && !hasStatus
                                && !missingDur
                                && hasGp
                                && PlayerHelper.GetGp() >= gatherBuff.MinGp
                                && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                                && properLvl,
                "Tidings" => gatherBuff.Enabled
                          && !hasStatus
                          && !missingDur
                          && hasGp
                          && PlayerHelper.GetGp() >= gatherBuff.MinGp
                          && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                          && properLvl,
                "YieldI" => gatherBuff.Enabled
                          && !hasStatus
                          && !missingDur
                          && hasGp
                          && PlayerHelper.GetGp() >= gatherBuff.MinGp
                          && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                          && (maxDur >= gatherBuff.MinUsableDurability)
                          && properLvl,
                "YieldII" => gatherBuff.Enabled
                         && !hasStatus
                         && !missingDur
                         && hasGp
                         && PlayerHelper.GetGp() >= gatherBuff.MinGp
                         && (maxDur >= gatherBuff.MinUsableDurability)
                         && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                         && properLvl,
                "BonusIntegrity" => gatherBuff.Enabled
                                    && currentDur == 1
                                    && hasGp
                                    && PlayerHelper.GetGp() >= gatherBuff.MinGp
                                    && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                                    && (maxDur >= gatherBuff.MinUsableDurability)
                                    && (GreaterReachCount < 4)
                                    && properLvl,
                "BountifulYieldII" => gatherBuff.Enabled
                                   && !hasStatus && !PlayerHelper.HasStatusId(actionInfo.StatusId2)
                                   && hasGp
                                   && PlayerHelper.GetGp() >= gatherBuff.MinGp
                                   && (gatherBuff.MaxUse == -1 || gatherBuff.MaxUse > used)
                                   && properLvl,
                _ => false,
            };
        }

        private static GatherBuffs GatherProfile(int profileId)
        {
            if (profileId != 0)
            {
                return C.GatherProfiles[profileId].GatherBuffs;
            }
            else
            {


                var currentMission = CosmicHelper.CurrentMissionInfo;
                return GetDefaultProfileForMission(currentMission.Attributes);
            }
        }

        private static GatherBuffs GetDefaultProfileForMission(MissionAttributes attrs)
        {
            var buffs = new GatherBuffs();

            if (attrs.HasFlag(MissionAttributes.GreaterReach_Boon_Chain))
            {
                buffs.Buffs["BoonIncrease2"].Enabled = true;
                buffs.Buffs["BoonIncrease1"].Enabled = true;
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.GreaterReach_Boon))
            {
                buffs.Buffs["BoonIncrease2"].Enabled = true;
                buffs.Buffs["BoonIncrease1"].Enabled = true;
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.GreaterReach_Chain))
            {
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.GreaterReach_GatherX))
            {
                buffs.Buffs["YieldII"].Enabled = true;
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.Score_GatherX))
            {
                buffs.Buffs["YieldII"].Enabled = true;
                buffs.Buffs["BountifulYieldII"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.Gather) && attrs.HasFlag(MissionAttributes.Craft))
            {
                buffs.Buffs["BoonIncrease2"].Enabled = true;
                buffs.Buffs["BoonIncrease1"].Enabled = true;
                buffs.Buffs["YieldII"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.Score_Boon) && attrs.HasFlag(MissionAttributes.Score_Chain))
            {
                buffs.Buffs["BoonIncrease2"].Enabled = true;
                buffs.Buffs["BoonIncrease1"].Enabled = true;
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.Score_Boon))
            {
                buffs.Buffs["BoonIncrease2"].Enabled = true;
                buffs.Buffs["BoonIncrease1"].Enabled = true;
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else if (attrs.HasFlag(MissionAttributes.Score_Chain))
            {
                buffs.Buffs["BonusIntegrity"].Enabled = true;
            }
            else
            {
                buffs.Buffs["BonusIntegrity"].Enabled = true;
                buffs.Buffs["YieldII"].Enabled = true;
            }

            return buffs;
        }

        private static bool CanUseCollectableAction(string action, bool missingDur = false)
        {
            var actionInfo = GatheringUtil.GathCollectableBuffs[action];
            bool hasStatus = PlayerHelper.HasStatusId(actionInfo.StatusId);
            bool hasGp = PlayerHelper.GetGp() >= actionInfo.RequiredGp;

            return action switch
            {
                "Scrutiny" => !hasStatus
                           && hasGp,
                "Focus" => !hasStatus
                        && hasGp,
                "Priming" => !hasStatus
                          && hasGp,
                "CollectorsHigh" => !hasStatus
                                 && hasGp,
                "BonusIntegrityChance" => hasStatus
                                       && missingDur,
                "BonusIntegrity" => hasGp
                                 && missingDur
                                 && PlayerHelper.GetGp() >= 300,
                _ => false,
            };
        }
        public static unsafe void UseCollectableBuff(string action)
        {
            var collectorBuffs = GatheringUtil.GathCollectableBuffs;
            var jobId = (uint)Player.Job;

            var actionId = collectorBuffs[action].ClassAction[jobId];
            if (EzThrottler.Throttle("Log Message for Collectable Action"))
                IceLogging.Verbose($"Checking for action usage: {actionId} | {action}");
            if (EzThrottler.Throttle("Using Action Buff", 500))
            {
                IceLogging.Verbose($"Attempting to use collectable buff: [{actionId}] {action}");
                ActionManager.Instance()->UseAction(ActionType.Action, actionId);
            }
        }
        public static unsafe void UseCollectableAction(string action)
        {
            var collectorAction = GatheringUtil.GathCollectableActions;
            var jobId = (uint)Player.Job;

            var actionId = collectorAction[action].ClassAction[jobId];
            if (EzThrottler.Throttle("Log Message for Collectable Action"))
                IceLogging.Verbose($"Checking for action usage: {actionId} | {action}");

            if (EzThrottler.Throttle("Using Action Buff", 100))
            {
                IceLogging.Verbose($"Attempting to use collectable action: [{actionId}] {action}");
                ActionManager.Instance()->UseAction(ActionType.Action, actionId);
            }
        }
        public static bool? CheckReduceMission()
        {
            // ミッション境界(報告/放棄直後)では CurrentLunarMission==0 → CurrentMissionInfo が null になるため早期に抜ける。
            if (CosmicHelper.CurrentLunarMission == 0)
                return true;

            IceLogging.Info($"Current itemId: {Mission_Settings.item_collectableId}", "[Gather: Check Reduce Mission]");
            bool hasCollectable = PlayerHelper.GetItemCount(Mission_Settings.item_collectableId, out var count) && count > 0;
            bool isReducableMission = CosmicHelper.CurrentMissionInfo.Attributes.HasFlag(MissionAttributes.ReducedItems);
            if (hasCollectable && isReducableMission)
            {
                P.TaskManager.InsertMulti(
                                            new(() => CheckReduceItems(), "Starting the desynth process"),
                                            new(() => WaitForDesynthCompletion(), "Waiting for desyntht to complete")
                                         );
            }

            return true;
        }
        public static unsafe bool? CheckReduceItems()
        {
            if (Svc.Condition[ConditionFlag.Occupied39])
            {
                IceLogging.Info("We're currently desynthing an item, continuing on to wait to stop", "[Task Gather: Reducing Item Check]");
                return true;
            }
            else
            {
                if (GenericHelpers.TryGetAddonMaster<Gathering>("Gathering", out var gather) && gather.IsAddonReady)
                {
                    // we shouldn't have this open while we're desynthing. . . closing it out.
                    if (EzThrottler.Throttle("Closing gather window"))
                    {
                        // 
                    }
                }
                if (Player.Mounted)
                {
                    Utils.Dismount();
                }

                // We have items to desynth! Time to check and see which window we need to interact with... or just wait. 
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("PurifyItemSelector", out var desynthWindow) && desynthWindow->IsReady)
                {
                    if (EzThrottler.Throttle("Desynthing the item"))
                    {
                        if (!Player.IsBusy)
                            ECommons.Automation.Callback.Fire(desynthWindow, true, 12, 0);
                    }
                }
                else if (GenericHelpers.TryGetAddonMaster<WKSMissionInfomation>("WKSMissionInfomation", out var missionInfo) && missionInfo.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Opening the desynth window"))
                        missionInfo.StellerReduction();
                }
                else if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var moonHud) && moonHud.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Opening the moon hud"))
                    {
                        moonHud.Mission();
                    }
                }
            }

            return false;
        }
        public static unsafe bool? WaitForDesynthCompletion()
        {
            if (!Svc.Condition[ConditionFlag.Occupied39])
            {
                PlayerHelper.GetItemCount(Mission_Settings.item_collectableId, out var count);
                if (count != 0)
                {
                    // Still have some more items to desynth, going to reset the current task count and re-check
                    P.TaskManager.Tasks.Clear();
                }
                else
                {
                    // All items reduced — close the aetherial reduction window before moving on
                    try
                    {
                        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("PurifyItemSelector", out var desynthWindow) && desynthWindow->IsReady)
                        {
                            desynthWindow->Close(true);
                        }
                    }
                    catch { }
                }

                return true;
            }

            return false;
        }
        private static int GetCurrentMissionRank()
        {
            if (CosmicHelper.CurrentLunarMission != 0
                && CosmicHelper.SheetMissionDict.TryGetValue(CosmicHelper.CurrentLunarMission, out var info))
                return (int)info.Rank;
            return 0;
        }
        public static unsafe bool UseCordial()
        {
            string tag = "Cordial Check";

            if (!PlayerHelper.CustomIsBusy)
            {
                IceLogging.Debug("Cordial Checkers", tag);
                if (C.AutoCordial)
                {
                    if (C.CordialMinRank > 0 && GetCurrentMissionRank() < C.CordialMinRank)
                    {
                        IceLogging.Debug($"Skipping cordial: mission rank {GetCurrentMissionRank()} below threshold {C.CordialMinRank}", tag);
                        return false;
                    }
                    IceLogging.Debug($"Min GP: {PlayerHelper.GetGp()} <= {C.CordialMinGp}", tag);

                    if (PlayerHelper.GetGp() <= C.CordialMinGp)
                    {
                        Dictionary<uint, (string Name, int GpGain)> cordials = new()
                        {
                            [12669] = ("Hi-Cordial", 400),
                            [1006141] = ("HQ Regular Cordial", 350),
                            [6141] = ("NQ Regular Cordial", 300),
                            [1016911] = ("HQ Watered Cordial", 200),
                            [16911] = ("NQ Watered Cordial", 150),
                        };

                        foreach (var cordial in C.inverseCordialPrio ? cordials.Reverse() : cordials)
                        {
                            IceLogging.Verbose($"Checking Cordial: {cordial.Value.Name}", tag);
                            bool hq = cordial.Key >= 1_000_000;
                            uint baseId = hq ? cordial.Key - 1_000_000 : cordial.Key;

                            if (PlayerHelper.GetItemCount(cordial.Key, out var amount, hq, !hq) && amount > 0)
                            {
                                if (ActionManager.Instance()->GetActionStatus(ActionType.Item, 12669) == 0)
                                {
                                    if (!C.PreventOvercap || !WillOvercap(cordial.Value.GpGain))
                                    {
                                        // Find the actual inventory slot and use it directly
                                        var inventoryManager = InventoryManager.Instance();
                                        var inventoryTypes = new[] 
                                        {
                                            InventoryType.Inventory1, InventoryType.Inventory2,
                                            InventoryType.Inventory3, InventoryType.Inventory4
                                        };

                                        foreach (var invType in inventoryTypes)
                                        {
                                            var container = inventoryManager->GetInventoryContainer(invType);
                                            if (container == null) continue;

                                            for (int i = 0; i < container->Size; i++)
                                            {
                                                var item = container->GetInventorySlot(i);
                                                if (item == null) continue;
                                                if (item->ItemId == baseId && (hq == false || item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))
                                                {
                                                    IceLogging.Verbose($"We're using a cordial: ID: {cordial.Key} | Name: {cordial.Value.Name}", tag);
                                                    AgentInventoryContext.Instance()->UseItem(cordial.Key, invType, (uint)i, 0);
                                                    return true;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (EzThrottler.Throttle("No Use Cordial"))
                        IceLogging.Verbose("We don't have auto cordial enabled, continuing on", tag);

                    return false;
                }
            }
            else
            {
                if (EzThrottler.Throttle("Cordial Busy"))
                    IceLogging.Debug("Player is busy, skipping cordial check", tag);
                return false;
            }
            return false;
        }
        private static bool WillOvercap(int recoveryGP)
        {
            bool WillOvercap = (PlayerHelper.GetGp() + recoveryGP) > PlayerHelper.MaxGp();
            if (WillOvercap)
            {
                IceLogging.Verbose("Not going to be using a cordial because we'll overcap\n" +
                    $"Recovered GP: {PlayerHelper.GetGp() + recoveryGP} | Max GP: {PlayerHelper.MaxGp()}");
            }

            return WillOvercap;
        }
        public static unsafe bool? UseFood()
        {
            string tag = "Task: Use Gathering Food";

            var ItemId = C.GatheringFood;
            if (C.UseGatheringFood && ItemId != 0)
            {
                if (C.FoodMinRank > 0 && GetCurrentMissionRank() < C.FoodMinRank)
                {
                    IceLogging.Debug($"Skipping food: mission rank {GetCurrentMissionRank()} below threshold {C.FoodMinRank}", tag);
                    return true;
                }
                PlayerHelper.GetItemCount(ItemId, out var HqCount, includeNq: false);
                PlayerHelper.GetItemCount(ItemId, out var NqCount, includeHq: false);

                if (HqCount > 0 || NqCount > 0)
                {
                    // We've gotten this far, which means we have a gathering item to use...
                    if (!PlayerHelper.HasFoodRunning())
                    {
                        IceLogging.Verbose("We currently don't have food running/we have food, so going to check to see if we can use it", tag);
                        // We need to apply the food, since we have some, we're going to use some here
                        if (EzThrottler.Throttle("Using Food Item", 3000))
                        {
                            if (HqCount > 0)
                                ItemId += 1_000_000;

                            ActionManager.Instance()->UseAction(ActionType.Item, ItemId, extraParam: 65535);
                            IceLogging.Debug($"Attempting to use food: {ItemId}", tag);
                        }
                        return false;
                    }
                    else
                    {
                        IceLogging.Info("We have food running, and it's the proper one! Continuing", tag);
                        return true;
                    }
                }
                else
                {
                    IceLogging.Info("We are out of the current food, continuing on w/o buff", tag);
                    return true;
                }
            }
            else
            {
                IceLogging.Info("We either don't have use food enabled, or have no food selected. Continuing on\n" +
                               $"Use Food Enabled: {C.UseGatheringFood}\n" +
                               $"ItemId of food: {ItemId}", tag);
                return true;
            }
        }
        private static void ThrottleMessage(string s, string handle)
        {
            if (EzThrottler.Throttle($"Throttling the following message: {s}", 1000))
            {
                IceLogging.Debug(s, handle);
            }
        }
        private static GatherProfile LevelProfile = new()
        {
            Name = "Leveing Profile",
            Id = 99999,
            MinimumGp = 0,
            GatherBuffs = new()
            {
                BountifulMinItem = 1,
                Buffs = new()
                {
                    ["BoonIncrease2"] = new() { Enabled = true },
                    ["BoonIncrease1"] = new() { Enabled = true },
                    ["Tidings"] = new() { Enabled = true },
                    ["YieldII"] = new() { Enabled = true },
                    ["YieldI"] = new() { Enabled = true },
                    ["BountifulYieldII"] = new() { Enabled = false },
                    ["BonusIntegrity"] = new() { Enabled = true },
                    ["BonusIntegrityChance"] = new() { Enabled = true },
                    ["FieldMasteryIII"] = new() { Enabled = true },
                    ["FieldMasteryII"] = new() { Enabled = true },
                    ["FieldMasteryI"] = new() { Enabled = true },
                    ["FieldMasteryTemp"] = new() { Enabled = true },
                }
            }
        };
    }
}
