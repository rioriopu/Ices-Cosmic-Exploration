using System.Collections.Generic;

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
    public bool UseBoards { get; set; } = true; // ボード(乗り口へ歩くと発進する連絡路)を移動に使う
    public float HubReturn_Distance { get; set; } = 75f;
    public bool UseRedAlertNpc { get; set; } = false;

    public class MissionCommand
    {
        public required string command { get; set; }
        public int Delay { get; set; } = 0;
    }

    // These are dumb options that do absolutely nothing for gameplay
    // And some of these are just for memes
    public bool CrazyTaxiArrow { get; set; } = false;
    public bool PlaceboCheckbox { get; set; } = false;

    // 採掘士マスター(1621)専用採取ループの有効化(Dev Favorites)。既定ON(常時オン)。
    // 旧「Notes即報告トグル表示」と「固定採取ルーチン有効化」の2チェックをこの1つに統合したもの。
    // ON時のループ: ①ミッションウィンドウを開く → ②クエスト1621を受注 → ③採取ポイントへアクセス →
    //   ④キングスイールドIIを1回使用 → ⑤トータスパインの琥珀(52057)をそのまま採取 → ⑥1ノードで報告 → ⑦最初に戻る。
    // 併せて Mission Setup の Notes 列にマスター即報告トグル(評価値500/1000)を表示する。
    public bool FixedGatherRoutineEnabled { get; set; } = true;
}
