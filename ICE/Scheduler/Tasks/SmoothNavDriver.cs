using SmoothNav.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// SmoothNavDriver がゲームに触れる口。ICE では SmoothNavBridge が vnavmesh の IPC で実装し、単体試験では偽物を渡す。
    /// NearestPoint(IMeshPointQuery)は裏のスレッドから呼ばれる。
    /// </summary>
    internal interface ISmoothNavPort : IMeshPointQuery
    {
        /// <summary>公式の経路探索(vnavmesh.Nav.Pathfind、地上)。頼めなければ null。</summary>
        Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to);
        /// <summary>vnavmesh の追従が走っているか(Path.IsRunning)。</summary>
        bool PathIsRunning();
        /// <summary>vnavmesh 任せの探索(SimpleMove)が保留中か。</summary>
        bool SimpleMovePending();
        /// <summary>点列を辿らせる(Tolerance を 0.25 に戻してから Path.MoveTo(points, fly:false))。</summary>
        void MoveAlong(List<Vector3> points);
    }

    /// <summary>Gate の結果。Planning/Applied の間、呼び出し側は「移動中」として扱い、vnavmesh への頼み直しをしない。</summary>
    internal enum SmoothGate { None, Planning, Applied }

    /// <summary>
    /// 野外の移動の角を曲線に整える(SmoothNav の RoutePlanner)。ICE の移動(Task_NavTo / Task_GatherMove)は到着・詰まり・頼み直しを
    /// 自前で判定するので、移動の管理ごと任せる SmoothNavigator ではなく、経路だけを整える RoutePlanner を使う。
    /// 流れ: TryBegin(公式の探索を頼み、リンクの見張りを掛けて RoutePlanner に渡す)→ 毎 tick の Gate(整うまで Planning、
    /// 整ったら Path.MoveTo で辿らせて Applied)。整えられない・間に合わない(0.25 秒)・探索の失敗・リンクの疑いのときは、
    /// その目的地に「公式に戻す」印を付け、呼び出し側は今までどおり vnavmesh 任せ(SimpleMove.PathfindAndMoveTo)で頼む。
    /// 途中からの乗り換え(遅れて整った経路への切り替え)は使わない: 計画は最初の経路を渡した時点で必ず終わり、
    /// ICE の各所の Stop の後に遅れて経路が届いて勝手に歩き出すことが原理的に起きない。
    /// ゲームに触れないので単体試験できる(tools/SmoothNavDriverTests)。
    /// </summary>
    internal sealed class SmoothNavDriver
    {
        /// <summary>同じ目的地とみなす水平距離(NPC などの目標点は半径 0.5 の乱数で揺れる)。</summary>
        public const float SameGoalDistance = 1.5f;
        /// <summary>計画中(探索・整える間)にこれだけ動いたら、古い位置から引いた計画として捨てる。</summary>
        public const float StaleMoveDistance = 2f;
        /// <summary>「公式に戻す」印の寿命(秒)。同じ納品点・ノードへ毎周回行くので、一度の失敗で永久に曲線をやめない。</summary>
        public const double OfficialMarkSeconds = 60;
        /// <summary>見張りで、最終区間の行き止まりを「目的地がメッシュの少し外」とみなす距離(終点から)。</summary>
        public const float GuardEndAllowance = 1.5f;
        /// <summary>見張りで、最終区間の「終点が別の高さの面」を目的地の高さのずれとみなす上限。リンクの多くは高さの差が大きい。</summary>
        public const float GuardEndHeight = 3f;

        private readonly ISmoothNavPort port;
        private readonly RoutePlanner planner;
        private readonly ThinningRefiner? thinning;
        private readonly List<(Vector3 Goal, double Until, string Why)> officialMarks = new();
        private bool planning;
        private Vector3 goal, beginPosition;
        // 見張りが疑ったときの詳しい理由(裏のスレッドで書き、探索 Task の完了後に画面のスレッドで読む)
        private volatile string guardDetail = "";

        public SmoothNavDriver(ISmoothNavPort port, IPathRefiner? refiner = null)
        {
            this.port = port;
            // 地上の窓口は点の照会だけで作る(MeshPointGround)。覚えの辞書はスレッド安全でないので要求ごとに作る。飛行は使わない。
            // 整えた曲線は 0.5m ごとの点なので、間引いてから渡す(ThinningRefiner。視点が小刻みに振れるのを防ぐ)
            if (refiner == null)
                thinning = new ThinningRefiner(new CurvePathRefiner(_ => [], () => new MeshPointGround(port)), () => new MeshPointGround(port));
            this.planner = new RoutePlanner(refiner ?? thinning)
            {
                RefineBudgetSeconds = .25,
                LateRefineSeconds = .25, // 予算と同じ = 途中からの乗り換えを使わない
                SearchTimeoutSeconds = 25,
            };
        }

        /// <summary>経路を整えている最中か(最初の経路を渡すまで)。</summary>
        public bool Planning => planning;
        /// <summary>直近に経路を渡した/戻したときの 1 行の要約(記録用)。読んだら ClearSummary。</summary>
        public string LastSummary { get; private set; } = "";
        /// <summary>直近の要約の、曲線にしなかった角の理由の内訳(記録用)。</summary>
        public string LastDetail { get; private set; } = "";
        public void ClearSummary() { LastSummary = ""; LastDetail = ""; }

        /// <summary>
        /// 経路を頼む。曲線にしないとき(この目的地に印がある・vnavmesh 任せの探索が保留中・探索を頼めない)は false を返すので、
        /// 呼び出し側は今までどおり vnavmesh 任せで頼む。
        /// </summary>
        public bool TryBegin(Vector3 from, Vector3 to, double now, out string reason)
        {
            Cancel();
            PruneMarks(now);
            var mark = FindMark(to, now);
            if (mark != null)
            {
                reason = "この目的地は公式の経路で動かす(" + mark + ")";
                return false;
            }
            if (port.SimpleMovePending())
            {
                reason = "vnavmesh 任せの探索が保留中";
                return false;
            }
            Task<List<Vector3>>? search;
            try
            {
                search = port.Pathfind(from, to);
            }
            catch (Exception ex)
            {
                reason = "探索を頼めない: " + ex.GetType().Name;
                return false;
            }
            if (search == null)
            {
                reason = "探索を頼めない";
                return false;
            }
            guardDetail = "";
            // 探索の結果に見張りを掛けてから整える部品へ渡す。探索が失敗していれば Result が例外を投げ、整える部品は Fallback にする
            var guarded = search.ContinueWith(t => Guard(t.Result), CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            planner.Begin(guarded, false, 0, now);
            planning = true;
            goal = to;
            beginPosition = from;
            reason = "";
            return true;
        }

        /// <summary>
        /// 移動の毎 tick に、vnavmesh が走っているかの判定より前に呼ぶ。currentGoal は今の目的地、arrived は呼び出し側の到着判定。
        /// Planning/Applied なら呼び出し側は「移動中」として扱い、None なら今までの処理へ進む。
        /// </summary>
        public SmoothGate Gate(Vector3 position, Vector3 currentGoal, bool arrived, bool ridingTransport, double now)
        {
            if (!planning)
                return SmoothGate.None;
            // 計画をやめる場面: 乗り物に乗った・目的地が変わった・着いた・他の移動が始まった・計画中に動いた
            if (ridingTransport || HorizontalDistance(currentGoal, goal) > SameGoalDistance || arrived
                || (planner.Pending && port.PathIsRunning())
                || (planner.Pending && HorizontalDistance(position, beginPosition) > StaleMoveDistance))
            {
                Cancel();
                return SmoothGate.None;
            }

            var plan = planner.Tick(position, now);
            if (plan == null)
            {
                if (planner.Pending)
                    return SmoothGate.Planning;
                planning = false;
                return SmoothGate.None;
            }

            planning = false;
            if (plan.Fallback)
            {
                // 公式の経路が得られない・リンクの疑い → この目的地は今までどおり vnavmesh 任せ(SimpleMove は経路点の種別を保つので、
                // コスモライナーの乗車待ちも働く)
                var why = guardDetail.Length > 0 ? "リンクの疑い: " + guardDetail : plan.Reason;
                MarkOfficial(goal, now, why);
                LastSummary = $"vnavmesh 任せへ戻す({why}・{plan.Seconds:F2} 秒)";
                LastDetail = "";
                return SmoothGate.None;
            }

            Describe(plan);
            if (plan.Waypoints.Count == 0)
                return SmoothGate.None; // 既に目的地(経路の終点)にいる。到着は呼び出し側が判定する
            if (port.SimpleMovePending())
            {
                // 他の頼み(vnavmesh 任せ)が保留中。今渡しても後から上書きされるので渡さない
                LastSummary += "(vnavmesh 任せの探索が保留中のため渡さない)";
                return SmoothGate.None;
            }
            port.MoveAlong(plan.Waypoints.ToList());
            return SmoothGate.Applied;
        }

        /// <summary>自分の計画をやめる(止めた・目的地を変えた・状態が変わったとき)。整える処理も取り消し、以後は何も渡さない。</summary>
        public void Cancel()
        {
            if (!planning)
                return;
            planner.Cancel();
            planning = false;
        }

        /// <summary>この目的地を、印の寿命の間は公式の経路(vnavmesh 任せ)で動かす(詰まった・失敗したとき)。</summary>
        public void MarkOfficial(Vector3 target, double now, string why)
        {
            officialMarks.RemoveAll(m => HorizontalDistance(m.Goal, target) <= SameGoalDistance);
            officialMarks.Add((target, now + OfficialMarkSeconds, why));
        }

        /// <summary>着いた目的地の印を外す(次に同じ場所へ行くときは、また曲線を試す)。</summary>
        public void ClearMarks(Vector3 target) => officialMarks.RemoveAll(m => HorizontalDistance(m.Goal, target) <= SameGoalDistance);

        private string? FindMark(Vector3 target, double now)
        {
            foreach (var m in officialMarks)
                if (m.Until > now && HorizontalDistance(m.Goal, target) <= SameGoalDistance)
                    return m.Why;
            return null;
        }

        private void PruneMarks(double now) => officialMarks.RemoveAll(m => m.Until <= now);

        /// <summary>
        /// 公式の経路に、コスモライナーの乗り口などのリンク(メッシュ外の連結)が含まれていないかを点の照会で確かめる。
        /// Path.MoveTo を通すと経路点の種別が落ち、乗り口で乗車を待てなくなる(vnavmesh IPCProvider の Path.MoveTo / FollowPath)。
        /// 歩ける面を続けて辿れない区間が 1 つでもあれば(長さを問わない)、例外で探索を失敗させて Fallback にする。
        /// ただし次は目的地や自機の位置のずれとして見逃す: 第 1 区間の「始点が歩ける面の外」(自機が面の少し外)、
        /// 最終区間で行き止まりが終点から GuardEndAllowance 以内、最終区間の「終点が別の高さの面」で高さの差が GuardEndHeight 以内。
        /// </summary>
        private List<Vector3> Guard(List<Vector3> path)
        {
            if (path == null || path.Count < 2)
                return path ?? [];
            var ground = new MeshPointGround(port);
            for (var i = 1; i < path.Count; i++)
            {
                var a = path[i - 1];
                var b = path[i];
                var r = ground.Segment(a, b, false);
                if (r.Verdict == Verdict.Clear)
                    continue;
                var first = i == 1;
                var last = i == path.Count - 1;
                if (r.Verdict == Verdict.Blocked && first && r.Reason.StartsWith("始点が歩ける面の外", StringComparison.Ordinal))
                    continue;
                if (r.Verdict == Verdict.Blocked && last && EndOffMesh(a, b, r))
                    continue;
                guardDetail = $"区間 {i}/{path.Count - 1}: {(r.Reason.Length > 0 ? r.Reason : r.Verdict.ToString())}"
                              + $"(水平 {HorizontalDistance(a, b):F1}m・高さ {b.Y - a.Y:F1}m)";
                throw new SuspectedLinkException(guardDetail);
            }
            return path;
        }

        private static bool EndOffMesh(Vector3 a, Vector3 b, ProbeResult r)
        {
            if (r.Reason.StartsWith("終点が別の高さの面", StringComparison.Ordinal))
                return MathF.Abs(b.Y - a.Y) <= GuardEndHeight;
            return r.Point is { } at && HorizontalDistance(at, b) <= GuardEndAllowance;
        }

        // 記録の 1 行(どう整えたか・理由・変えた区間・点・時間)と、曲線にしなかった角の理由の内訳
        private void Describe(RoutePlan plan)
        {
            var decisions = plan.Refined?.Decisions;
            var changed = decisions?.Count(d => d.Changed) ?? 0;
            var kind = plan.Smoothed ? "曲線に整えた経路" : "公式の経路";
            var thinned = plan.Smoothed && thinning != null && thinning.LastDenseCount > 0 ? $"(間引く前 {thinning.LastDenseCount - 1})" : "";
            LastSummary = $"{kind}({plan.Reason}・変えた区間 {changed}/{decisions?.Length ?? 0}・点 {plan.Waypoints.Count}{thinned}・{plan.Seconds:F2} 秒)";
            LastDetail = decisions == null ? "" : string.Join(" / ", decisions.Where(d => !d.Changed)
                .GroupBy(d => d.Reason).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ×{g.Count()}"));
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>見張りが、公式の経路にリンク(コスモライナーの乗り口など)らしい区間を見つけた。</summary>
    internal sealed class SuspectedLinkException(string detail) : Exception(detail);
}
