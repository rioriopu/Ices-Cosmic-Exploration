# SmoothNav の写し

移動の角を曲線に整える部品 SmoothNav(別リポジトリ)の写し。原本のコミットは `4548808`。

- ICE は宇宙探査の地上の移動だけで使う。使うのは経路だけを整える `RoutePlanner` と、vnavmesh の点の照会で作った歩ける面の窓口 `MeshPointGround`。
  つなぎは `Scheduler/Tasks/SmoothNavDriver.cs`(ゲームに触れない部品)と `Scheduler/Tasks/SmoothNavBridge.cs`(ICE につなぐ層)を見る。
- **ここのファイルは直接直さない。** 原本で直してから `python -X utf8 tools/sync_smoothnav.py` で写し直す
  (写すときに、ICE が暗黙に使わない using を足し、1 行目に原本を書く)。`--verify` で写しと原本が一致するかを確かめられる。

| 写し | 原本 | 原本の SHA256(先頭16文字・改行は LF) |
|---|---|---|
| Core/Collision.cs | SmoothNav.Core/Collision.cs | ba74bcab375d3c06 |
| Core/CurveBuilder.cs | SmoothNav.Core/CurveBuilder.cs | b181d1687eea0d64 |
| Core/FollowerSimulation.cs | SmoothNav.Core/FollowerSimulation.cs | d756c4e9b097282d |
| Core/GroundSurface.cs | SmoothNav.Core/GroundSurface.cs | 66fcc1fc93c139a4 |
| Core/MeshPointGround.cs | SmoothNav.Core/MeshPointGround.cs | f5ccdee396a65e16 |
| Core/Metrics.cs | SmoothNav.Core/Metrics.cs | fe3ae64035b2c68d |
| Core/MovementSession.cs | SmoothNav.Core/MovementSession.cs | 559b0cac1830ee5f |
| Core/PathMarker.cs | SmoothNav.Core/PathMarker.cs | 1bd7a394bf5b9e3b |
| Core/PathRefiner.cs | SmoothNav.Core/PathRefiner.cs | 95f869e30daa0f82 |
| Core/RoutePlanner.cs | SmoothNav.Core/RoutePlanner.cs | 846d51f073c633cf |
| Core/WindowMetrics.cs | SmoothNav.Core/WindowMetrics.cs | d9d4fd5dedf2d547 |
