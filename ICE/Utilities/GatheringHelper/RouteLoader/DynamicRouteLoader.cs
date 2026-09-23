using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ICE.Utilities.GatheringHelper.RouteLoader;

/// <summary>
/// 静的ルート(JSON)に無い/座標がズレている採取ノードを、実際に出現している GatheringPoint から実行時に補う。
/// 静的ルートがあればそれを基本に、フラグ周辺に出現しているノードで BaseId が未登録のものを追加する(追加のみ、削除はしない)。
/// 静的ルートが無いフラグ(新ミッション等)では、出現しているノードだけで動的にルートを合成する。
/// </summary>
public static class DynamicRouteLoader
{
    private static readonly Dictionary<uint, (DateTime at, List<NodeInfo> nodes)> _cache = new();
    private const double ThrottleSeconds = 1.0; // 毎tickのオブジェクト走査/navmesh問い合わせを避ける

    public static List<NodeInfo> GetRouteOrDynamic(uint routeId, uint territoryId, Vector2 flag)
    {
        if (_cache.TryGetValue(routeId, out var cached) && (DateTime.Now - cached.at).TotalSeconds < ThrottleSeconds)
            return cached.nodes;

        var staticNodes = GatheringRouteLoader.GetRoute(routeId)?.Nodes;
        // 読み込み済みルートのリストを直接いじらないようコピーして併合する
        var result = staticNodes != null ? new List<NodeInfo>(staticNodes) : new List<NodeInfo>();

        Vector3? center = Utils.FlagToWorld(territoryId, flag);
        if (!center.HasValue && staticNodes is { Count: > 0 })
            center = staticNodes[0].Position;
        if (center.HasValue)
            ScanAround(center.Value, result);

        _cache[routeId] = (DateTime.Now, result);
        return result;
    }

    // 中心(XZ 150m / 高さ ±15m)に出現している採取ポイントを nodes に追加する。
    // 足で近づける地面が無い、または到達可能な地面より 3.5m 超高いノードは採取不能とみなして除外する。
    private static void ScanAround(Vector3 center, List<NodeInfo> nodes)
    {
        foreach (var obj in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable))
        {
            var pos = obj.Position;
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

            nodes.Add(new NodeInfo { NodeId = obj.BaseId, Position = pos, LandZone = landZone });
        }
    }
}