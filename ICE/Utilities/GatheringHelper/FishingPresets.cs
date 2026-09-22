using ICE.Utilities.Cosmic_Helper;
using System;
using System.Collections.Generic;

namespace ICE.Utilities.GatheringHelper;

public static partial class GatheringUtil
{
    // 自動生成プリセットのミッションID(UIで「ミッション失敗の可能性」を警告するためのフラグ)。
    // 支給品からエサを特定し付与スキルも反映した専用プリセットだが、手動検証していない自動生成のため、
    // 失敗リスクが残る旨をミッション名左に警告表示する(エサ選択ロジックには影響しない=支給エサを使用)。
    public static readonly HashSet<uint> GenericFishingPresetMissions = new()
    {
        // 1655(C手調整)・1669(EX手調整)は手調整済み＝自動生成ではないため警告対象から除外。
        1650, 1651, 1652, 1653, 1654, 1656, 1657, 1658, 1659,
        1660, 1661, 1662, 1670, 1671, 1672, 1673, 1674, 1676,
        1677, 1698, 1699,
    };

    // マスター釣りミッション。これらは改良コスモエサ(下記)が配布されるため、エサ選択で改良エサを最優先する。
    public static readonly HashSet<uint> MasterFishingMissions = new() { 1675, 1676, 1677 };

    // ミッション別のエサ上書き(プリセットは本家のまま・装備するエサだけ変更する)。
    // GetPreferredBait/IsCurrentBaitAcceptable で最優先に判定するため、マスターの改良エサ強制より優先される。
    // 1676「在来植物魚の生態系調査」は改良コスモリーチ(52251)ではなく通常コスモリーチ(52248)を使う。
    public static readonly Dictionary<uint, uint> FishingBaitOverride = new() { [1676] = 52248 };

    // 改良コスモエサ(改良コスモカゲロウ52250/改良コスモリーチ52251/改良星赤虫52252)。マスターで配布される高品質エサ。
    // これらは「専用釣り餌」でAutoHookのSwapBaitByIdでは装備できないため、ネイティブのChangeBaitで装備する。
    public static readonly HashSet<uint> ImprovedCosmoBaits = new() { 52250, 52251, 52252 };

    public static Dictionary<uint, List<string>> FishingPreset = new();

    private static readonly Dictionary<uint, Action> FishingRegistrars = new()
    {
        [CosmicMoonRegistry.Sinus.TerritoryId] = RegisterSinus,
        [CosmicMoonRegistry.Phaenna.TerritoryId] = RegisterPhaenna,
        [CosmicMoonRegistry.Oizys.TerritoryId] = RegisterOizys,
        [CosmicMoonRegistry.Auxesia.TerritoryId] = RegisterAuxesia,
    };

    public static void RegisterPresets()
    {
        foreach (var moon in CosmicMoonRegistry.All)
        {
            if (FishingRegistrars.TryGetValue(moon.TerritoryId, out var register))
            {
                IceLogging.Verbose($"Registering Presets for: {moon.TerritoryId}");
                register();
            }
            else
                PluginLog.Warning($"[FishingPresets] No registrar for {moon.DisplayName} ({moon.TerritoryId})");
        }
        // 当方の手調整プリセットで本家プリセットを上書きする(Fishing_Custom.cs)。
        // 本家のプリセット更新を取り込んでも、このファイルの分だけは独自調整が残る設計。
        RegisterCustomOverrides();
    }

    internal static bool HasFishingRegistrar(uint territoryId) =>
        FishingRegistrars.ContainsKey(territoryId);
}
