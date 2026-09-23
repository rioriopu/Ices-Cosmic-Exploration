using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.Utilities.Cosmic_Helper;

public static unsafe partial class CosmicHelper
{
    public static string PlaylistOptionString(PlaylistOptions option)
    {
        if (CosmicMoonRegistry.TryGetMoonForMaxRelicOption(option, out var moon))
            return $"Max {moon.DisplayName} Relic [Lv. {moon.MaxRelicStage}]";

        // UI に表示する名前なので辞書で日本語化する
        return Loc.T(option switch
        {
            PlaylistOptions.None => "None",
            PlaylistOptions.SelectedRelicLv => "Selected Relic Level",
            PlaylistOptions.CreditAmount => "Credit Amount",
            PlaylistOptions.PlanetAmount => "Planetary Credit Amount",
            PlaylistOptions.DronebitAmount => "Planetary Dronebit Amount",
            PlaylistOptions.ClassLevel => "Class Level",
            PlaylistOptions.ClassScore => "Class Score",
            PlaylistOptions.GoldClassMissions => "All Missions Golded",
            PlaylistOptions.ToolMaxExp => "Max Tool Exp",
            PlaylistOptions.MasteryScore => "Mastery Score",
            _ => "???"
        });
    }
}
