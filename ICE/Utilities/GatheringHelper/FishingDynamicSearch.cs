using ICE.Ui.DebugWindowTabs;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ICE.Utilities.GatheringHelper;

// 釣り専用の動的釣り場探索モジュール。
// ギャザラーの動的ルート(採集ノードオブジェクトのスキャン)とは独立した実装。
// 釣り場座標(MoonFishingLocations)が未収録のフラグ向けに、フラグ中心(多くは水中)の周辺を
// グリッド状にサンプリングし、各点を「到達可能な歩行面(=岸)」へスナップ・重複排除したうえで、
// その岸点から全周レイキャストして水面(マテリアル0x8000)がキャスト可能な立ち位置を探す。
//
// 前回失敗(PointOnFloorで水中点が芝生へ誤スナップ)の反省を踏まえ、NearestPointReachable で
// 「到達可能な最寄りの歩行面」へ寄せることで、水中のフラグ中心からでも実際の岸点を拾えるようにしている。
public static class FishingDynamicSearch
{
    /// <summary>
    /// フラグ中心周辺を探索し、釣り可能な岸の立ち位置を返す。
    /// </summary>
    /// <param name="flagCenter">ミッションフラグのワールド座標(水中のことが多い)</param>
    /// <param name="radius">探索半径(m)</param>
    /// <param name="ray">レイキャスト判定器</param>
    /// <param name="standPosition">出力: 釣り可能な立ち位置(岸)</param>
    /// <param name="facePoint">出力: 釣り可能な水面のヒット点</param>
    public static bool TryFindStand(Vector3 flagCenter, float radius, FishingDebug ray, out Vector3 standPosition, out Vector3 facePoint)
    {
        standPosition = Vector3.Zero;
        facePoint = Vector3.Zero;
        if (ray == null || !P.Navmesh.Installed)
            return false;

        const int gridRings = 6;       // 中心からのリング数(各リングは正方形の外周)
        const int rotationSteps = 16;  // 各岸点での全周レイキャスト分割数
        float step = MathF.Max(radius / gridRings, 2f);
        float angleStep = (2f * MathF.PI) / rotationSteps;

        // スナップ後の岸点を粗く量子化(2m格子)して重複排除し、同一岸点の多重レイキャストを避ける。
        var visited = new HashSet<(int, int, int)>();

        // 中心に近いリングから外周へ走査。各リングは外周セルのみ(内側は前リングで処理済み)。
        for (int r = 0; r <= gridRings; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    if (r != 0 && Math.Max(Math.Abs(dx), Math.Abs(dz)) != r)
                        continue; // 外周セルのみ

                    var probe = new Vector3(flagCenter.X + (dx * step), flagCenter.Y, flagCenter.Z + (dz * step));
                    // 到達可能な最寄りの歩行面へスナップ(水中点も最寄りの岸へ寄る)。不可なら通常の最寄り面で代替。
                    var snapped = P.Navmesh.NearestPointReachable(probe, step * 2f, 60f)
                                  ?? P.Navmesh.NearestPoint(probe, step * 2f, 60f);
                    if (!snapped.HasValue)
                        continue;

                    var stand = snapped.Value;
                    var key = ((int)MathF.Round(stand.X / 2f), (int)MathF.Round(stand.Y / 2f), (int)MathF.Round(stand.Z / 2f));
                    if (!visited.Add(key))
                        continue; // 同じ岸点は一度だけ判定

                    for (int i = 0; i < rotationSteps; i++)
                    {
                        if (ray.IsFishableAt(stand, i * angleStep, out var hit) && hit.HasValue)
                        {
                            standPosition = stand;
                            facePoint = hit.Value;
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}
