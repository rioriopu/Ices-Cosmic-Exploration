using SmoothNav.Core;
using System.Collections.Generic;
using System.Threading;

namespace ICE.Scheduler.Tasks
{
    /// <summary>
    /// 曲線に整えた経路の点を間引いてから渡す(地上だけ)。
    /// SmoothNav の地上の曲線は 0.5m ごとに点を打つ。vnavmesh の追従は「次の点」へ向かい、カメラも「次の点」へ向ける
    /// (FollowPath.cs:143, 158)ので、次の点が数十 cm 先しかないと、少しのずれで向かう向きとカメラが小刻みに振れる
    /// (2026-10-13 実機: 曲線を使った移動だけ視点がおかしくなった)。曲線からのずれを MaxDeviation 以内に保ったまま点を減らし、
    /// 次の点までの距離を確保する。
    /// </summary>
    internal sealed class ThinningRefiner(IPathRefiner inner, Func<IGroundSurface?> ground) : IPathRefiner
    {
        private volatile int lastDenseCount;

        /// <summary>直近に間引いた経路の、間引く前の点の数(記録用)。</summary>
        public int LastDenseCount => lastDenseCount;

        public CurveResult Refine(IReadOnlyList<Vector3> official, bool fly, CancellationToken cancel)
        {
            var result = inner.Refine(official, fly, cancel);
            if (fly || cancel.IsCancellationRequested || !MovementSession.Usable(result))
                return result;
            // 覚えの辞書はスレッド安全でないので、窓口は要求ごとに作る
            var thinned = PathThinner.Thin(result.UsedPath, official, ground(), cancel);
            lastDenseCount = result.UsedPath.Length;
            return result with { UsedPath = thinned };
        }
    }

    /// <summary>点列を、元の線からのずれが MaxDeviation 以内に収まる範囲で間引く(水平で測る。地上の追従は高さを見ない)。</summary>
    internal static class PathThinner
    {
        /// <summary>間引いた線と、飛ばした点との水平のずれの上限。曲線は壁から 0.75m 離して作るので、その内側に収まる。</summary>
        public const float MaxDeviation = .2f;
        /// <summary>1 本の線で飛ばす点の数の上限(計算量の上限)。</summary>
        private const int MaxWindow = 400;

        /// <summary>
        /// 始点・終点と、公式の経路の点(曲線にしなかった角。メッシュの縁に接している)は必ず残す。
        /// 新しく作る線(元の隣どうしでない点を結ぶ線)は、歩ける面を続けて辿れるかを ground で確かめ、通らなければ短くする。
        /// ground が無ければ間引かない。打ち切られたら元の点列を返す。
        /// </summary>
        public static Vector3[] Thin(IReadOnlyList<Vector3> path, IReadOnlyList<Vector3> official, IGroundSurface? ground, CancellationToken cancel = default)
        {
            if (path.Count <= 2 || ground == null)
                return [.. path];
            var pinned = new bool[path.Count];
            for (var i = 0; i < path.Count; i++)
                pinned[i] = i == 0 || i == path.Count - 1 || official.Any(o => Horizontal(o, path[i]) <= .01f);

            var kept = new List<Vector3> { path[0] };
            var a = 0;
            while (a < path.Count - 1)
            {
                if (cancel.IsCancellationRequested)
                    return [.. path];
                // ずれの上限の中で一番遠い点(残す点は飛ばさない)
                var far = a + 1;
                for (var j = a + 2; j < path.Count && j - a <= MaxWindow; j++)
                {
                    if (pinned[j - 1] || !WithinDeviation(path, a, j))
                        break;
                    far = j;
                }
                // 新しく作った線は歩ける面で確かめる。通らなければ半分ずつ手前へ(隣の点への線は整える処理が確かめ済み)
                var b = far;
                while (b > a + 1 && ground.Segment(path[a], path[b], false).Verdict != Verdict.Clear)
                    b = a + 1 + (b - a - 1) / 2;
                kept.Add(path[b]);
                a = b;
            }
            return [.. kept];
        }

        private static bool WithinDeviation(IReadOnlyList<Vector3> path, int a, int j)
        {
            var from = Flat(path[a]);
            var to = Flat(path[j]);
            for (var k = a + 1; k < j; k++)
                if (FollowerSimulation.DistanceToSegment(Flat(path[k]), from, to) > MaxDeviation)
                    return false;
            return true;
        }

        private static Vector3 Flat(Vector3 v) => v with { Y = 0 };
        private static float Horizontal(Vector3 a, Vector3 b) => Vector3.Distance(Flat(a), Flat(b));
    }
}
