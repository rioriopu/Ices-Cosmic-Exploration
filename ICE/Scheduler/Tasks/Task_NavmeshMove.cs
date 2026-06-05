using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Resources.GatheringRoutes;
using ICE.Scheduler.Handlers.PictoStuff;
using ICE.Ui.DebugWindowTabs;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.Threading.Tasks;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal class Task_NavmeshMove
    {
        // Constants
        private static readonly Random _random = new();
        private const float navmeshTolerance = 0.25f;
        private const float distanceToBeStuck = 1.0f;
        private static int stuckTimeThresholdMs => C.StuckDelayMs;
        private const int navmeshThrottleMs = 3000;
        private const int waitingThrottleMs = 1000;
        private const int positionLogThrottleMs = 500;
        private const int stuckCheckLogThrottleMs = 500;
        private const int movingMessageThrottleMs = 2000;
        private const int stuckHardStopMs = 10000; // 5m圏内からこの時間出られなければ安全のためICE自動停止(走り続け/壁ジャンプ事故防止)
        private const float hardStopRadius = 5.0f;  // この半径内に留まり続けたら「前進できていない」とみなす

        // State tracking
        private static Vector3 lastPosition = Vector3.Zero;
        private static DateTime whenStarted = DateTime.Now;
        private static DateTime lastTimeTracked = DateTime.Now;
        private static bool isJumpInProgress = false;

        // 進捗ベースのハードストップ用: 「直近で十分前進した位置」と「その時刻」。5m以上前進する度に更新する。
        private static Vector3 hardStopAnchor = Vector3.Zero;
        private static DateTime hardStopAnchorTime = DateTime.Now;
        // ハードストップのストライク管理: 短時間に連続して詰まった回数。1回の連鎖でいきなり全停止せず、
        // ミッションのスキップ/放棄で復帰を試み、それでも連続(maxStrikes)で詰まる=真に脱出不能な時だけ全停止する。
        private static int hardStopStrikes = 0;
        private static DateTime lastHardStopTime = DateTime.Now;
        private const int hardStopMaxStrikes = 3;
        private const double hardStopStrikeWindowSec = 60.0;
        // 拠点帰還(Stellar Return)による脱出の連発防止。脱出しても解決しない(再度嵌る)場合は上限で見切って全停止する。
        private static int escapeCount = 0;
        private static DateTime lastEscapeTime = DateTime.Now;
        private const int maxEscapes = 3;
        private const double escapeWindowSec = 180.0;

        private static FishingDebug _fishingDebug = null;

        public enum TravelTypes
        {
            Direct,
            Aethernet,
            RedAlert,
            Hub_Return,
            Hub_Aethernet,
            Hub_RedAlert,
            Board,
        }

        #region Navmesh Stuff

        public static bool? Task_NavTo(Vector3 pos, bool waitForBusy = true, float distance = 2.0f, bool stayMounted = false, Vector3? npcLoc = null, bool mountBeforeMove = false)
        {
            string handle = "[Navmesh Task_NavTo]";

            // Cache frequently accessed values
            bool usingCosmoliner = Svc.Condition[ConditionFlag.Unknown101];
            bool mounted = Player.Mounted;
            bool inMission = CosmicHelper.CurrentLunarMission != 0;
            float minMountDistance = C.MountRadius;
            float dismountDistance = C.DismountRadius;
            bool useMount = ShouldUseMount(inMission);

            // Calculate distance once
            float distanceToTarget = npcLoc.HasValue
                ? Player.DistanceTo(npcLoc.Value)
                : Player.DistanceTo(pos);

            if (EzThrottler.Throttle("Navmesh message throttle", navmeshThrottleMs))
                IceLogging.Verbose("Executing Navmesh Task", handle, debugOnly: true);

            // Early exit if navmesh not installed
            if (!P.Navmesh.Installed)
            {
                IceLogging.Info("We seem to be missing navmesh... so we're just going to exit here", handle);
                return true;
            }

            // Handle navmesh not ready
            if (!P.Navmesh.IsReady())
            {
                if (EzThrottler.Throttle("Waiting on navmesh", waitingThrottleMs))
                {
                    var navProgress = P.Navmesh.BuildProgress();
                    IceLogging.Debug($"Waiting for navmesh to finish building. Currently at: {navProgress:N2}", handle);
                }
                return false;
            }

            CloseExtraWindows();

            // Handle running navmesh
            if (P.Navmesh.IsRunning())
            {
                return HandleRunningNavmesh(pos, waitForBusy, distance, stayMounted, npcLoc, usingCosmoliner, mounted, useMount, minMountDistance, dismountDistance, distanceToTarget, handle);
            }

            // Handle starting navmesh
            return HandleStartNavmesh(pos, distance, stayMounted, npcLoc, usingCosmoliner, mounted, distanceToTarget, handle, useMount, mountBeforeMove);
        }
        public static bool? Task_GatherMove(GathNodeInfo routeinfo, bool waitForBusy = true, float distance = 3.5f, bool stayMounted = false, bool mountBeforeMove = false)
        {
            string handle = "Navmesh: Gather Move";

            // Cache frequently accessed values
            bool usingCosmoliner = Svc.Condition[ConditionFlag.Unknown101];
            bool mounted = Player.Mounted;
            bool inMission = CosmicHelper.CurrentLunarMission != 0;
            float minMountDistance = C.MountRadius;
            float dismountDistance = C.DismountRadius;
            bool useMount = ShouldUseMount(inMission);

            if (EzThrottler.Throttle("Navmesh message throttle", navmeshThrottleMs))
                IceLogging.Verbose("Executing Navmesh Task", handle, debugOnly: true);

            // Early exit if navmesh not installed
            if (!P.Navmesh.Installed)
            {
                IceLogging.Info("We seem to be missing navmesh... so we're just going to exit here", handle);
                return true;
            }

            // Handle navmesh not ready
            if (!P.Navmesh.IsReady())
            {
                if (EzThrottler.Throttle("Waiting on navmesh", waitingThrottleMs))
                {
                    var navProgress = P.Navmesh.BuildProgress();
                    IceLogging.Debug($"Waiting for navmesh to finish building. Currently at: {navProgress:N2}", handle);
                }
                return false;
            }

            // This is where we need to get a random point in a fan if possible...
            // Esentially going to pass this once mainly for the pathing, and then if we meed the minimum distance the HandleRunningNavmesh should take care of properly returning if we're within interacting range once stop moving

            Vector3 nodePos = routeinfo.Position;
            Vector3 playerPos = Player.Position;
            float angleToPlayer = CalculateAngleToPlayer(nodePos, playerPos);

            float node_MinAngle = PictomancyToFFXIV(routeinfo.Radius_Start + routeinfo.FanHeight);
            float node_MaxAngle = PictomancyToFFXIV(routeinfo.Radius_End + routeinfo.FanHeight);

            bool isInsideFan = IsAngleInRange(angleToPlayer, node_MinAngle, node_MaxAngle);
            float sectionSize = isInsideFan ? 30f : 60f;

            var (sectionMin, sectionMax) = GetNearestSection(node_MinAngle, node_MaxAngle, angleToPlayer, sectionSize);
            float selectedAngle = RandomAngleInRange(sectionMin, sectionMax);
            float selectedDistance = NextFloat(routeinfo.Distance_Min, routeinfo.Distance_Max);

            Vector3 randomPosition = CalculateFanPosition(nodePos, selectedAngle, selectedDistance, routeinfo.FanHeight);
            // 立ち位置は「足で到達可能な地点」を優先する。NearestPointだと到達可否を無視して最寄りメッシュに吸着し、
            // キノコの傘など歩いて行けない孤立メッシュ島へ吸着→登ろうとしてジャンプ連発(Auxesia多層地形)。
            // NearestPointReachableで到達可能点に補正し、XZ探索も0.1f→3fに拡大。失敗時のみNearestPointへフォールバック。
            // (旧 .Value はnull時NREの潜在バグもあった→HasValueガードで解消)
            var snappedPos = P.Navmesh.NearestPointReachable(randomPosition, 3f, 5f)
                             ?? P.Navmesh.NearestPoint(randomPosition, 3f, 5f);
            if (snappedPos.HasValue)
                randomPosition = snappedPos.Value;
            // if (EzThrottler.Throttle("Gather Route Throttle", 3000))
               // IceLogging.Debug($"[GatherMove] angleToPlayer={angleToPlayer:F1}, node_MinAngle={node_MinAngle:F1}, node_MaxAngle={node_MaxAngle:F1}, sectionMin={sectionMin:F1}, sectionMax={sectionMax:F1}, selectedAngle={selectedAngle:F1}, selectedDistance={selectedDistance:F2}, minDist={routeinfo.Distance_Min}, maxDist={routeinfo.Distance_Max}, randomPosition={randomPosition}", handle);

            float distanceToTarget = Player.DistanceTo(nodePos);

            // Handle running navmesh
            if (P.Navmesh.IsRunning())
            {
                return HandleRunningNavmesh(randomPosition, waitForBusy, distance, stayMounted, nodePos, usingCosmoliner, mounted, useMount, minMountDistance, dismountDistance, distanceToTarget, handle);
            }

            // Handle starting navmesh
            return HandleStartNavmesh(randomPosition, distance, stayMounted, nodePos, usingCosmoliner, mounted, distanceToTarget, handle, useMount, mountBeforeMove);
        }
        private static bool ShouldUseMount(bool inMission)
        {
            return (C.UseMountInMission && inMission) || (C.UseMountOutsideMission && !inMission);
        }
        private static bool? HandleRunningNavmesh(Vector3 pos, bool waitForBusy, float distance, bool stayMounted, Vector3? npcLoc, bool usingCosmoliner, bool mounted, bool useMount, float minMountDistance, float dismountDistance, float distanceToTarget, string handle)
        {
            // Stop navmesh if using cosmoliner
            if (usingCosmoliner)
            {
                P.Navmesh.Stop();
                return false;
            }

            // Stop navmesh after jump completes
            if (!Player.IsJumping && isJumpInProgress)
            {
                P.Navmesh.Stop();
                isJumpInProgress = false;
                return false;
            }

            // Track position for stuck detection
            if (EzThrottler.Throttle("Logging last position", positionLogThrottleMs))
            {
                lastPosition = Player.Position;
            }

            CheckIfIsStuck();

            // Handle mounting
            if (!mounted && distanceToTarget > minMountDistance && useMount)
            {
                if (EzThrottler.Throttle("Using mount"))
                    Utils.MountAction();
            }

            // Handle dismounting
            if (distanceToTarget <= dismountDistance && !stayMounted)
            {
                if (EzThrottler.Throttle("Dismounting the mount"))
                {
                    Utils.Dismount();
                }
            }

            if (C.CrazyTaxiArrow)
            {
                if (npcLoc != null)
                    PictoManager.DrawArrowToward(npcLoc.Value);
                else
                    PictoManager.DrawArrowToward(pos);
            }

            // Check if we should wait for movement to stop
            if (Player.IsMoving && waitForBusy)
            {
                if (EzThrottler.Throttle("Throttle message tehe", movingMessageThrottleMs))
                    IceLogging.Verbose("We're currently moving, and we were told to wait for us to NOT be moving so... yeah, we waiting", handle);
                return false;
            }

            // Stop navmesh if we're close enough and not waiting for busy
            if (!waitForBusy && distanceToTarget <= distance)
            {
                if (EzThrottler.Throttle("Distance stop throttle"))
                    IceLogging.Debug("We're within stopping distance, so stopping navmesh", handle);
                P.Navmesh.Stop();
            }

            return false;
        }
        private static bool? HandleStartNavmesh(Vector3 pos, float distance, bool stayMounted, Vector3? npcLoc, bool usingCosmoliner, bool mounted, float distanceToTarget, string handle, bool useMount = false, bool mountBeforeMove = false)
        {

            if (C.CrazyTaxiArrow)
            {
                if (npcLoc != null)
                    PictoManager.DrawArrowToward(npcLoc.Value);
                else
                    PictoManager.DrawArrowToward(pos);
            }

            // Don't start navmesh while using cosmoliner
            if (usingCosmoliner)
            {
                ResetInfo();
                return false;
            }

            // Wait for jump to complete
            if (Player.IsJumping)
            {
                return false;
            }

            // Check if we've arrived at destination
            if (distanceToTarget < distance)
            {
                return HandleArrival(mounted, stayMounted, distanceToTarget, distance, handle);
            }

            if (!mounted && useMount && mountBeforeMove)
            {
                Utils.MountAction();
                return false;
            }

            // Start navmesh pathfinding
            if (EzThrottler.Throttle("Telling navmesh to start"))
            {
                IceLogging.Debug("Telling navmesh to start pathfinding", handle);
                whenStarted = DateTime.Now;
                hardStopAnchor = Vector3.Zero; // 新しい移動開始 → 進捗アンカーをリセット
                ResetInfo();
                IceLogging.DestinationLogs.Log(pos);
                P.Navmesh.SetTolerance(navmeshTolerance);

                var targetPos = pos;

                P.Navmesh.PathfindAndMoveTo(targetPos, false);
            }

            return false;
        }
        private static bool? HandleArrival(bool mounted, bool stayMounted, float distanceToTarget, float requiredDistance, string handle)
        {
            if (mounted && !stayMounted)
            {
                if (EzThrottler.Throttle("Dismounting the mount"))
                {
                    Utils.Dismount();
                }
                return false;
            }

            if (EzThrottler.Throttle($"Met Location throttle", 5000))
            {
                IceLogging.Debug("We've met the distance threshold for our destination", handle);
                IceLogging.Debug($"Player Distance: {distanceToTarget:N2}");
                IceLogging.Debug($"Expected Distance: {requiredDistance}");
            }
            ResetInfo();
            return true;
        }
        private static void ResetInfo()
        {
            lastPosition = Vector3.Zero;
            lastTimeTracked = DateTime.Now;
            isJumpInProgress = false;
        }
        private static unsafe void CheckIfIsStuck()
        {
            var currentPos = Player.Position;

            // === 進捗ベースのハードストップ(最優先・jitter耐性) ===
            // navmesh稼働中、hardStopRadius(5m)圏内から stuckHardStopMs(10秒) 出られなければ
            // 「決まった座標へ走り続けているが実質前進できていない＝到達不能」とみなし、ICEを停止する。
            // 完全静止でなく微妙に動いていても(壁押し/ジャンプ/微振動)、5m前進できなければ検知できる。
            if (hardStopAnchor == Vector3.Zero || Vector3.Distance(currentPos, hardStopAnchor) > hardStopRadius)
            {
                hardStopAnchor = currentPos;
                hardStopAnchorTime = DateTime.Now;
            }
            else if ((DateTime.Now - hardStopAnchorTime).TotalMilliseconds >= stuckHardStopMs)
            {
                P.Navmesh.Stop();
                ResetInfo();
                hardStopAnchor = Vector3.Zero;

                // 既に拠点帰還(脱出)処理中(HubReturn)なら、脱出を再発火させず帰還の完了を待つ(脱出の無限ループ防止)。
                if (SchedulerMain.State == IceState.HubReturn)
                    return;

                // ストライク集計: 一定時間(60s)以上ぶりの単発スタックはカウントをリセット。連続して詰まる時だけ加算。
                if ((DateTime.Now - lastHardStopTime).TotalSeconds > hardStopStrikeWindowSec)
                    hardStopStrikes = 0;
                hardStopStrikes++;
                lastHardStopTime = DateTime.Now;

                // まだ連続ストライクが上限未満なら、全停止せず「現在のミッションをスキップ/放棄して継続」を試みる。
                if (hardStopStrikes < hardStopMaxStrikes)
                {
                    // ミッションへ接近中(まだ未取得) → そのミッションをunsupported登録してスキップ＋別ミッション再選択。
                    if (SchedulerMain.State == IceState.GrabMission && Task_CheckMissions.CurrentGrabTarget != 0)
                    {
                        IceLogging.Warning($"{stuckHardStopMs / 1000}秒間ミッションへ到達できないため、このミッションをスキップして別ミッションを選び直します(到達不能ミッションの自動スキップ {hardStopStrikes}/{hardStopMaxStrikes})。");
                        Task_CheckMissions.SkipUnsupportedAndReselect(Task_CheckMissions.CurrentGrabTarget);
                        return;
                    }
                    // 既にミッション取得済(採取/制作中など)で詰まった → そのミッションをunsupported登録して放棄＋次ミッションへ。
                    if (CosmicHelper.CurrentLunarMission != 0)
                    {
                        IceLogging.Warning($"{stuckHardStopMs / 1000}秒間ミッション遂行中に到達できないため、ミッションを放棄して次へ進みます(到達不能の自動回避 {hardStopStrikes}/{hardStopMaxStrikes})。");
                        Task_CheckMissions.MarkCurrentMissionUnsupportedAndAbandon();
                        return;
                    }
                    // ミッション文脈が無い(ハブ移動中等)場合は、脱出せずナビだけ止めて次サイクルで再評価(フォールスルー脱出を防ぐ)。
                    return;
                }

                // === ここから strikes>=max (真に脱出不能=地形に嵌った等) ===
                // Stellar Return(拠点帰還テレポート)で物理的な罠から脱出し継続を試みる。テレポートなので経路不能でも抜けられる。
                // ただし脱出しても解決しない(再度同じ罠に嵌る)場合があるため、一定回数(maxEscapes/escapeWindowSec)で見切って全停止する。
                if ((DateTime.Now - lastEscapeTime).TotalSeconds > escapeWindowSec)
                    escapeCount = 0;

                bool canStellarReturn = C.UseHubReturn
                    && !(C.AvoidStellarReturn && !C.AvoidStellarReturnExceptHub)
                    && CosmicHelper.HubCenter.ContainsKey(Player.Territory.RowId);

                if (canStellarReturn && escapeCount < maxEscapes)
                {
                    escapeCount++;
                    lastEscapeTime = DateTime.Now;
                    IceLogging.Warning($"到達不能のため拠点へ帰還(Stellar Return)して罠から脱出し継続します(脱出 {escapeCount}/{maxEscapes})。");
                    // 嵌りの原因となった現行ミッション/接近対象をunsupported登録して再選択時に除外
                    Task_CheckMissions.MarkUnsupported(Task_CheckMissions.CurrentGrabTarget != 0 ? Task_CheckMissions.CurrentGrabTarget : CosmicHelper.CurrentLunarMission);
                    Task_CheckMissions.CurrentGrabTarget = 0;
                    hardStopStrikes = 0;
                    P.TaskManager.Tasks.Clear();
                    SchedulerMain.State = IceState.HubReturn; // HubReturn→HubCheckでStellar Return→Start→ミッション再選択
                    return;
                }

                // 脱出を上限まで試しても解決しない、またはStellar Return不可設定/ハブ未登録 → 最終防壁として全停止(自動OFF)。
                IceLogging.Warning($"到達不能を解消できないため、安全のためICEを停止します(脱出{escapeCount}回試行後/脱出手段なし strikes={hardStopStrikes})。");
                escapeCount = 0;
                SchedulerMain.DisablePlugin();
                return;
            }

            var timeSinceLastChecked = (DateTime.Now - lastTimeTracked).TotalMilliseconds;
            var navmeshStartTime = (DateTime.Now - whenStarted).TotalMilliseconds;
            var distanceMoved = Vector3.Distance(currentPos, lastPosition);

            // Early exit if player has moved enough (not stuck)
            if (distanceMoved > distanceToBeStuck)
            {
                ResetInfo();
                return;
            }

            /*
            // Throttled logging for debug purposes
            if (EzThrottler.Throttle("Log stuck info", stuckCheckLogThrottleMs))
            {
                IceLogging.Verbose($"Last time checked: {timeSinceLastChecked:N0}ms");
                IceLogging.Verbose($"Navmesh Start time: {navmeshStartTime:N0}ms");
                IceLogging.Verbose($"Current Pos: {currentPos:N2} | Last position: {lastPosition:N2} | Distance: {distanceMoved:N2}");
            }
            */

            // If stuck for long enough, try unstuck actions
            if (timeSinceLastChecked >= stuckTimeThresholdMs && navmeshStartTime >= stuckTimeThresholdMs)
            {
                if (C.RetargetIfStuck && EzThrottler.Throttle("Retarget if stuck", 1000))
                {
                    IceLogging.Debug("Stuck detected - stopping navmesh to trigger retarget");
                    P.Navmesh.Stop();
                    ResetInfo();
                    return;
                }

                // ミッション接近中(GrabMission)は到達不能地点へのジャンプを抑止する。
                // 未開拓/高所の到達不能ミッションへ走り込んだ際、スキップ(10秒)が発火するまで登ろうとジャンプ連発するのは
                // 見た目が露骨(BOT露見)なため。ジャンプせず静止して待ち、ハードストップのスキップに任せる。
                // 採取中(Gather)等のノード周りでは従来どおりジャンプで引っ掛かりを解消する。
                bool suppressJump = SchedulerMain.State == IceState.GrabMission;
                if (C.JumpIfStuck_V2 && !suppressJump && EzThrottler.Throttle("Using jump action"))
                {
                    isJumpInProgress = true;
                    ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
                }
            }
        }

        #endregion

        #region Best Pathfinding

        public class PathInfo
        {
            public Vector3 destination { get; set; } = Vector3.Zero;
            public float distance { get; set; } = 0;
            public List<Vector3> pathTo { get; set; } = null;
            public List<Vector3> pathFrom { get; set; } = null;
            public uint Aethernet_TravelTo { get; set; } = 0;
            public uint Aethernet_TravelFrom { get; set; } = 0;
            // ボード移動用: Entry=乗り口(歩いて行くと自動発進)、Exit=到着地点。
            public Vector3 BoardEntry { get; set; } = Vector3.Zero;
            public Vector3 BoardExit { get; set; } = Vector3.Zero;
        }
        private static Dictionary<TravelTypes, PathInfo> TravelMethods = new()
        {
            [TravelTypes.Direct] = new(),
            [TravelTypes.Aethernet] = new(),
            [TravelTypes.RedAlert] = new(),
            [TravelTypes.Hub_Return] = new(),
            [TravelTypes.Hub_Aethernet] = new(),
            [TravelTypes.Hub_RedAlert] = new(),
            [TravelTypes.Board] = new(),
        };
        public class AethernetSystem
        {
            public uint AethernetId { get; set; } = 0;
            public Vector3 Location { get; set; } = Vector3.Zero;
            public Vector3 LandZone { get; set; } = Vector3.Zero;
            public int MapSelector { get; set; } = 0;
            public uint RequiredLogLv { get; set; } = 0;
        }
        // ボード(オブジェクト操作不要・Entry座標へ歩くと自動発進してExitへ運ばれる)。実機座標で登録。
        public class BoardInfo
        {
            public Vector3 Entry { get; set; } = Vector3.Zero; // 乗り口(ここへ歩くと発進)
            public Vector3 Exit { get; set; } = Vector3.Zero;  // 到着地点(運ばれて降りる場所)
        }
        public static Dictionary<uint, List<BoardInfo>> PlanetBoards = new()
        {
            [1319] = new()  // Auxesia (実機取得 2026-06-03)。双方向ペアの連絡ボード網。
            {
                // ハブ ↔ 中央
                new() { Entry = new(257.09f, 208.35f, 335.37f), Exit = new(-4.57f, 186.59f, 21.81f) },   // ハブ→中央
                new() { Entry = new(-12.81f, 187.47f, 30.51f),  Exit = new(250.08f, 208.43f, 341.45f) }, // 中央→ハブ
                // 中央 ↔ 北西
                new() { Entry = new(-41.66f, 187.30f, -1.94f),  Exit = new(-368.59f, 170.09f, -173.83f) }, // 中央→北西
                new() { Entry = new(-361.62f, 170.02f, -166.83f), Exit = new(-32.92f, 186.64f, 6.43f) },   // 北西→中央
                // 中央 ↔ 東
                new() { Entry = new(17.17f, 186.58f, 1.88f),    Exit = new(594.02f, 190.12f, 119.37f) },  // 中央→東
                new() { Entry = new(597.97f, 190.11f, 110.60f), Exit = new(10.87f, 186.62f, -6.43f) },    // 東→中央
            },
        };
        public static Dictionary<uint, List<AethernetSystem>> PlanetAethernet = new()
        {
            [1237] = new()
            {
                new()
                {
                    MapSelector = 0,
                    AethernetId = 2015100,
                    LandZone = new(-2.66f, 1.64f, -29.62f),
                    Location = new(-3.80f, 1.64f, -32.15f),
                },
                new()
                {
                    MapSelector = 1,
                    AethernetId = 2015053,
                    LandZone = new(-540.94f, 59.55f, -529.95f),
                    Location = new(-540.00f, 59.55f, -526.75f),
                },
                new()
                {
                    MapSelector = 2,
                    AethernetId = 2015054,
                    LandZone = new(429.26f, 45.55f, 499.95f),
                    Location = new(427.23f, 45.55f, 497.46f),
                },
                new()
                {
                    MapSelector = 3,
                    AethernetId = 2015055,
                    LandZone = new(-621.24f, 50.55f, 396.78f),
                    Location = new(-619.56f, 50.55f, 399.77f),
                },
                new()
                {
                    MapSelector = 4,
                    AethernetId = 2015056,
                    LandZone = new(631.38f, -73.95f, -572.59f),
                    Location = new(629.80f, -73.95f, -572.78f),
                }
            },
            [1291] = new()
            {
                new()
                {
                    MapSelector = 0,
                    AethernetId = 2015057,
                    Location = new(336.26f, 52.64f, -381.73f),
                    LandZone = new(337.50f, 52.64f, -383.00f),
                },
                new()
                {
                    MapSelector = 1,
                    AethernetId = 2015058,
                    Location = new(-150.82f, 29.05f, 314.44f),
                    LandZone = new(-151.47f, 29.05f, 313.33f),
                },
                new()
                {
                    MapSelector = 2,
                    AethernetId = 2015059,
                    Location = new(-640.11f, -1.45f, -627.05f),
                    LandZone = new(-640.05f, -1.45f, -628.36f),
                },
                new()
                {
                    MapSelector = 3,
                    AethernetId = 2015060,
                    Location = new(672.88f, -241.50f, 422.65f),
                    LandZone = new(673.69f, -241.86f, 424.29f),
                },
                new()
                {
                    MapSelector = 4,
                    AethernetId = 2015061,
                    Location = new(-590.90f, 28.50f, 722.25f),
                    LandZone = new(-591.95f, 28.50f, 722.10f),
                }
            },
            [1310] = new()
            {
                new()
                {
                    MapSelector = 0,
                    AethernetId = 2015062,
                    Location = new(-164.10f, 0.50f, 83.50f),
                    LandZone = new(-165.44f, 0.38f, 84.41f),
                },
                new()
                {
                    MapSelector = 1,
                    AethernetId = 2015063,
                    Location = new(-141.20f, -74.95f, -591.17f),
                    LandZone = new(-140.56f, -74.95f, -589.59f),
                },
                new()
                {
                    MapSelector = 2,
                    AethernetId = 2015064,
                    Location = new(-454.18f, 102.05f, 760.83f),
                    LandZone = new(-453.34f, 102.05f, 761.97f),
                },
                new()
                {
                    MapSelector = 3,
                    AethernetId = 2015065,
                    Location = new(733.85f, 218.80f, -100.98f),
                    LandZone = new(732.61f, 218.80f, -101.53f),
                },
                new()
                {
                    MapSelector = 4,
                    AethernetId = 2015066,
                    Location = new(-124.6f, -193.7f, -801.46f),
                    LandZone = new(-123.94f, -193.70f, -802.01f),
                    RequiredLogLv = 14,
                }
            },
            [1319] = new()  // Auxesia (実機取得 2026-06-03)。MapSelectorはID順(=テレポメニュー並び順の推定)。ズレたら要実機修正。
            {
                new()
                {
                    // ティンバーロッジ基地(ハブ近傍 Y≈205)。基幹拠点=メニュー先頭と推定。
                    MapSelector = 0,
                    AethernetId = 2015422,
                    Location = new(259.80f, 205.64f, 356.30f),
                    LandZone = new(262.39f, 205.69f, 353.20f),
                },
                new()
                {
                    // フルブルーム・ガーデン (採取エリア Y≈145)
                    MapSelector = 1,
                    AethernetId = 2015423,
                    Location = new(-226.37f, 145.01f, -560.40f),
                    LandZone = new(-225.69f, 145.01f, -556.88f),
                },
                new()
                {
                    // パイレウス・パーゴラ (Y≈168)
                    MapSelector = 2,
                    AethernetId = 2015424,
                    Location = new(-242.73f, 168.05f, 321.17f),
                    LandZone = new(-244.14f, 168.05f, 317.43f),
                },
            }
        };
        private static Task? _PathCalculations = null;

        public static void Enqueue_NavmeshTask(Vector3 destination, bool waitForBusy = true, float distance = 2.0f)
        {
            if (P.Navmesh.Installed)
            {
                P.TaskManager.InsertMulti
                (
                    new(() => Paths_Clear(), "Clearing all Navmesh Paths"),
                    new(() => CalculateAethernet(destination), "Calculating Aethernet Path"),
                    new(() => CalculateHub(destination), "Calculating Hub Path"),
                    new(() => CalculateDirect(destination), "Calculating Direct Path"),
                    new(() => CalculateHubAethernet(destination), "Calculate Hub Aetheryte Travel"),
                    new(() => CalculateBoard(destination), "Calculating Board (hover platform) Path"),
                    new(() => FindBestTravel(destination, waitForBusy, distance), "Finding best pathing method")
                );
            }
            else
            {
                IceLogging.Verbose("Navmesh was not installed, so we're not even going to attempt to move", "Enqueue Navmesh");
            }
        }
        public static void Enqueue_RedAlertNavmesh(Vector3 destination, bool waitForBusy = true, float distance = 2.0f, uint missionId = 0)
        {
            if (P.Navmesh.Installed)
            {
                P.TaskManager.InsertMulti
                (
                    new(() => Paths_Clear(), "Clearing all Navmesh Paths"),
                    new(() => CalculateDirect(destination), "Calculating Direct Path"),
                    new(() => CalculateRedAlert(missionId, destination), "Calculating Red Alert Direct Travel [Direct -> NPC -> RE]"),
                    new(() => CalculateRedAlertHub(missionId, destination), "Calculating Red Alert Hub Travel [Hub -> NPC -> RE]"),
                    new(() => FindBestTravel(destination, waitForBusy, distance, missionId))
                );
            }
            else
            {
                IceLogging.Verbose("Navmesh was not installed, so we're not even going to attempt to move", "Enqueue Navmesh");
            }
        }

        private static bool? Paths_Clear()
        {
            foreach (var path in TravelMethods)
            {
                path.Value.pathTo = null;
                path.Value.pathFrom = null;
                path.Value.destination = Vector3.Zero;
                path.Value.distance = 0;
                path.Value.Aethernet_TravelTo = 0;
                path.Value.Aethernet_TravelFrom = 0;
                path.Value.BoardEntry = Vector3.Zero;
                path.Value.BoardExit = Vector3.Zero;
            }
            return true;
        }

        public static Dictionary<uint, uint> PlanetProgress = new()
        {
            [1237] = 15,
            [1291] = 15,
            [1310] = 0,
            [1319] = 0, // Auxesia (2026.05.25追加。エーテネット未実装のため0)
        };

        private static bool? CalculateAethernet(Vector3 destination)
        {
            string tag = "Navmesh: Aethernet Calculation";
            var territoryId = Player.Territory.RowId;
            var planetProgress = PlanetProgress.GetValueOrDefault(territoryId, 0u);

            if (planetProgress == 0)
            {
                if (GenericHelpers.TryGetAddonMaster<WKSHistoryBoard>("WKSHistoryBoard", out var progress) && progress.IsAddonReady)
                {
                    PlanetProgress[territoryId] = progress.NumEntries;
                    IceLogging.Info($"We've updated the entried to contain the following value: {PlanetProgress[territoryId]}");
                    if (EzThrottler.Throttle("Closing addon"))
                        GenericHandlers.FireCallback("WKSHistoryBoard", true, -1);
                }
                else if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var hud) && hud.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Opening Progress Hud"))
                    {
                        hud.Infrastructor();
                    }
                }

                return false;
            }

            var territory = Player.Territory.RowId;
            if (!PlanetAethernet.TryGetValue(territory, out var aetherList))
            {
                IceLogging.Info("No valid aethernets, returning", tag);
                return true;
            }

            if (!C.UseAethernet)
            {
                IceLogging.Info("We have aethernet travel turned off, so continuing");
                return true;
            }

            var closestAetheryte = aetherList
                .Where(x => x.RequiredLogLv <= planetProgress)
                .OrderBy(x => Player.DistanceTo(x.Location)).FirstOrDefault();
            var destinationAetheryte = aetherList
                .Where(x => x.RequiredLogLv <= planetProgress)
                .OrderBy(x => Vector3.Distance(x.Location, destination)).FirstOrDefault();

            if (closestAetheryte == null || destinationAetheryte == null)
            {
                IceLogging.Info("Was not able to find a valid aetheryte for either going to or destination, continuing", tag);
                return true;
            }

            if (closestAetheryte.Location == destinationAetheryte.Location)
            {
                IceLogging.Info("Both aetherytes were the same ID/Location, so we don't need to take one, continuing", tag);
                return true;
            }

            var aethernet = TravelMethods[TravelTypes.Aethernet];

            var playerPosition = Player.Position;

            // Starting the distance calculations here
            if (_PathCalculations == null)
            {
                _PathCalculations = Task.Run(async () =>
                {
                    aethernet.pathTo = await FindPath(playerPosition, closestAetheryte.LandZone);
                    aethernet.pathFrom = await FindPath(destinationAetheryte.LandZone, destination);
                });
                if (EzThrottler.Throttle("Started task"))
                    IceLogging.Verbose("Started to calculate path", tag);
                return false; // Keep checking
            }

            // Wait for completion
            if (!_PathCalculations.IsCompleted)
            {
                if (EzThrottler.Throttle("Calculating path message", 1000))
                {
                    IceLogging.Verbose("Still calculating path that would be between aetherytes (via navmesh)", tag);
                }

                return false; // Still calculating
            }

            // Done!
            _PathCalculations = null; // Reset for next use
            float distance = 0;
            if (aethernet.pathTo != null)
            {
                for (int i = 0; i < aethernet.pathTo.Count - 1; i++)
                {
                    var start = aethernet.pathTo[i];
                    var end = aethernet.pathTo[i + 1];

                    distance += Vector3.Distance(start, end);
                }
            }

            if (aethernet.pathFrom != null)
            {
                for (int i = 0; i < aethernet.pathFrom.Count - 1; i++)
                {
                    var start = aethernet.pathFrom[i];
                    var end = aethernet.pathFrom[i + 1];

                    distance += Vector3.Distance(start, end);
                }
            }

            if (distance > 0)
            {
                aethernet.distance = distance;
                aethernet.Aethernet_TravelTo = closestAetheryte.AethernetId;
                aethernet.Aethernet_TravelFrom = destinationAetheryte.AethernetId;
            }

            return true;
        }
        // ボード(乗り口へ歩くと自動発進し到着地点へ運ばれる)を移動手段の一つとして評価する。CalculateAethernetを踏襲。
        // コスト = 徒歩(プレイヤー→Entry) + 徒歩(Exit→目的地)。ボードの運搬自体は実質ゼロコスト扱い。
        // 目的地がボードのExit側にある時だけこの合計が直接歩行より短く(または直接歩行が不能で)、FindBestTravelに選ばれる。
        private static bool? CalculateBoard(Vector3 destination)
        {
            string tag = "Navmesh: Board Calculation";
            var territory = Player.Territory.RowId;

            if (!C.UseBoards)
                return true;
            if (!PlanetBoards.TryGetValue(territory, out var boardList) || boardList.Count == 0)
                return true;

            // 目的地に最も近い到着地点(Exit)を持つボードを選ぶ
            var board = boardList.OrderBy(b => Vector3.Distance(b.Exit, destination)).FirstOrDefault();
            if (board == null)
                return true;

            // ★ボードはExitが目的地の近く(=ショートカットになる)時だけ使う。
            // Exitが目的地から遠い(>BoardUsefulDist)ボードを乗ると大遠回りになる(実機: 全Exitが300m超なのに
            // ボードを選び遠回りした)。また現在地より目的地に近づかないボードも無意味。どちらも不採用(distance0で除外)。
            const float BoardUsefulDist = 80f;
            float exitToDest = Vector3.Distance(board.Exit, destination);
            float playerToDest = Vector3.Distance(Player.Position, destination);
            if (exitToDest > BoardUsefulDist || exitToDest >= playerToDest)
            {
                if (EzThrottler.Throttle("Board not useful msg", 3000))
                    IceLogging.Verbose($"Board not useful (exit→dest {exitToDest:F0}m). Skipping board option.", tag);
                return true;
            }

            var boardPath = TravelMethods[TravelTypes.Board];
            var playerPosition = Player.Position;

            if (_PathCalculations == null)
            {
                _PathCalculations = Task.Run(async () =>
                {
                    boardPath.pathTo = await FindPath(playerPosition, board.Entry);   // プレイヤー→乗り口
                    boardPath.pathFrom = await FindPath(board.Exit, destination);     // 到着地点→目的地
                });
                if (EzThrottler.Throttle("Started board task"))
                    IceLogging.Verbose("Started to calculate board path", tag);
                return false;
            }

            if (!_PathCalculations.IsCompleted)
            {
                if (EzThrottler.Throttle("Calculating board path message", 1000))
                    IceLogging.Verbose("Still calculating board path (via navmesh)", tag);
                return false;
            }

            _PathCalculations = null;
            float distance = 0;
            if (boardPath.pathTo != null)
                for (int i = 0; i < boardPath.pathTo.Count - 1; i++)
                    distance += Vector3.Distance(boardPath.pathTo[i], boardPath.pathTo[i + 1]);
            if (boardPath.pathFrom != null)
                for (int i = 0; i < boardPath.pathFrom.Count - 1; i++)
                    distance += Vector3.Distance(boardPath.pathFrom[i], boardPath.pathFrom[i + 1]);

            // 両区間とも歩行経路が成立した時のみ採用(片方でも経路不能ならボードに乗っても目的地へ行けず嵌るため除外)
            if (boardPath.pathTo != null && boardPath.pathFrom != null && distance > 0)
            {
                boardPath.distance = distance;
                boardPath.BoardEntry = board.Entry;
                boardPath.BoardExit = board.Exit;
                IceLogging.Verbose($"Board candidate: entry={board.Entry} exit={board.Exit} totalWalk={distance:F1}", tag);
            }

            return true;
        }
        private static bool? CalculateDirect(Vector3 destination)
        {
            string tag = "[Navmesh: Calculate Direct]";
            var method = TravelMethods[TravelTypes.Direct];

            var playerPosition = Player.Position;

            if (_PathCalculations == null)
            {
                _PathCalculations = Task.Run(async () =>
                {
                    method.pathTo = await FindPath(playerPosition, destination);
                });
                if (EzThrottler.Throttle("Started task"))
                    IceLogging.Verbose("Started to calculate path", tag);
                return false; // Keep checking
            }

            // Wait for completion
            if (!_PathCalculations.IsCompleted)
            {
                if (EzThrottler.Throttle("Calculating path message", 1000))
                {
                    IceLogging.Verbose("Still calculating path that would be direct (via navmesh)", tag);
                }

                return false; // Still calculating
            }

            // Done!
            _PathCalculations = null; // Reset for next use
            if (method.pathTo != null)
            {
                float distance = 0;

                for (int i = 0; i < method.pathTo.Count - 1; i++)
                {
                    var start = method.pathTo[i];
                    var end = method.pathTo[i + 1];

                    distance += Vector3.Distance(start, end);
                }

                if (distance > 0)
                {
                    method.distance = distance;
                }
            }
            IceLogging.Info($"Direct Pathing Complete", tag);
            return true;
        }
        private static bool? CalculateRedAlert(uint missionId, Vector3 destination)
        {
            string tag = "Navmesh: Position -> Red Alert";
            var territoryId = Player.Territory.RowId;

            if (!C.UseRedAlertNpc)
            {
                IceLogging.Info("We were told not to use the red alert NPC travel method, so we're going to just nope out of here", tag);
                return true;
            }
            else if (NpcData.MoonNpcs.TryGetValue(territoryId, out var planetInfo))
            {
                if (planetInfo.TryGetValue(NpcData.NpcType.RedAlert, out var npcInfo))
                {
                    var method = TravelMethods[TravelTypes.RedAlert];
                    var approxStart = CosmicHelper.CriticalLocations[missionId];

                    if (_PathCalculations == null)
                    {
                        var start = Player.Position;

                        _PathCalculations = Task.Run(async () =>
                        {
                            method.pathTo = await FindPath(start, npcInfo.Location_Circle);
                            method.pathFrom = await FindPath(approxStart.RawLocation, destination);
                        });
                        if (EzThrottler.Throttle("Started task: Direct"))
                            IceLogging.Verbose("Started to calculate path", tag);
                        return false;
                    }

                    if (!_PathCalculations.IsCompleted)
                    {
                        if (EzThrottler.Throttle("Calculating path message", 1000))
                            IceLogging.Verbose("Still calculating path for red alert (via navmesh)", tag);

                        return false; // Still calculating
                    }

                    // Done!
                    _PathCalculations = null; // Reset for next use
                    float distance = 0;
                    if (method.pathTo != null && method.pathTo.Count > 1)
                    {
                        for (int i = 0; i < method.pathTo.Count - 1; i++)
                        {
                            var start = method.pathTo[i];
                            var end = method.pathTo[i + 1];
                            distance += Vector3.Distance(start, end);
                        }
                    }

                    if (method.pathFrom != null && method.pathFrom.Count > 1)
                    {
                        for (int i = 0; i < method.pathFrom.Count - 1; i++)
                        {
                            var start = method.pathFrom[i];
                            var end = method.pathFrom[i + 1];
                            distance += Vector3.Distance(start, end);
                        }
                    }

                    if (distance > 0 && method.pathTo != null && method.pathFrom != null)
                    {
                        method.distance = distance;
                    }

                    IceLogging.Info($"Hub -> Aethernet Complete", tag);
                    return true;
                }
                else
                {
                    IceLogging.Error("This territory doesn't exist... which means I fucked up and haven't added it, or it's the new planet. Lmk", tag);
                }
            }
            else
            {
                IceLogging.Error("This territory doesn't exist... which means I fucked up and haven't added it, or it's the new planet. Lmk", tag);
            }

            return true;
        }
        private static bool? CalculateHub(Vector3 destination)
        {
            string tag = "[Navmesh: Calculate Hub Path]";

            if (CosmicHelper.HubCenter.TryGetValue(Player.Territory.RowId, out var HubCenter))
            {
                var method = TravelMethods[TravelTypes.Hub_Return];

                if (!C.UseHubReturn)
                {
                    IceLogging.Info("We were told not to use hub return, so we're going to respect your decision", tag);
                }

                if (Player.DistanceTo(HubCenter) > C.HubReturn_Distance)
                {
                    if (_PathCalculations == null)
                    {
                        _PathCalculations = Task.Run(async () =>
                        {
                            TravelMethods[TravelTypes.Hub_Return].pathTo = await FindPath(HubCenter, destination);
                        });
                        if (EzThrottler.Throttle("Started task"))
                            IceLogging.Verbose("Started to calculate path", tag);
                        return false; // Keep checking
                    }

                    // Wait for completion
                    if (!_PathCalculations.IsCompleted)
                    {
                        if (EzThrottler.Throttle("Calculating path message", 1000))
                        {
                            IceLogging.Verbose("Still calculating path that would be direct (via navmesh)", tag);
                        }

                        return false; // Still calculating
                    }

                    // Done!
                    _PathCalculations = null; // Reset for next use
                    if (method.pathTo != null)
                    {
                        float distance = 0;

                        for (int i = 0; i < method.pathTo.Count - 1; i++)
                        {
                            var start = method.pathTo[i];
                            var end = method.pathTo[i + 1];

                            distance += Vector3.Distance(start, end);
                        }

                        if (distance > 0)
                        {
                            distance *= 1.2f;
                            method.distance = distance;
                        }
                    }
                    IceLogging.Info("HubPath Calculations Complete", tag);
                    return true;
                }
                else
                {
                    IceLogging.Info("We're within walking distance of the hub currently, so teleporting seems... reduntant to say the least. \n" +
                        "Going to just in turn set this to 0");
                    method.distance = 0;
                    return true;
                }
            }
            else
            {
                IceLogging.Error("No hub center is currently recorded. So we're just gonna skip this", tag);
                return true;
            }
        }
        private static bool? CalculateHubAethernet(Vector3 destination)
        {
            string tag = "[Navmesh: Calculate Hub -> Aethernet]";
            var territoryId = Player.Territory.RowId;
            var planetProgress = PlanetProgress.GetValueOrDefault(territoryId, 0u);

            if (CosmicHelper.HubCenter.TryGetValue(Player.Territory.RowId, out var HubCenter))
            {
                var method = TravelMethods[TravelTypes.Hub_Aethernet];

                if (!C.UseHubReturn)
                {
                    IceLogging.Info("We were told no hub return, so not going to do so", tag);
                    return true;
                }
                if (!C.UseAethernet)
                {
                    IceLogging.Info("We were told no teleporting via aethernet, so we shall respect this decision");
                    return true;
                }

                if (Player.DistanceTo(HubCenter) > C.HubReturn_Distance)
                {
                    var territory = Player.Territory.RowId;
                    if (!PlanetAethernet.TryGetValue(territory, out var aetherList))
                    {
                        IceLogging.Info("No valid aethernets, returning", tag);
                        return true;
                    }

                    var closestAetheryte = aetherList.OrderBy(x => Vector3.Distance(HubCenter, x.Location)).FirstOrDefault();
                    var destinationAetheryte = aetherList
                        .Where(x => x.RequiredLogLv <= planetProgress)
                        .OrderBy(x => Vector3.Distance(x.Location, destination)).FirstOrDefault();

                    if (closestAetheryte == null || destinationAetheryte == null)
                    {
                        IceLogging.Info("Was not able to find a valid aetheryte for either going to or destination, continuing", tag);
                        return true;
                    }

                    if (closestAetheryte.Location == destinationAetheryte.Location)
                    {
                        IceLogging.Info("Both aetherytes were the same ID/Location, so we don't need to take one, continuing", tag);
                        return true;
                    }

                    if (_PathCalculations == null)
                    {
                        IceLogging.Verbose($"Hub Center: {HubCenter}\n" +
                            $"Closest Aetheryte: {closestAetheryte.LandZone}\n" +
                            $"Destination Aetheryte: {destinationAetheryte.LandZone}\n" +
                            $"Destination: {destination}", tag);

                        _PathCalculations = Task.Run(async () =>
                        {
                            method.pathTo = await FindPath(HubCenter, closestAetheryte.LandZone);
                            method.pathFrom = await FindPath(destinationAetheryte.LandZone, destination);
                        });
                        if (EzThrottler.Throttle("Started task: Direct"))
                            IceLogging.Verbose("Started to calculate path", tag);
                        return false; // Keep checking
                    }

                    // Wait for completion
                    if (!_PathCalculations.IsCompleted)
                    {
                        if (EzThrottler.Throttle("Calculating path message", 1000))
                        {
                            IceLogging.Verbose("Still calculating path that would be direct (via navmesh)", tag);
                        }

                        return false; // Still calculating
                    }

                    // Done!
                    _PathCalculations = null; // Reset for next use
                    float distance = 0;
                    if (method.pathTo != null && method.pathTo.Count > 1)
                    {
                        for (int i = 0; i < method.pathTo.Count - 1; i++)
                        {
                            var start = method.pathTo[i];
                            var end = method.pathTo[i + 1];
                            distance += Vector3.Distance(start, end);
                        }
                    }

                    if (method.pathFrom != null && method.pathFrom.Count > 1)
                    {
                        for (int i = 0; i < method.pathFrom.Count - 1; i++)
                        {
                            var start = method.pathFrom[i];
                            var end = method.pathFrom[i + 1];
                            distance += Vector3.Distance(start, end);
                        }
                    }

                    if (distance > 0 && method.pathTo != null && method.pathFrom != null)
                    {
                        method.distance = distance;
                        method.Aethernet_TravelTo = closestAetheryte.AethernetId;
                        method.Aethernet_TravelFrom = destinationAetheryte.AethernetId;
                    }

                    IceLogging.Info($"Hub -> Aethernet Complete", tag);
                    return true;
                }
                else
                {
                    IceLogging.Info("Player distance to the hub center is more than 50, which seems reduntant to cast a hub return in there, so going to just set this to 0", tag);
                    method.distance = 0;
                    return true;
                }
            }
            else
            {
                IceLogging.Error("No hub center is currently recorded. So we're just gonna skip this", tag);
                return true;
            }
        }
        private static bool? CalculateRedAlertHub(uint missionId, Vector3 destination)
        {
            string tag = "Navmesh: Calculate Hub -> Red Alert";
            var territoryId = Player.Territory.RowId;
            var method = TravelMethods[TravelTypes.Hub_RedAlert];

            if (!C.UseHubReturn)
                return true;

            if (!C.UseRedAlertNpc)
                return true;

            if (!CosmicHelper.SheetMissionDict[missionId].IsCritical)
                return true;

            var approxStart = CosmicHelper.CriticalLocations[missionId];

            if (CosmicHelper.HubCenter.TryGetValue(Player.Territory.RowId, out var HubCenter))
            {
                if (NpcData.MoonNpcs.TryGetValue(territoryId, out var planetInfo))
                {
                    if (planetInfo.TryGetValue(NpcData.NpcType.RedAlert, out var npcInfo))
                    {
                        if (Player.DistanceTo(HubCenter) > C.HubReturn_Distance)
                        {
                            if (_PathCalculations == null)
                            {
                                IceLogging.Verbose("Doing the following calculations:\n" +
                                    "Hub Center -> Npc\n" +
                                    "Npc -> Teleport spot\n" +
                                    "Teleport spot -> Destination");

                                _PathCalculations = Task.Run(async () =>
                                {
                                    method.pathTo = await FindPath(HubCenter, npcInfo.Location_Circle);
                                    method.pathFrom = await FindPath(approxStart.RawLocation, destination);
                                });
                                if (EzThrottler.Throttle("Started task: Direct"))
                                    IceLogging.Verbose("Started to calculate path", tag);
                                return false; // Keep checking
                            }

                            if (!_PathCalculations.IsCompleted)
                            {
                                if (EzThrottler.Throttle("Calculating path message", 1000))
                                    IceLogging.Verbose("Still calculating path for red alert (via navmesh)", tag);

                                return false; // Still calculating
                            }

                            // Done!
                            _PathCalculations = null; // Reset for next use
                            float distance = 0;
                            if (method.pathTo != null && method.pathTo.Count > 1)
                            {
                                for (int i = 0; i < method.pathTo.Count - 1; i++)
                                {
                                    var start = method.pathTo[i];
                                    var end = method.pathTo[i + 1];
                                    distance += Vector3.Distance(start, end);
                                }
                            }

                            if (method.pathFrom != null && method.pathFrom.Count > 1)
                            {
                                for (int i = 0; i < method.pathFrom.Count - 1; i++)
                                {
                                    var start = method.pathFrom[i];
                                    var end = method.pathFrom[i + 1];
                                    distance += Vector3.Distance(start, end);
                                }
                            }

                            if (distance > 0 && method.pathTo != null && method.pathFrom != null)
                            {
                                method.distance = distance * 1.2f;
                            }

                            IceLogging.Info($"Hub -> Aethernet Complete", tag);
                            return true;
                        }
                    }
                    else
                    {
                        IceLogging.Error("This territory doesn't exist... which means I fucked up and haven't added it, or it's the new planet. Lmk", tag);
                    }
                }
                else
                {
                    IceLogging.Error("This territory doesn't exist... which means I fucked up and haven't added it, or it's the new planet. Lmk", tag);
                }
            }
            else
            {
                IceLogging.Error("We don't have any record for this hub center, so we're going to ignore it for now", tag);
            }

            return true;
        }
        private static bool? FindBestTravel(Vector3 destination, bool waitForBusy, float distance, uint missionId = 0)
        {
            var bestTravel = TravelMethods.Where(x => x.Value.distance != 0)
                .Where(x => !C.AvoidStellarReturn || (x.Key != TravelTypes.Hub_Return && x.Key != TravelTypes.Hub_Aethernet))
                .OrderBy(x => x.Value.distance)
                .FirstOrDefault();

            foreach(var travelKind in TravelMethods)
            {
                IceLogging.Verbose($"[{travelKind.Key}] = {travelKind.Value.distance}");
            }

            if (bestTravel.Key == TravelTypes.Aethernet)
            {
                randomCounter = 0; // Delay_Aethernet: travel毎に遅延カウンタをリセット
                var targetAether = bestTravel.Value.Aethernet_TravelTo;
                var AethernetLoc = PlanetAethernet[Player.Territory.RowId].Where(x => x.AethernetId == targetAether).FirstOrDefault();
                P.TaskManager.InsertMulti
                    (
                        new(() => DestinationPathing(AethernetLoc.LandZone), "Traveling to Aetheryte"),
                        new(() => TravelToAethershard(bestTravel.Value), "Traveling Via Aethernet"),
                        new(() => DestinationPathing(destination, waitForBusy, distance, mountBeforeMove: true), "Pathing to our destination: Aethernet")
                    );
            }
            else if (bestTravel.Key == TravelTypes.Hub_Return)
            {
                P.TaskManager.InsertMulti
                    (
                        new(() => Task_Repair.HubCheck(), "Returning back to hub"),
                        new(() => DestinationPathing(destination, waitForBusy, distance), "Pathing to our destination: Hub Return")
                    );
            }
            else if (bestTravel.Key == TravelTypes.Hub_Aethernet)
            {
                randomCounter = 0; // Delay_Aethernet: travel毎に遅延カウンタをリセット
                var targetAether = bestTravel.Value.Aethernet_TravelTo;
                var AethernetLoc = PlanetAethernet[Player.Territory.RowId].Where(x => x.AethernetId == targetAether).FirstOrDefault();
                P.TaskManager.InsertMulti
                    (
                        new(() => Task_Repair.HubCheck(), "Returning back to hub"),
                        new(() => DestinationPathing(AethernetLoc.LandZone), "Traveling to the hub aetheryte"),
                        new(() => TravelToAethershard(bestTravel.Value), "Travel Via Aethernet"),
                        new(() => DestinationPathing(destination, waitForBusy, distance, mountBeforeMove: true), "Pathing to our destination: HubAethernet")
                    );
            }
            else if (bestTravel.Key == TravelTypes.RedAlert)
            {
                randomCounter = 0; // Delay_Aethernet: travel毎に遅延カウンタをリセット
                var redAlertNpc = NpcData.MoonNpcs[Player.Territory.RowId][NpcData.NpcType.RedAlert];
                P.TaskManager.InsertMulti
                    (
                        new(() => DestinationPathing(redAlertNpc.Location_Circle), "Traveling to the Red Alert NPC"),
                        new(() => TravelToRedAlertNpc(redAlertNpc, missionId), "Interacting + Traveling Via RedAlert NPC"),
                        new(() => DestinationPathing(destination, waitForBusy, distance), "Pathing to our destination: Red Alert")
                    );
            }
            else if (bestTravel.Key == TravelTypes.Hub_RedAlert)
            {
                randomCounter = 0; // Delay_Aethernet: travel毎に遅延カウンタをリセット
                var redAlertNpc = NpcData.MoonNpcs[Player.Territory.RowId][NpcData.NpcType.RedAlert];
                P.TaskManager.InsertMulti
                    (
                        new(() => Task_Repair.HubCheck(), "Returning back to hub"),
                        new(() => DestinationPathing(redAlertNpc.Location_Circle), "Traveling to the Red Alert NPC"),
                        new(() => TravelToRedAlertNpc(redAlertNpc, missionId), "Interacting + Traveling Via RedAlert NPC"),
                        new(() => DestinationPathing(destination, waitForBusy, distance), "Pathing to our destination: Red Alert")
                    );
            }
            else if (bestTravel.Key == TravelTypes.Board)
            {
                _boardRidden = false; // 乗車検知をリセット
                P.TaskManager.InsertMulti
                    (
                        new(() => TravelToBoard(bestTravel.Value), "Riding board (hover platform) shortcut"),
                        new(() => DestinationPathing(destination, waitForBusy, distance, mountBeforeMove: true), "Pathing from board exit to destination")
                    );
            }
            else
            {
                P.TaskManager.Insert(() => DestinationPathing(destination, waitForBusy, distance), "Pathing to our destination: Basic");
            }

            return true;
        }
        private static bool _boardRidden = false;
        // ボード移動の実行: 乗り口(Entry)へ歩く→乗ると自動発進(Condition 101でnavmesh停止=既存処理)→到着地点(Exit)で完了。
        // オブジェクト操作は不要。Entry座標へ到達すると発進するので、Entryへ歩いて乗車→運搬を待つ→Exit到着で次タスクへ。
        private static unsafe bool? TravelToBoard(PathInfo board)
        {
            string tag = "[Navmesh: Board ride]";
            var entry = board.BoardEntry;
            var exit = board.BoardExit;

            // 乗車中(Condition 101): ボードが運んでいる。navmeshは既存処理が止めるが念のため止め、待つ。
            if (Svc.Condition[ConditionFlag.Unknown101])
            {
                _boardRidden = true;
                if (P.Navmesh.IsRunning())
                    P.Navmesh.Stop();
                if (EzThrottler.Throttle("Board riding message", 1000))
                    IceLogging.Verbose("Riding the board, waiting for arrival...", tag);
                return false;
            }

            // 到着地点付近に居る → 完了(EntryとExitは遠く離れているため、Exit付近に居る=乗車して運ばれた証拠)。
            // Condition 101の検知に依存せず位置で判定するので、乗車フラグが万一拾えなくても確実に完了できる。
            if (Player.DistanceTo(exit) < 10f)
            {
                IceLogging.Info("Board ride complete, arrived near exit", tag);
                return true;
            }

            // まだ乗っていない＆到着していない → 乗り口へ歩いて乗車させる
            if (Player.DistanceTo(entry) > 1.5f)
            {
                if (!Task_NavTo(entry, waitForBusy: false, distance: 1.5f).Value)
                {
                    if (EzThrottler.Throttle("Board entry move message", 1000))
                        IceLogging.Verbose($"Moving to board entry. Distance: {Player.DistanceTo(entry):F1}", tag);
                }
                return false;
            }

            // 乗り口に到達済みだがまだ発進していない → 発進を待つ(数tick)。発進しなければスタック検知/スキップに委ねる。
            if (P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            if (EzThrottler.Throttle("Board waiting to launch", 1000))
                IceLogging.Verbose("At board entry, waiting for it to launch...", tag);
            return false;
        }

        private static int counter = 0;
        // 公式0.0.78.1より移植: Delay_Aethernet(エーテネット/NPC移動前のランダム遅延)用カウンタ。travel投入毎にリセット。
        private static int randomCounter = 0;

        private static unsafe bool? TravelToAethershard(PathInfo shardInfo)
        {
            string tag = "[Navmesh: Aethershard movement]";

            uint targetId = shardInfo.Aethernet_TravelTo;
            uint destinationId = shardInfo.Aethernet_TravelFrom;

            int menuId = 0; // TODO: Actually code this in, esentially grab the destationId -> grab the submenuID

            var territoryId = Player.Territory.RowId;
            var targetAether = PlanetAethernet[territoryId].Where(x => x.AethernetId == targetId).FirstOrDefault();
            var destinationAether = PlanetAethernet[territoryId].Where(x => x.AethernetId == destinationId).FirstOrDefault();

            menuId = destinationAether.MapSelector;

            if (Player.DistanceTo(targetAether.Location) < 5)
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TelepotTown", out var TelepotTown) && TelepotTown->IsReady)
                {
                    // 公式移植: テレポート確定の直前に人間らしいランダム遅延(検知回避)
                    if (C.Delay_Aethernet)
                    {
                        int delay = _random.Next(1999, 6001);
                        if (EzThrottler.Throttle("Delay for aethernet travel", delay))
                            randomCounter += 1;

                        if (randomCounter < 2)
                        {
                            if (EzThrottler.Throttle("Wait for random encounter"))
                                IceLogging.Verbose("Waiting for the random timer to fully randomize", tag);
                            return false;
                        }
                    }

                    if (EzThrottler.Throttle("Use Aethernet", 100))
                    {
                        GenericHandlers.FireCallback("TelepotTown", true, 11, menuId);
                    }
                }
                else
                {
                    var aethernet = Svc.Objects.Where(x => x.BaseId == targetId).FirstOrDefault();
                    if (aethernet != null)
                    {
                        if (Player.Mounted || Player.IsJumping)
                        {
                            Utils.Dismount();
                            return false;
                        }
                        else if (!Player.IsBusy)
                        {
                            Utils.TargetgameObject(aethernet);
                            Utils.InteractWithObject(aethernet);
                        }
                    }
                }
            }
            else if (Player.DistanceTo(destinationAether.Location) < 10)
            {
                if (!PlayerHelper.IsScreenReady())
                {
                    return false;
                }
                else
                {
                    IceLogging.Info("We've reached our destination!", tag);
                    return true;
                }
            }

            return false;
        }
        private static unsafe bool? TravelToRedAlertNpc(NpcData.NPCInfo redAlertNpc, uint missionId)
        {
            string tag = "Travel: Via RedAlert NPC";

            IceLogging.Verbose("Travel via Npc commenced", tag);

            if (CosmicHelper.CriticalLocations.TryGetValue(missionId, out var redAlert))
            {
                if (Player.DistanceTo(redAlertNpc.Location_Circle) < 5)
                {
                    IceLogging.Verbose("Close enough to npc to travel", tag);

                    // 公式移植: NPC経由テレポートの選択直前にランダム遅延(検知回避)
                    if (C.Delay_Aethernet)
                    {
                        int delay = _random.Next(1999, 6001);
                        if (EzThrottler.Throttle("Delay for npc travel", delay))
                            randomCounter += 1;

                        if (randomCounter < 2)
                        {
                            if (EzThrottler.Throttle("Wait for random encounter"))
                                IceLogging.Verbose("Waiting for the random timer to fully randomize", tag);
                            return false;
                        }
                    }

                    if (GenericHelpers.TryGetAddonMaster<SelectString>(out var selectString) && selectString.IsAddonReady)
                    {
                        if (EzThrottler.Throttle("Selecting teleport option"))
                        {
                            IceLogging.Verbose($"Selecting Option: {redAlert.NpcSelection} for mission: {missionId}", tag);
                            selectString.Entries[redAlert.NpcSelection].Select();
                        }
                    }
                    else if (GenericHelpers.TryGetAddonMaster<SelectYesno>(out var yesNo) && yesNo.IsAddonReady)
                    {
                        if (EzThrottler.Throttle("Selecting yes"))
                            yesNo.Yes();
                    }
                    else if (GenericHelpers.TryGetAddonMaster<Talk>(out var talk) && talk.IsAddonReady)
                    {
                        if (EzThrottler.Throttle("Clicking on talk", 100))
                        {
                            talk.Click();
                        }
                    }
                    else
                    {
                        if (EzThrottler.Throttle("Log Message"))
                            IceLogging.Verbose("Should be interacting here...", tag);

                        var npc = Svc.Objects.Where(x => x.BaseId == redAlertNpc.NpcId).FirstOrDefault();
                        if (npc != null)
                        {
                            if (Player.Mounted || Player.IsJumping)
                            {
                                Utils.Dismount();
                                return false;
                            }

                            if (EzThrottler.Throttle("Target + Interact"))
                            {
                                Utils.TargetgameObject(npc);
                                Utils.InteractWithObject(npc);
                            }

                            return false;
                        }
                    }
                }
                else if (Player.DistanceTo(redAlert.RawLocation) < 75)
                {
                    if (!PlayerHelper.IsScreenReady())
                        return false;
                    else

                    IceLogging.Info("We've reached the red alert destination, need to just do the final pathing", tag);
                    return true;
                }
            }
            else
            {
                return true;
            }



            return false;
        }
        private static bool? DestinationPathing(Vector3 pos, bool waitForBusy = true, float distance = 2.0f, bool stayMounted = false, Vector3? npcLoc = null, bool mountBeforeMove = false)
        {
            string tag = "Navmesh Move: Destination Pathing";

            if (!Task_NavTo(pos, waitForBusy, distance, stayMounted, npcLoc, mountBeforeMove:mountBeforeMove).Value)
            {
                return false;
            }
            else
            {
                IceLogging.Info($"We've reached our destination: {pos}", tag);
                return true;
            }
        }
        private static async Task<List<Vector3>> FindPath(Vector3 position, Vector3 destination)
        {
            return await P.Navmesh.Pathfind(position, destination, false);
        }

        #endregion

        #region Gathering Functions

        /// <summary>
        /// Converts Pictomancy coordinates to FFXIV world coordinates.
        /// Pictomancy: 0=South, 90=West, 180=North, 270=East
        /// FFXIV:      0=North, 90=East, 180=South, 270=West
        /// </summary>
        private static float PictomancyToFFXIV(float pictomancyAngle)
        {
            float ffxivAngle = (pictomancyAngle + 180f) % 360f;
            if (ffxivAngle < 0f)
                ffxivAngle += 360f;
            return ffxivAngle;
        }

        private static float CalculateAngleToPlayer(Vector3 nodePos, Vector3 playerPos)
        {
            Vector3 direction = playerPos - nodePos;
            float angle = MathF.Atan2(direction.X, direction.Z) * (180f / MathF.PI);
            angle = 180f - angle;

            if (angle < 0f)
                angle += 360f;
            else if (angle >= 360f)
                angle -= 360f;

            return angle;
        }

        private static float NormalizeAngle(float angle)
        {
            angle = angle % 360f;
            if (angle < 0f)
                angle += 360f;
            return angle;
        }

        private static float GetRangeSpan(float min, float max)
        {
            float diff = MathF.Abs(max - min);
            if (MathF.Abs(diff - 360f) < 0.01f)
                return 360f;

            min = NormalizeAngle(min);
            max = NormalizeAngle(max);

            float span = max - min;
            if (span < 0)
                span += 360f;

            return span;
        }

        private static bool IsAngleInRange(float angle, float min, float max)
        {
            angle = NormalizeAngle(angle);
            min = NormalizeAngle(min);
            max = NormalizeAngle(max);

            float rangeSpan = max - min;
            if (rangeSpan < 0)
                rangeSpan += 360f;

            if (rangeSpan >= 360f)
                return true;

            if (min <= max)
                return angle >= min && angle <= max;
            else
                return angle >= min || angle <= max;
        }

        private static float GetAngularDistance(float angle1, float angle2)
        {
            angle1 = NormalizeAngle(angle1);
            angle2 = NormalizeAngle(angle2);

            float diff = angle2 - angle1;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;

            return MathF.Abs(diff);
        }

        private static float ClampAngleToRange(float angle, float allowedMin, float allowedMax, bool preferMin)
        {
            angle = NormalizeAngle(angle);

            if (IsAngleInRange(angle, allowedMin, allowedMax))
                return angle;

            float distToMin = GetAngularDistance(angle, allowedMin);
            float distToMax = GetAngularDistance(angle, allowedMax);

            if (MathF.Abs(distToMin - distToMax) < 0.01f)
                return preferMin ? allowedMin : allowedMax;

            return distToMin < distToMax ? allowedMin : allowedMax;
        }

        private static (float sectionMin, float sectionMax) GetNearestSection(float allowedMin, float allowedMax, float targetAngle, float sectionSize)
        {
            float rangeSpan = GetRangeSpan(allowedMin, allowedMax);

            if (rangeSpan >= 359.9f)
            {
                float halfSection = sectionSize / 2f;
                return (NormalizeAngle(targetAngle - halfSection), NormalizeAngle(targetAngle + halfSection));
            }

            allowedMin = NormalizeAngle(allowedMin);
            allowedMax = NormalizeAngle(allowedMax);
            targetAngle = NormalizeAngle(targetAngle);

            float closestPointInRange = IsAngleInRange(targetAngle, allowedMin, allowedMax)
                ? targetAngle
                : GetAngularDistance(targetAngle, allowedMin) < GetAngularDistance(targetAngle, allowedMax)
                    ? allowedMin
                    : allowedMax;

            float half = sectionSize / 2f;
            float secMin = NormalizeAngle(closestPointInRange - half);
            float secMax = NormalizeAngle(closestPointInRange + half);

            secMin = ClampAngleToRange(secMin, allowedMin, allowedMax, true);
            secMax = ClampAngleToRange(secMax, allowedMin, allowedMax, false);

            return (secMin, secMax);
        }

        private static float RandomAngleInRange(float min, float max)
        {
            min = NormalizeAngle(min);
            max = NormalizeAngle(max);

            if (min <= max)
                return NextFloat(min, max);

            float rangeSize = (360f - min) + max;
            return NormalizeAngle(min + NextFloat(0, rangeSize));
        }

        private static float NextFloat(float min, float max)
        {
            return min + (float)_random.NextDouble() * (max - min);
        }

        private static Vector3 CalculateFanPosition(Vector3 center, float angleDegrees, float distance, float height)
        {
            float standardAngle = 180f - angleDegrees;
            float angleRadians = standardAngle * (MathF.PI / 180f);

            var pos = new Vector3(
                center.X + distance * MathF.Sin(angleRadians),
                center.Y,
                center.Z + distance * MathF.Cos(angleRadians)
            );

            // 立ち位置のYはノードのY(GameObject原点)のままだと、岩場等でノード原点が地表から浮く/めり込む場所では
            // 後段の NearestPointReachable が到達可能点を見つけられず、NearestPointフォールバックで到達不可点(谷の先/
            // 岩の上)へ吸着して不正パスになる。立ち位置XZ直下の地表(navmesh floor)へYを投影して補正する。
            var floor = P.Navmesh.PointOnFloor(pos, false, 5f);
            if (floor.HasValue)
                pos.Y = floor.Value.Y;

            return pos;
        }

        #endregion

        private static void CloseExtraWindows()
        {
            if (EzThrottler.Throttle("Closing Extra Windows"))
            {
                if (GenericHelpers.TryGetAddonMaster<ShopExchangeCurrency>("ShopExchangeCurrency", out var shopExchange) && shopExchange.IsAddonReady)
                {
                    GenericHandlers.FireCallback("ShopExchangeCurrency", true, -1);
                }
            }
        }
    }
}