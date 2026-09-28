using System.Collections.Generic;
using System.Numerics;

namespace ICE.ConfigFiles;

public partial class Config
{
    public bool MoonSprint { get; set; } = true;
    public uint MountId { get; set; } = 0;
    public string MountName { get; set; } = "Mount Roulette";
    public float MountRadius { get; set; } = 15.0f;
    public float DismountRadius { get; set; } = 7.0f;
    public bool UseMountOutsideMission { get; set; } = true;
    public bool UseMountInMission { get; set; } = true;
    public float LeftColumnWidth { get; set; } = 300f;
    public bool PlaySoundAlert { get; set; } = false;
    public float SoundVolume { get; set; } = 0.5f;
    public int TimeHistoryLimit { get; set; } = 100;
    public bool RemoveStellarStatus { get; set; } = false;
    public bool ShowSPM { get; set; } = false;
    public bool StartUponEnterMoon { get; set; } = false;
    public bool PersonalReturnSpot { get; set; } = false;
    public bool ClosestNodeSelection { get; set; } = false;
    public bool AvoidStellarReturn { get; set; } = false;
    public bool AvoidStellarReturnExceptHub { get; set; } = false;
    public bool RandomizeWaypoints { get; set; } = false;
    public float RandomizeWaypointsRadius { get; set; } = 1.0f;
    public bool RandomizeWaypointsDebug { get; set; } = false;
    public Dictionary<uint, Vector3> CrafterLocations { get; set; } = new();
    public List<MissionCommand> PostMissionCommands { get; set; } = new();

    public bool UseHubReturn { get; set; } = true;
    public bool UseAethernet { get; set; } = true;
    public bool UseBoards { get; set; } = true; // 連絡ボード(乗り口へ歩くと自動発進する)を移動に使う
    // レベリング中(Leveling モードのミッション後/レベリング装備の購入後)に最強装備を自動で行う。
    // Stylist があればそれを、無ければゲームの「おすすめ装備」を使う。「レベリング装備を購入」ボタンで自動的に ON になる。
    public bool LevelingGear_AutoEquipBest { get; set; } = true;
    // レベリング中(Leveling モード、およびレリックモードからの一時レベリング中)の製作で、ICE のソルバー設定を無視して
    // Artisan の「Progress Only Solver」を使うか。本家は常にこの動作だったが、設定した Raphael 等が効かず品質が付かないため既定 OFF(設定どおり)。
    public bool Leveling_UseProgressOnlySolver { get; set; } = false;
    // レリックモードで Lv90 のジョブは、コスモデータ(金賞)を狙わずレベリング(ブロンズ)で Lv91 まで上げる。
    // Lv90 の製作難易度は Lv90 用の表(推奨作業精度 2805)に跳ね、Lv91 からは新しい装備が着けられるため、
    // Lv90 は通過点として扱い、Lv91 で最強装備してから B クラスに挑む(ユーザー方針 2026-09-26)。
    public bool Relic_BronzeThroughLv90 { get; set; } = true;
    // 「レベリング装備を売却」で対象にする種類。確認ダイアログのチェックで切り替え、次回も同じ選択を使う(ユーザー要望 2026-09-28)。
    public bool SellGear_Armor { get; set; } = true;     // 防具(頭・胴・手・脚・足)
    public bool SellGear_MainHand { get; set; } = true;  // 主道具
    public bool SellGear_OffHand { get; set; } = true;   // 副道具
    public bool SellGear_Accessory { get; set; } = true; // アクセサリ(耳・首・腕輪・指)
    // UI の表示言語(Auto=クライアント言語に従う)。日本語なら同梱の CSV 辞書でプラグイン自身が翻訳する
    public global::ICE.Localization.UiLanguage UiLanguage { get; set; } = global::ICE.Localization.UiLanguage.Auto;
    public float HubReturn_Distance { get; set; } = 75f;
    public bool UseRedAlertNpc { get; set; } = false;
    public bool HideRewardWindow { get; set; } = true;

    // 指定ノード採取: 記録した採取ポイントだけを回る。ルート上のノードが荒れている場所や、
    // 特定のノードに張り付きたい場合に使う。記録した惑星に居るときだけ有効になる。
    public bool DesignatedNodeEnabled { get; set; } = false;
    public Vector3 DesignatedNodePos { get; set; } = Vector3.Zero;
    public uint DesignatedNodeBaseId { get; set; } = 0;
    public uint DesignatedNodeTerritory { get; set; } = 0;
    public bool FakeIncreaseFisher { get; set; } = false;

    public class MissionCommand
    {
        public required string command { get; set; }
        public int Delay { get; set; } = 0;
    }

    // These are dumb options that do absolutely nothing for gameplay
    // And some of these are just for memes
    public bool CrazyTaxiArrow { get; set; } = false;
    public bool PlaceboCheckbox { get; set; } = false;
}
