using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    // 1676「在来植物魚の生態系調査」は改良コスモリーチ(52251)ではなく通常コスモリーチ(52248)を使う(ユーザー指定)。
    // 1675「植物魚の多様性調査」は改良エサ列挙順に依存させず改良コスモカゲロウ(52250)固定にする(ユーザー指定)。
    public static readonly Dictionary<uint, uint> FishingBaitOverride = new() { [1676] = 52248, [1675] = 52250 };

    // 改良コスモエサ(改良コスモカゲロウ52250/改良コスモリーチ52251/改良星赤虫52252)。マスターで配布される高品質エサ。
    public static readonly HashSet<uint> ImprovedCosmoBaits = new() { 52250, 52251, 52252 };

    public static Dictionary<uint, List<string>> FishingPreset = new();

    /// <summary>
    /// 釣りプリセットを登録する。
    /// 本家(upstream)の「月別レジストラ」構造(Fishing_Sinus/Phaenna/Oizys/Aux)を採用しつつ、
    /// 本家がディスパッチに使う大規模インフラ刷新 CosmicMoonRegistry(33ファイル波及)は持ち込まず、
    /// 各月の Register を直接呼ぶ簡略版にしている。
    /// 最後に RegisterCustomOverrides()(Fishing_Custom.cs)で当方の手調整プリセットを上書きする。
    /// </summary>
    public static void RegisterPresets()
    {
        FishingPreset.Clear();

        // 本家プリセット(各月)
        RegisterSinus();
        RegisterPhaenna();
        RegisterOizys();
        RegisterAuxesia();

        // 当方の手調整プリセットで上書き(Aクラス一部・マスター・C/EX手調整)
        RegisterCustomOverrides();
    }
}
