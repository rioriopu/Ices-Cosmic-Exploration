using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.GameHelpers;
using ICE.Resources.GatheringRoutes;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ICE.Utilities.GatheringHelper;

public static class GatheringRouteLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Cache for loaded routes
    private static Dictionary<uint, Dictionary<Vector2, List<GathNodeInfo>>>? _cachedRoutes;

    // 動的ルート(yaml未作成ゾーン用): 実機の採集ノードから生成したルートを (zone, flagX, flagY) でキャッシュ
    private static readonly Dictionary<(uint zone, float fx, float fy), List<GathNodeInfo>> _dynamicRoutes = new();

    public static Dictionary<uint, Dictionary<Vector2, List<GathNodeInfo>>> LoadAllRoutes()
    {
        if (_cachedRoutes != null)
            return _cachedRoutes;

        _cachedRoutes = new Dictionary<uint, Dictionary<Vector2, List<GathNodeInfo>>>();
        var assembly = Assembly.GetExecutingAssembly();

        // Get all embedded .yaml files
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.Contains("GatheringRoutes") && r.EndsWith(".yaml"))
            .ToList();

        PluginLog.Information($"Found {resourceNames.Count} gathering route resources");

        foreach (var resourceName in resourceNames)
        {
            try
            {
                var route = LoadRouteFromResource(resourceName);

                if (!_cachedRoutes.ContainsKey(route.ZoneId))
                    _cachedRoutes[route.ZoneId] = new Dictionary<Vector2, List<GathNodeInfo>>();

                _cachedRoutes[route.ZoneId][route.Flag] = route.Nodes;

                PluginLog.Debug($"Loaded route: Zone {route.ZoneId}, Flag ({route.Flag.X}, {route.Flag.Y}), Job {route.Job}");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to load route from {resourceName}: {ex.Message}");
            }
        }

        // 埋め込みルートに加えて、ディスク上のキャプチャ済みルート(/ice capture で保存)も読み込む。
        // これにより再ビルド無しで、その場で保存した静的ルートが即使われる(埋め込みと同じflagがあれば上書き)。
        LoadCapturedRoutesFromDisk();

        PluginLog.Information($"Loaded {_cachedRoutes.Sum(x => x.Value.Count)} gathering routes across {_cachedRoutes.Count} zones");

        return _cachedRoutes;
    }

    // /ice capture で保存した動的キャプチャルートの保存フォルダ
    public static string GetCapturedRoutesPath()
        => Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "CapturedRoutes");

    private static void LoadCapturedRoutesFromDisk()
    {
        try
        {
            var dir = GetCapturedRoutesPath();
            if (!Directory.Exists(dir))
                return;

            foreach (var file in Directory.GetFiles(dir, "*.yaml"))
            {
                try
                {
                    var yaml = File.ReadAllText(file);
                    var route = Deserializer.Deserialize<GatheringRouteFile>(yaml);
                    if (route == null || route.Nodes == null || route.Nodes.Count == 0)
                        continue;

                    if (!_cachedRoutes.ContainsKey(route.ZoneId))
                        _cachedRoutes[route.ZoneId] = new Dictionary<Vector2, List<GathNodeInfo>>();

                    _cachedRoutes[route.ZoneId][route.Flag] = route.Nodes; // 埋め込みより優先(上書き)
                    PluginLog.Information($"[CapturedRoute] Loaded {route.Nodes.Count} nodes for zone {route.ZoneId} flag({route.Flag.X},{route.Flag.Y}) from {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"[CapturedRoute] Failed to load {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error($"[CapturedRoute] Failed to scan captured routes: {ex.Message}");
        }
    }

    /// <summary>
    /// 現在地周辺の採取可能(光っている)ノードを静的yamlルートとして保存する(/ice capture)。
    /// 現在の採取ミッションのフラグをキーにするので、採取ミッション受注中・採取エリア内で実行すること。
    /// 保存後は即キャッシュに反映され、以後そのフラグのミッションは安定した静的ルートで動く。
    /// </summary>
    public static string CaptureCurrentRoute()
    {
        if (!Player.Available)
            return "プレイヤー情報が取得できません";

        uint zone = Player.Territory.RowId;

        if (CosmicHelper.CurrentLunarMission == 0)
            return "採取ミッションを受注した状態で実行してください(ルートのフラグ特定のため)";

        var missionInfo = CosmicHelper.CurrentMissionInfo;
        Vector2 flag = missionInfo.MapPosition;

        uint jobId = (uint)Player.Job;
        string jobStr = jobId switch { 16 => "MIN", 17 => "BTN", 18 => "FSH", _ => "MIN" };

        var playerPos = Player.Position;
        var nodes = new List<GathNodeInfo>();

        // 光っている(採取可能)GatheringPointを、プレイヤー平面150m以内で収集。到達可能点が無い/高所(登れない)は除外。
        foreach (var obj in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable))
        {
            var pos = obj.Position;
            var planar = new Vector2(pos.X - playerPos.X, pos.Z - playerPos.Z);
            if (planar.Length() > 150f)
                continue;

            var landZone = pos;
            try
            {
                if (P.Navmesh.Installed)
                {
                    var reachable = P.Navmesh.NearestPointReachable(pos, 4f, 5f);
                    if (!reachable.HasValue)
                        continue; // 足で近づけない=採取不能ノードなので除外
                    if (pos.Y - reachable.Value.Y > 3.5f)
                        continue; // 到達可能地面より3.5m超高い=登れないので除外
                    landZone = reachable.Value;
                }
            }
            catch { }

            // 同一ノードIDの重複は避ける
            if (nodes.Any(n => n.NodeId == obj.BaseId))
                continue;

            nodes.Add(new GathNodeInfo
            {
                NodeId = obj.BaseId,
                Position = pos,
                LandZone = landZone,
            });
        }

        if (nodes.Count == 0)
            return "周辺(150m)に採取可能なノードが見つかりません。採取エリア内・ノードが光っている状態で実行してください";

        var routeFile = new GatheringRouteFile
        {
            ZoneId = zone,
            ZoneName = GetZoneName(zone),
            Job = jobStr,
            Flag = flag,
            Author = string.IsNullOrWhiteSpace(C.AuthorName) ? "Captured" : C.AuthorName,
            DateModified = DateTime.UtcNow,
            Nodes = nodes,
        };

        try
        {
            var dir = GetCapturedRoutesPath();
            Directory.CreateDirectory(dir);
            string fileName = $"{zone}_{jobStr}_Flag_{(int)flag.X}_{(int)flag.Y}.yaml";
            string fullPath = Path.Combine(dir, fileName);
            File.WriteAllText(fullPath, Serializer.Serialize(routeFile));

            // 即時反映: キャッシュへ登録(以後GetRouteOrDynamicが静的ルートを返す)
            LoadAllRoutes();
            if (!_cachedRoutes.ContainsKey(zone))
                _cachedRoutes[zone] = new Dictionary<Vector2, List<GathNodeInfo>>();
            _cachedRoutes[zone][flag] = nodes;

            return $"{nodes.Count}ノードを保存しました → {fileName} (zone {zone}, flag {(int)flag.X},{(int)flag.Y}, {jobStr})。以後このミッションは静的ルートで動きます";
        }
        catch (Exception ex)
        {
            return $"保存に失敗: {ex.Message}";
        }
    }

    private static GatheringRouteFile LoadRouteFromResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Resource not found: {resourceName}");

        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();

        return Deserializer.Deserialize<GatheringRouteFile>(yaml)
            ?? throw new InvalidDataException($"Failed to deserialize {resourceName}");
    }

    // Get routes for a specific zone and flag
    public static List<GathNodeInfo>? GetRoute(uint zoneId, Vector2 flag)
    {
        var routes = LoadAllRoutes();

        if (routes.TryGetValue(zoneId, out var zoneRoutes))
        {
            if (zoneRoutes.TryGetValue(flag, out var nodes))
                return nodes;
        }

        return null;
    }

    /// <summary>
    /// 静的(yaml)ルートが在ればそれを返し、無ければ実機の採集ノードから動的ルートを生成して返す。
    /// Auxesia等、手書きyamlの無いゾーンの採集ミッションを動かすためのフォールバック。
    /// </summary>
    public static List<GathNodeInfo> GetRouteOrDynamic(uint zoneId, Vector2 flag)
    {
        var route = GetRoute(zoneId, flag);

        // 実機で今まさに出現(IsTargetable=光っている)しているノードを取得し、authoredルートへ併合する。
        // authoredルートの座標が実際のノード出現位置とズレている場合でも、確実に「実際に出現しているノード」へ
        // 向かわせるための措置(ユーザー要望)。authoredに無いBaseIdのライブノードを追加する。
        // authoredが無いゾーン(Auxesia等)は従来どおり動的ルートのみで動く。
        var live = BuildOrUpdateDynamicRoute(zoneId, flag);

        if (route == null || route.Count == 0)
            return live;
        if (live.Count == 0)
            return route;

        // authored(調整済みland_zone/扇パラメータ)を優先しつつ、authoredに無い実出現ノードを末尾に追加。
        // 出現していないauthoredノードは選択側(IsTargetable判定)で自然に除外されるため無害。
        var merged = new List<GathNodeInfo>(route);
        foreach (var n in live)
        {
            if (!merged.Any(m => m.NodeId == n.NodeId))
                merged.Add(n);
        }
        return merged;
    }

    /// <summary>
    /// 現在ロードされている採集ノード(ObjectKind.GatheringPoint)を走査し、ミッションフラグ付近(120m以内)の
    /// ノードを動的ルートに追記する。プレイヤーがフラグ地点へ近づくにつれノードがストリームインし、ルートが充実していく。
    /// </summary>
    private static List<GathNodeInfo> BuildOrUpdateDynamicRoute(uint zoneId, Vector2 flag)
    {
        var key = (zoneId, flag.X, flag.Y);
        bool existed = _dynamicRoutes.TryGetValue(key, out var nodes);
        if (!existed)
        {
            nodes = new List<GathNodeInfo>();
            _dynamicRoutes[key] = nodes;
        }

        // GetRouteOrDynamicの併合により、authoredルートのある全ゾーンでも毎tick呼ばれるようになった。
        // navmeshクエリ(NearestPointReachable)の多発によるフレーム負荷を避けるため、直近にスキャン済みなら
        // 1秒間は再スキャンせず既存リストを返す。選択側が毎回IsTargetableを再検証するので、わずかな鮮度差は無害。
        if (existed && nodes.Count > 0 && !EzThrottler.Throttle($"DynRebuild_{zoneId}_{flag.X}_{flag.Y}", 1000))
            return nodes;

        // 毎回アクティブなノードで作り直す。枯渇/非アクティブ(光っていない)ノードを残すと、
        // 採取できない場所へ向かって右往左往するため、現在 IsTargetable なノードだけを保持する。
        nodes.Clear();

        var flagWorld = FlagToWorld(zoneId, flag);
        int loaded = 0, added = 0;
        // IsTargetable=今まさに採取可能(光っている)ノードのみ対象
        foreach (var obj in Svc.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable))
        {
            var pos = obj.Position;

            // ミッションフラグ(=対象ジョブの採集エリア)付近(平面150m)のノードだけ収集する。
            // プレイヤー基準にすると、目的地へ向かう道中の別ジョブ(園芸)ノードを拾ってしまい、
            // 採掘師が遠くの園芸ノードへ逸れて右往左往する(実機報告)。Y差はsnap推定で誤差が出るため平面距離で判定。
            if (flagWorld.HasValue)
            {
                var planar = new Vector2(pos.X - flagWorld.Value.X, pos.Z - flagWorld.Value.Z);
                if (planar.Length() > 150f)
                    continue;
            }
            else if (Player.DistanceTo(pos) > 150f)
            {
                // フラグ変換失敗時のみプレイヤー近傍にフォールバック
                continue;
            }

            // 異常な高さ(飛行必須/別の高度レイヤー)のノードは除外。歩行可能面から大きく外れた高さへ
            // ジャンプして無理に向かう挙動を防ぐ。基準はフラグ高度(無ければプレイヤー高度)。
            float refY = flagWorld.HasValue ? flagWorld.Value.Y : (Player.Available ? Player.Position.Y : pos.Y);
            if (Math.Abs(pos.Y - refY) > 15f)
                continue;

            loaded++;

            // 立ち位置(LandZone)はナビメッシュ上の到達可能点に補正。
            // さらに「足で到達可能な地面がノードより大きく下にある(=登れない高所ノード)」や
            // 「到達可能点が全く無いノード」は採取不能(キノコの傘の上等)なのでルートから除外する。
            // これをしないと、採取できない高所ノードへ向かってジャンプを繰り返しスタックする(実機報告)。
            var landZone = pos;
            try
            {
                if (P.Navmesh.Installed)
                {
                    // XZ広め(4f)・下方向も拾えるよう縦5fで最寄り到達可能点を探す
                    var reachable = P.Navmesh.NearestPointReachable(pos, 4f, 5f);
                    if (!reachable.HasValue)
                        continue; // 足で近づける地面が無い→採取不能ノード、スキップ
                    if (pos.Y - reachable.Value.Y > 3.5f)
                        continue; // ノードが到達可能地面より3.5m超高い→登れない/採取範囲外、スキップ
                    landZone = reachable.Value;
                }
            }
            catch
            {
                // ナビメッシュ未準備等は無視してノード座標をそのまま使う(スキップはしない)
            }

            nodes.Add(new GathNodeInfo
            {
                NodeId = obj.BaseId,
                Position = pos,
                LandZone = landZone,
            });
            added++;
        }

        if (EzThrottler.Throttle("DynamicRouteScan", 3000))
        {
            var fw = flagWorld.HasValue ? $"({flagWorld.Value.X:F1},{flagWorld.Value.Y:F1},{flagWorld.Value.Z:F1})" : "(none)";
            PluginLog.Information($"[DynamicRoute] zone {zoneId} flag({flag.X},{flag.Y})→world{fw}: フラグ150m内のアクティブ(光っている)GatheringPoint {nodes.Count}件");
        }

        return nodes;
    }

    /// <summary>
    /// ミッションのマップフラグ座標をワールド3D座標へ変換する。ナビメッシュが在れば床/最寄り点へ補正する。
    /// </summary>
    public static Vector3? FlagToWorld(uint territoryId, Vector2 flag)
    {
        try
        {
            // MapPositionは ICEDictornaryCreation で「マップマーカー生値 - 1024」として格納されており、
            // コスミックマップ(SizeFactor=100)ではワールド座標(X,Z)とほぼ1:1で一致する。
            // 実測: Oizys flag(-340,870)↔world(-354,847) / flag(220,60)↔world(213,68)。
            // 旧実装の Utils.MapToWorld(1〜42のマップ座標前提) を通すと座標系が違い巨大値(例: -12574)になりナビ不能だった。
            var map = ExcelHelper.TerritorySheet.GetRow(territoryId).Map.Value;
            float scalar = map.SizeFactor / 100f;
            if (scalar <= 0f) scalar = 1f;
            // Yはプレイヤーの現在高度を基準にする。0にするとNearestPointが「下の低層メッシュ」へ誤スナップし、
            // 的外れの低い場所へ向かってスタックする(実機報告)。同高度の歩行可能面に寄せるのが狙い。
            float baseY = Player.Available ? Player.Position.Y : 0f;
            Vector3 world = new(flag.X / scalar, baseY, flag.Y / scalar);

            if (P.Navmesh.Installed)
            {
                var floor = P.Navmesh.PointOnFloor(world, false, 50f);
                if (floor.HasValue)
                    return floor.Value;

                // フラグがメッシュ外(縁の外)の場合、XZは広め(±200)・Yは狭め(±60)で最寄りメッシュへスナップ。
                // Yを広げると下層メッシュを拾うため、プレイヤー高度付近の層に限定する。
                var nearest = P.Navmesh.NearestPoint(world, 200f, 60f);
                if (nearest.HasValue)
                    return nearest.Value;
            }

            // 床にも最寄りメッシュ(200m)にも乗らなかった = 座標変換が破綻したゴミの可能性(例: -19324,0,8176)。
            // プレイヤーから異常に遠い(平面1000m超)結果は移動させると永久にpathfind失敗してスタックするため、
            // nullを返して呼び出し側にミッションをスキップ/再選択させる(ゴミ座標への走り込み/無限失敗を防止)。
            if (Player.Available)
            {
                var pp = Player.Position;
                float planar = new Vector2(world.X - pp.X, world.Z - pp.Z).Length();
                if (planar > 1000f)
                {
                    PluginLog.Error($"[FlagToWorld] 変換結果がゴミ座標(プレイヤーから{planar:F0}m): {world} (flag {flag.X},{flag.Y} / scalar {scalar} / SizeFactor {map.SizeFactor})。nullを返してスキップさせます。");
                    return null;
                }
            }

            return world;
        }
        catch
        {
            return null;
        }
    }

    // 動的ルートのクリア(ゾーン移動時などに呼ぶ)
    public static void ClearDynamicRoutes()
    {
        _dynamicRoutes.Clear();
    }

    // Clear cache if needed (for testing/reloading)
    public static void ClearCache()
    {
        _cachedRoutes = null;
    }

    private static readonly ISerializer Serializer = new SerializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

    public static void ExportRoute(uint zoneId, Vector2 flag, string? exportPath = null)
    {
        var routes = LoadAllRoutes();

        if (!routes.TryGetValue(zoneId, out var zoneRoutes))
            throw new InvalidOperationException($"Zone {zoneId} not found");

        if (!zoneRoutes.TryGetValue(flag, out var nodes))
            throw new InvalidOperationException($"Route at flag ({flag.X}, {flag.Y}) not found");

        if (nodes == null || nodes.Count == 0)
            throw new InvalidOperationException("Route has no nodes to export");

        var (zoneName, job) = GetRouteMetadata(zoneId, flag);

        // Ensure we have valid values
        if (string.IsNullOrWhiteSpace(zoneName))
            zoneName = "Unknown";
        if (string.IsNullOrWhiteSpace(job))
            job = "BTN";

        var routeFile = new GatheringRouteFile
        {
            ZoneId = zoneId,
            ZoneName = zoneName,
            Job = job,
            Flag = flag,
            Author = string.IsNullOrWhiteSpace(C.AuthorName) ? "Ice" : C.AuthorName,
            DateModified = DateTime.UtcNow,
            Nodes = nodes
        };

        // Determine base output path
        string basePath;
        if (!string.IsNullOrEmpty(exportPath))
        {
            basePath = exportPath;
        }
        else if (!string.IsNullOrEmpty(C.CustomRoutePath))
        {
            basePath = C.CustomRoutePath;
        }
        else
        {
            basePath = GetDefaultExportPath();
        }

        // Create zone subdirectory: "ZoneId_ZoneName"
        string zoneFolderName = SanitizeFolderName($"{zoneId}_{zoneName}");
        string outputPath = Path.Combine(basePath, zoneFolderName);

        string fileName = $"{job}_Flag_{(int)flag.X}_{(int)flag.Y}.yaml";
        string fullPath = Path.Combine(outputPath, fileName);

        try
        {
            Directory.CreateDirectory(outputPath);

            string yaml = Serializer.Serialize(routeFile);
            File.WriteAllText(fullPath, yaml);

            PluginLog.Information($"Exported route to {fullPath}");
        }
        catch (Exception ex)
        {
            PluginLog.Error($"Failed to write file: {ex.Message}");
            throw new InvalidOperationException($"Failed to write file to {fullPath}: {ex.Message}", ex);
        }
    }
    private static string SanitizeFolderName(string folderName)
    {
        // Remove invalid characters from folder name
        char[] invalidChars = Path.GetInvalidFileNameChars();
        string sanitized = string.Join("_", folderName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Trim();
    }

    public static void ExportAllRoutes(string? exportPath = null)
    {
        var routes = LoadAllRoutes();
        string outputPath = exportPath ?? C.CustomRoutePath ?? GetDefaultExportPath();

        int exportedCount = 0;

        foreach (var (zoneId, zoneRoutes) in routes)
        {
            foreach (var (flag, nodes) in zoneRoutes)
            {
                try
                {
                    ExportRoute(zoneId, flag, outputPath);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    PluginLog.Error($"Failed to export route for zone {zoneId}, flag ({flag.X}, {flag.Y}): {ex.Message}");
                }
            }
        }

        PluginLog.Information($"Exported {exportedCount} routes to {outputPath}");
    }

    private static string GetDefaultExportPath()
    {
        // Get Dalamud config directory
        var configDir = Svc.PluginInterface.ConfigDirectory.FullName;
        return Path.Combine(configDir, "ExportedRoutes");
    }

    private static (string zoneName, string job) GetRouteMetadata(uint zoneId, Vector2 flag)
    {
        // We need to parse the original resource to get the metadata
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.Contains("GatheringRoutes") && r.EndsWith(".yaml"))
            .ToList();

        foreach (var resourceName in resourceNames)
        {
            try
            {
                var route = LoadRouteFromResource(resourceName);
                if (route.ZoneId == zoneId && route.Flag == flag)
                {
                    return (route.ZoneName, route.Job);
                }
            }
            catch
            {
                // Skip invalid resources
            }
        }

        return ("Unknown", "BTN"); // Fallback
    }

    public static List<string> CreateMissingRoutes(string? exportPath = null)
    {
        var routes = LoadAllRoutes();
        var createdRoutes = new List<string>();

        // Get base export path
        string basePath = exportPath ?? C.CustomRoutePath ?? GetDefaultExportPath();

        // Iterate through all missions in the sheet
        foreach (var (missionId, missionInfo) in CosmicHelper.SheetMissionDict)
        {
            // Check if job is MIN (16) or BTN (17)
            if (!missionInfo.Jobs.Contains(16) && !missionInfo.Jobs.Contains(17))
                continue;

            var territoryId = missionInfo.TerritoryId;
            var mapFlag = missionInfo.MapPosition;

            // Determine job type
            string jobType = missionInfo.Jobs.Contains(17) ? "BTN" : "MIN";
            uint jobId = missionInfo.Jobs.Contains(17) ? 17u : 16u;

            // Check if this route already exists
            bool routeExists = routes.ContainsKey(territoryId) &&
                              routes[territoryId].ContainsKey(mapFlag);

            if (routeExists)
            {
                PluginLog.Debug($"Route already exists: Zone {territoryId}, Flag ({mapFlag.X}, {mapFlag.Y}), Job {jobType}");
                continue;
            }

            // Route doesn't exist, create it
            try
            {
                // Get zone name from territory lookup or use fallback
                string zoneName = GetZoneName(territoryId);

                var newRoute = new GatheringRouteFile
                {
                    ZoneId = territoryId,
                    ZoneName = zoneName,
                    Job = jobType,
                    Flag = mapFlag,
                    Author = string.IsNullOrWhiteSpace(C.AuthorName) ? "Ice" : C.AuthorName,
                    DateModified = DateTime.UtcNow,
                    Nodes = new List<GathNodeInfo>() // Empty list to be populated later
                };

                // Create zone subdirectory
                string zoneFolderName = SanitizeFolderName($"{territoryId}_{zoneName}");
                string outputPath = Path.Combine(basePath, zoneFolderName);

                string fileName = $"{jobType}_Flag_{(int)mapFlag.X}_{(int)mapFlag.Y}.yaml";
                string fullPath = Path.Combine(outputPath, fileName);

                // Create directory if it doesn't exist
                Directory.CreateDirectory(outputPath);

                // Serialize and write file
                string yaml = Serializer.Serialize(newRoute);
                File.WriteAllText(fullPath, yaml);

                createdRoutes.Add(fullPath);
                PluginLog.Information($"Created new route: {fullPath}");

                // Add to cache so it's available immediately
                if (!routes.ContainsKey(territoryId))
                    routes[territoryId] = new Dictionary<Vector2, List<GathNodeInfo>>();

                routes[territoryId][mapFlag] = newRoute.Nodes;
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed to create route for Zone {territoryId}, Flag ({mapFlag.X}, {mapFlag.Y}): {ex.Message}");
            }
        }

        // Clear cache to force reload with new files on next load
        if (createdRoutes.Count > 0)
        {
            ClearCache();
            PluginLog.Information($"Created {createdRoutes.Count} new gathering routes");
        }
        else
        {
            PluginLog.Information("No missing routes found - all routes already exist");
        }

        return createdRoutes;
    }

    private static string GetZoneName(uint territoryId)
    {
        // You can expand this with a proper territory lookup if you have access to game sheets
        // For now, using your existing mappings
        return territoryId switch
        {
            1237 => "Sinus Ardorum",
            1291 => "Phaenna",
            1310 => "Oizys",
            1319 => "Auxesia",
            _ => $"Zone_{territoryId}" // Fallback for unknown zones
        };
    }
}