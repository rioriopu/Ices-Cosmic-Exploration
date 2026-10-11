using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// SmoothNavDriver(野外の移動の角を曲線に整える)を ICE につなぐ層。Task_NavmeshMove から呼ぶ。
    /// 使うのは宇宙探査の野外だけ: 拠点の近く(拠点帰還の判定距離 HubReturn_Distance の内側)を出発点か目的地とする移動は、
    /// 建物が多く終点が壁際になりやすいので、今までどおり vnavmesh 任せにする。
    /// 呼ぶのはすべて画面のスレッド(TaskManager のタスク・状態遷移)から。
    /// </summary>
    internal static class SmoothNavBridge
    {
        private const string Tag = "[SmoothNav]";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static SmoothNavDriver? driver;
        private static Vector3 lastGoal;
        private static bool hasGoal;

        private static double Now => Clock.Elapsed.TotalSeconds;
        private static SmoothNavDriver Driver => driver ??= new SmoothNavDriver(new Port());

        /// <summary>
        /// 経路を頼むところ(HandleStartNavmesh)で呼ぶ。曲線にする移動を始めたら true。false なら呼び出し側は今までどおり
        /// vnavmesh 任せ(SimpleMove.PathfindAndMoveTo)で頼む。
        /// </summary>
        public static bool TryBegin(Vector3 goal)
        {
            lastGoal = goal;
            hasGoal = true;
            if (!C.SmoothNavMovement || !Player.Available)
                return false;
            var from = Player.Position;
            if (!InField(from, goal, out var why))
            {
                if (EzThrottler.Throttle("SmoothNav: not in field", 10000))
                    IceLogging.Debug($"曲線にせず vnavmesh 任せ: {why}", Tag);
                return false;
            }
            if (Driver.TryBegin(from, goal, Now, out var reason))
            {
                IceLogging.Debug($"経路を頼みました(曲線にする移動): {from:N1} → {goal:N1}", Tag);
                return true;
            }
            IceLogging.Debug($"曲線にせず vnavmesh 任せ: {reason}", Tag);
            return false;
        }

        /// <summary>
        /// 移動の毎 tick に、vnavmesh が走っているかの判定より前に呼ぶ。Planning/Applied の間は「移動中」として扱う。
        /// </summary>
        public static SmoothGate Gate(Vector3 goal, bool arrived, bool ridingTransport)
        {
            if (driver == null || !driver.Planning)
                return SmoothGate.None;
            var result = driver.Gate(Player.Position, goal, arrived, ridingTransport, Now);
            if (driver.LastSummary.Length > 0)
            {
                // 1 回の移動につき 1 行。ICE のログ画面と dalamud.log の両方に残す(実機で「なぜ曲線にならなかったか」を後から読む)
                var line = "移動の経路: " + driver.LastSummary;
                IceLogging.Info(line, Tag);
                Svc.Log.Information($"{Tag} {line}");
                if (driver.LastDetail.Length > 0)
                    IceLogging.Debug("曲線にしなかった角の理由: " + driver.LastDetail, Tag);
                driver.ClearSummary();
            }
            return result;
        }

        /// <summary>自分の計画をやめる(止めた・状態が変わった・プラグインの終了)。</summary>
        public static void Cancel() => driver?.Cancel();

        /// <summary>今の目的地で詰まった(ジャンプ・リターゲット)。印の寿命の間、この目的地は公式の経路で動かす。</summary>
        public static void MarkStuck(string why)
        {
            if (driver == null || !hasGoal)
                return;
            driver.Cancel();
            driver.MarkOfficial(lastGoal, Now, why);
        }

        /// <summary>目的地に着いた。その目的地の印を外す(次に同じ場所へ行くときは、また曲線を試す)。</summary>
        public static void OnArrived()
        {
            if (driver != null && hasGoal)
                driver.ClearMarks(lastGoal);
        }

        private static bool InField(Vector3 from, Vector3 goal, out string why)
        {
            if (!CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var hub))
            {
                why = "宇宙探査の地図ではない";
                return false;
            }
            float radius = C.HubReturn_Distance;
            if (Vector3.Distance(from, hub) < radius || Vector3.Distance(goal, hub) < radius)
            {
                why = $"拠点の近く({radius:F0}m 以内)";
                return false;
            }
            why = "";
            return true;
        }

        /// <summary>vnavmesh の IPC で ISmoothNavPort を実装する。NearestPoint は整える処理(裏のスレッド)から呼ばれる。</summary>
        private sealed class Port : ISmoothNavPort
        {
            public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to) => P.Navmesh.Pathfind(from, to, false);

            public Vector3? NearestPoint(Vector3 point, float halfExtentXZ, float halfExtentY)
            {
                // 点の照会は地図を読むだけで、経路探索の作業領域を使わない。呼べない(vnavmesh が無い・地図の読み直し中)ときは
                // 「面が無い」= その区間は曲線にしない
                try { return P.Navmesh.NearestPoint(point, halfExtentXZ, halfExtentY); }
                catch (Exception) { return null; }
            }

            public bool PathIsRunning()
            {
                try { return P.Navmesh.IsRunning(); }
                catch (Exception) { return false; }
            }

            public bool SimpleMovePending()
            {
                try { return P.Navmesh.PathfindInProgress(); }
                catch (Exception) { return false; }
            }

            public void MoveAlong(List<Vector3> points)
            {
                // Tolerance はプロセス全体で 1 つの値。他のプラグインが大きくしていると曲線の角を切るので、渡す直前に戻す
                P.Navmesh.SetTolerance(0.25f);
                P.Navmesh.MoveTo(points, false);
            }
        }
    }
}
