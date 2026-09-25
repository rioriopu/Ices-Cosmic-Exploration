using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ICE.Ui.MainUi.HelpFolder
{
    internal class helpSelect_Required
    {
        public static void Draw()
        {
            ImGui.TextWrapped(Loc.T("These are a list of the following plugins that are required for the plugin to function. If you don't have these installed, it will not function properly"));

            ImGui.Separator();
            ImGuiEx.IconWithText(FontAwesomeIcon.Hammer, Loc.T("Crafting"));
            HasPlugin("https://love.puni.sh/ment.json", "Artisan");

            ImGui.Separator();
            ImGuiEx.IconWithText(FontAwesomeIcon.Feather, Loc.T("Gathering"));
            ImGui.Text(Loc.T("For botanist/miner/fisher"));
            HasPlugin("https://puni.sh/api/repository/veyn", "vnavmesh");
            ImGui.Dummy(new Vector2(0, 10));
            ImGui.Text(Loc.T("For fisher only"));
            HasPlugin("https://love.puni.sh/ment.json", "AutoHook");

            ImGui.Separator();
            ImGuiEx.IconWithText(FontAwesomeIcon.Running, Loc.T("Automating Hub Activities"));
            HasPlugin("https://puni.sh/api/repository/veyn", "vnavmesh");

            ImGui.Separator();
            ImGuiEx.IconWithText(FontAwesomeIcon.Bell, Loc.T("Collecting retainer ventures"));
            ImGui.Text(Loc.T("Only if you enable it in Misc settings"));
            HasPlugin("https://love.puni.sh/ment.json", "AutoRetainer");

            ImGui.Separator();
            ImGui.TextWrapped(Loc.T("This isn't required, but highly recommended for leveling up characters. It will auto equip gear from your armory/inventory, and swap it out when running Leveling Grind Mode"));
            ImGuiEx.IconWithText(FontAwesomeIcon.Leaf, Loc.T("Stylist"));
            HasPlugin("https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json", "Stylist");
        }

        public static void HasPlugin(string repo, string pluginName)
        {
            bool isInstalled = DalamudReflector.HasRepo($"{repo}");
            if (isInstalled)
            {
                FontAwesome.Print(EColor.Green, FontAwesome.Check);
                ImGui.SameLine();
                ImGui.Text($"{pluginName} Repo is Installed");
            }
            else
            {
                FontAwesome.Print(EColor.Red, FontAwesome.Cross);
                ImGui.SameLine();
                if (ImGui.Button($"Install {pluginName} Repo"))
                {
                    DalamudReflector.AddRepo(repo, true);
                    DalamudReflector.SaveDalamudConfig();
                }
            }

            bool hasPlugin = Utils.HasPlugin($"{pluginName}");

            if (hasPlugin)
            {
                FontAwesome.Print(EColor.Green, FontAwesome.Check);
                ImGui.SameLine();
                ImGui.Text($"{pluginName} is installed");
            }
            else
            {
                FontAwesome.Print(EColor.Red, FontAwesome.Cross);
                ImGui.SameLine();
                using (ImRaii.Disabled(installingPlugin))
                {
                    if (ImGui.Button($"Install {pluginName}"))
                    {
                        _ = InstallPlugin(repo, pluginName);
                    }
                }
            }
        }

        private static bool installingPlugin = false;
        private static async Task InstallPlugin(string repo, string pluginName)
        {
            if (installingPlugin) return; // Already installing

            installingPlugin = true;
            try
            {
                await DalamudReflector.AddPlugin(repo, pluginName);
                DalamudReflector.SaveDalamudConfig();
            }
            finally
            {
                installingPlugin = false;
            }
        }
    }
}
