using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Ui.Debug_Tabs.Debug_Hud
{
    internal class Hud_MoonRecipe
    {
        public static void Draw()
        {
            if (GenericHelpers.TryGetAddonMaster<WKSRecipeNotebook>("WKSRecipeNotebook", out var x) && x.IsAddonReady)
            {
                ImGui.Text(x.SelectedCraftingItem);

                if (ImGui.Button(Loc.T("Fill NQ")))
                {
                    x.NQItemInput();
                }
                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Fill HQ")))
                {
                    x.HQItemInput();
                }
                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Fill Both")))
                {
                    x.NQItemInput();
                    x.HQItemInput();
                }
                ImGui.SameLine();

                if (ImGui.Button(Loc.T("Synthesize")))
                {
                    x.Synthesize();
                }

                foreach (var m in x.CraftingItems)
                {
                    if (ImGui.Button($"Select ###Select + {m.Name}"))
                    {
                        m.Select();
                    }
                    ImGui.SameLine();
                    ImGui.Text($"{m.Name}");
                }
            }
            else
            {
                ImGui.Text(Loc.T("Waiting for \"WKSRecipeNotebook\" to be visible"));
            }
        }
    }
}
