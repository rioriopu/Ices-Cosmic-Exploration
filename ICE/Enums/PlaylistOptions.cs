namespace ICE.Enums
{
    [Flags]
    public enum PlaylistOptions
    {
        None = 0,
        SinusMax = 1,
        PhaennaMax = 2,
        OizysMax = 3,
        AuxesiaMax = 4,
        SelectedRelicLv = 5,

        CreditAmount = 6,
        PlanetAmount = 7,
        DronebitAmount = 8,

        ClassLevel = 9,
        ClassScore = 10,

        GoldClassMissions = 11,
        ToolMaxExp = 12,

        // 本家0.0.78.26より移植。各ジョブのマスターシップポイント(WKSScoreList[i].Unknown5が指すアイテム)所持数を目標にする。
        MasteryScore = 13
    }
}
