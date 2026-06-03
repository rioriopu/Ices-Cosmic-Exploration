using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.Utilities.Cosmic_Helper;

public static unsafe partial class CosmicHelper
{
    public static string PlaylistOptionString(PlaylistOptions option)
    {
        return option switch
        {
            PlaylistOptions.None => "None",
            PlaylistOptions.SinusMax => "Max Sinus Relic [Lv. 9]",
            PlaylistOptions.PhaennaMax => "Max Phaenna Relic [Lv. 14]",
            PlaylistOptions.OizysMax => "Max Oizys Relic [Lv. 17]",
            PlaylistOptions.SelectedRelicLv => "Selected Relic Level",
            PlaylistOptions.CreditAmount => "Credit Amount",
            PlaylistOptions.PlanetAmount => "Planetary Credit Amount",
            PlaylistOptions.DronebitAmount => "Planetary Dronebit Amount",
            PlaylistOptions.ClassLevel => "Class Level",
            PlaylistOptions.ClassScore => "Class Score",
            PlaylistOptions.GoldClassMissions => "All Missions Golded",
            PlaylistOptions.ToolMaxExp => "Max Tool Exp",
            _ => "???"
        };
    }
}
