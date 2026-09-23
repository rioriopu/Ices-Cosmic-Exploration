using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ICE.Utilities.GatheringHelper.RouteLoader;

/// <summary>
/// 静的ルート(JSON)に無い/座標がズレている採取ノードを、実際に出現している GatheringPoint から実行時に補う。
/// 静的ルートがあればそれを基本に、フラグ周辺に出現しているノードで BaseId が未登録のものを追加する(追加のみ、削除はしない)。
/// 一度見つけた動的ノードは、採取後に光らなくなっても同じルートの間は保持し、ルートの並び(index)が毎秒変わらないようにする。
/// 静的ルートが無いフラグ(新ミッション等)では、出現しているノードだけで動的にルートを合成する。
/// </summary>
public static class DynamicRouteLoader
{
    private const double ThrottleSeconds = 1.0; // 毎tickのオブジェクト走査/navmesh問い合わせを避ける

    private static string _activeKey = "";                                    // 現在合成中のルート(routeId+territory+flag)
    private static readonly Dictionary<uint, NodeInfo> _dynamicSeen = new();  // このルートで見つけた動的ノード(BaseId → ノード、発見順)
    private static List<NodeInfo> _last;                                      // 直近の合成結果
    private static DateTime _lastAt = DateTime.MinValue;

    public static List<NodeInfo> GetRouteOrDynamic(uint routeId, uint territoryId, Vector2 flag)
    {
        // ルートが変わったら動的ノードの記憶を捨てる。Gather_MapKey が 0 の未登録ミッション同士を区別するため flag も鍵に含める。
        string key = $"{routeId}:{territoryId}:{flag.X:F1}:{flag.Y:F1}";
        if (key != _activeKey)
        {
            _activeKey = key;
            _dynamicSeen.Clear();
            _last = null;
        }
        if (_last != null && (DateTime.Now - _lastAt).TotalSeconds < ThrottleSeconds)
            return _last;

        var staticNodes = GatheringRouteLoader.GetRoute(routeId)?.Nodes;
        // 読み込み済みルートのリストを直接いじらないようコピーして併合する
        var result = staticNodes != null ? new List<NodeInfo>(staticNodes) : new List<NodeInfo>();

        Vector3? center = Utils.FlagToWorld(territoryId, flag);
        if (!center.HasValue && staticNodes is { Count: > 0 })
            center = staticNodes[0].Position;
        if (center.HasValue)
            ScanAround(center.Value, result);

        // 見つけた動的ノードを発見順に足す(今は光っていなくても保持し、index のズレを防ぐ)
        foreach (var seen in _dynamicSeen.Values)
            if (!result.Any(n => n.NodeId == seen.NodeId))
                result.Add(seen);

        _last = result;
        _lastAt = DateTime.Now;
        return result;
    }

    /// <summary>ルート外で見つけた実ノードを、このルートの動的ノードとして末尾に追加する(採取で詰まった時の向け直しに使う)。</summary>
    public static void AddDynamicNode(NodeInfo node)
    {
        if (node == null || node.NodeId == 0)
            return;
        _dynamicSeen[node.NodeId] = node;
        _last = null; // 次回の呼び出しで再合成させる
    }

    // 中心(XZ 150m / 高さ ±15m)に出現している採取ポイントのうち、ルート未登録のものを動的ノードとして記憶する。
    // 足で近づける地面が無い、または到達可能な地面より 3.5m 超高いノードは採取不能とみなして除外する。
    private static void ScanAround(Vector3 center, List<NodeInfo> nodes)
    {
        foreach (var obj in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable))
        {
            var pos = obj.Position;
            if (_dynamicSeen.ContainsKey(obj.BaseId)) continue;
            if (nodes.Any(n => n.NodeId == obj.BaseId)) continue;

            var planar = new Vector2(pos.X - center.X, pos.Z - center.Z);
            if (planar.Length() > 150f) continue;
            if (Math.Abs(pos.Y - center.Y) > 15f) continue;

            var landZone = pos;
            try
            {
                if (P.Navmesh.Installed)
                {
                    var reachable = P.Navmesh.NearestPointReachable(pos, 4f, 5f);
                    if (!reachable.HasValue) continue;
                    if (pos.Y - reachable.Value.Y > 3.5f) continue;
                    landZone = reachable.Value;
                }
            }
            catch { }

            _dynamicSeen[obj.BaseId] = new NodeInfo { NodeId = obj.BaseId, Position = pos, LandZone = landZone };
        }
    }
}
