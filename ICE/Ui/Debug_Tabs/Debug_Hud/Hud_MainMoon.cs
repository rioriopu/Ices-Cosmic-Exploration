using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Ui.Debug_Tabs.Debug_Hud
{
    internal class Hud_MainMoon
    {
        public static void Draw()
        {
            if (GenericHelpers.TryGetAddonMaster<WKSHud>("WKSHud", out var HudAddon))
            {
                if (ImGui.Button(Loc.T("Mission")))
                {
                    HudAddon.Mission();
                }

                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Mech")))
                {
                    HudAddon.Mech();
                }

                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Steller")))
                {
                    HudAddon.Steller();
                }

                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Infrastructor")))
                {
                    HudAddon.Infrastructor();
                }

                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Research")))
                {
                    HudAddon.Research();
                }

                ImGui.SameLine();

                if (ImGui.Button(Loc.T("ClassTracker")))
                {
                    HudAddon.ClassTracker();
                }
            }
            else
            {
                ImGui.Text(Loc.T("Waiting for \"WKSHud\" to be visible"));
            }
        }
    }
}
