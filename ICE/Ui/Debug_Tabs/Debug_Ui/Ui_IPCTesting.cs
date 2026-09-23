using ECommons.ExcelServices.TerritoryEnumeration;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ICE.Ui.Debug_Tabs.Debug_Ui
{
    internal class Ui_IPCTesting
    {
        private static int Radius = 10;
        private static int XLoc = 0;
        private static int YLoc = 0;
        private static string PandoraFeature = "";
        private static int amount = 1000;

        private static string importString = new string('\0', 2048); // Pre-allocate buffer
        private static string SwapToPreset = string.Empty;
        private static uint missionId = 0;
        private static uint baitId = 0;
        private static bool baitSwapped = false;
        private static uint MMSAmount = 20;
        private static uint MMMaxUse = 1;
        private static bool tempMM = true;


        private static string SettingChange = "";
        private static bool SettingState = false;

        public static unsafe void Draw()
        {
            ImGui.Text($"Artisan Is Busy? {P.Artisan.IsBusy()}");
            ImGui.Text($"{EzThrottler.GetRemainingTime("[Main Item(s)] Starting Main Craft")}");
            if (ImGui.Button("Artisan, craft this"))
            {
                P.Artisan.CraftItem(36026, 1);
            }

            ImGui.SetNextItemWidth(125);
            ImGui.InputInt("Radius", ref Radius);
            ImGui.SetNextItemWidth(125);
            ImGui.InputInt("X Location", ref XLoc);
            ImGui.SetNextItemWidth(125);
            ImGui.InputInt("Y Location", ref YLoc);

            if (ImGui.Button($"Test Radius"))
            {
                var agent = AgentMap.Instance();

                Utils.SetGatheringRing(agent->CurrentTerritoryId, XLoc, YLoc, Radius);
            }

            ImGui.Separator();
            ImGui.InputText("Pandora Feature", ref PandoraFeature);
            if (ImGui.Button("Pause Feature"))
            {
                P.Pandora.PauseFeature(PandoraFeature, amount);
            }

            ImGui.Separator();
            ImGui.Text("AutoHook");
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Preset String", ref importString, 2048);
            if (ImGui.Button("Import"))
            {
                P.AutoHook.ImportAndSelectPreset(importString);
                importString = string.Empty;
            }
            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Swap to preset", ref SwapToPreset);
            if (ImGui.Button("Swap"))
            {
                P.AutoHook.SetPreset(SwapToPreset);
            }
            if (ImGui.Button("Apply Temp"))
            {
                P.AutoHook.CreateAndSelectAnonymousPreset(importString);
            }
            ImGui.SetNextItemWidth(200);
            ImGui.InputUInt("Select mission to import", ref missionId);
            ImGui.InputUInt("Bait ID", ref baitId);
            if (ImGui.Button("Swap to bait"))
            {
                 SwapBait(baitId);
            }
            if (ImGui.Button("Swap Bait... simple"))
            {
                if (CosmicHelper.CurrentBait() == 0)
                {
                    IceLogging.Debug("Bait is not currently equipped");
                }

                P.AutoHook.SwapBaitById(baitId);
            }
            if (ImGui.Button("Stupid Test"))
            {
                if (CosmicHelper.CurrentBait() == 0)
                {
                    IceLogging.Debug($"No bait is equipped");
                }
                else if (CosmicHelper.CurrentBait == null)
                {
                    IceLogging.Debug("Bait is null... aka not in the middle of a mission");
                }
                else
                {
                    IceLogging.Debug($"Current bait: {CosmicHelper.CurrentBait}");
                }
            }

            if (ImGui.Button("Enable AutoHook"))
            {
                P.AutoHook.Ah_State(true);
            }
            if (ImGui.Button("Disable Autohook"))
            {
                P.AutoHook.Ah_State(false);
            }

            ImGui.Separator();
            ImGui.Text($"Is ICE Running? | {P.IceIpc.IsRunning()}");
            if (ImGui.Button("Only Missions Via IPC"))
            {
                HashSet<uint> missionListIds = new() { 1, 3, 4, 7, 9, 11 };
                P.IceIpc.OnlyMissions(missionListIds);
            }
            if (ImGui.Button("Change to gamba"))
            {
                SchedulerMain.State = IceState.Gambling;
            }

            ImGui.Separator();

            ImGui.SetNextItemWidth(150);
            ImGui.InputText("Setting Name", ref SettingChange);
            ImGui.Checkbox("Setting Bool", ref SettingState);

            if (ImGui.Button("Toggle Setting"))
            {
                P.IceIpc.ChangeSetting(SettingChange, SettingState);
            }
            if (ImGui.Button("Set temp setting"))
            {
                P.Artisan.ChangeSolver(37084, "Progress Only Solver", true);
            }
            if (ImGui.Button("Set raphael solver"))
            {
                P.Artisan.ChangeSolver(37084, "Raphael Recipe Solver", true);
            }
            if (ImGui.Button("Set current mission to Raphael"))
            {
                if (CosmicHelper.CurrentLunarMission != 0)
                {
                    foreach (var craftItem in CosmicHelper.CurrentMissionInfo?.Crafts_Main ?? new())
                    {
                        P.Artisan.ChangeSolver(craftItem.Value.RecipeId, "Raphael Recipe Solver", true);
                    }
                    foreach (var preCraft in CosmicHelper.CurrentMissionInfo?.Crafts_Pre ?? new())
                    {
                        P.Artisan.ChangeSolver(preCraft.Value.RecipeId, "Raphael Recipe Solver", true);
                    }
                }
            }
            if (ImGui.Button("Set current mission to Progress"))
            {
                if (CosmicHelper.CurrentLunarMission != 0)
                {
                    foreach (var craftItem in CosmicHelper.CurrentMissionInfo?.Crafts_Main ?? new())
                    {
                        P.Artisan.ChangeSolver(craftItem.Value.RecipeId, "Progress Only Solver", true);
                    }
                    foreach (var preCraft in CosmicHelper.CurrentMissionInfo?.Crafts_Pre ?? new())
                    {
                        P.Artisan.ChangeSolver(preCraft.Value.RecipeId, "Progress Only Solver", true);
                    }
                }
            }
            ImGui.DragUInt("MM Step Use", ref MMSAmount, 1, 0, 20);
            ImGui.DragUInt("MM Recipe Usage", ref MMMaxUse, 1, 0, 3);
            ImGui.Checkbox("Set MM Temp", ref tempMM);
            if (ImGui.Button("Set Miracle Solver"))
            {
                P.Artisan.ChangeStandardMinimumStepsBeforeMiracle(MMSAmount, tempMM);
                P.Artisan.ChangeStandardMaxMaterialMiracleUses(MMMaxUse, tempMM);
            }
            ImGui.SameLine();
            if (ImGui.Button("Restore Temp MM"))
            {
                P.Artisan.SetTempStandardMinimumStepsBeforeMiracleBackToNormal();
                P.Artisan.SetTempStandardMaxMaterialMiracleUsesBackToNormal();
            }

            if (ImGui.Button("Return back to normal"))
            {
                if (CosmicHelper.CurrentLunarMission != 0)
                {
                    foreach (var craftItem in CosmicHelper.CurrentMissionInfo?.Crafts_Main ?? new())
                    {
                        P.Artisan.SetTempSolverBackToNormal(craftItem.Value.RecipeId);
                    }
                    foreach (var preCraft in CosmicHelper.CurrentMissionInfo?.Crafts_Pre ?? new())
                    {
                        P.Artisan.SetTempSolverBackToNormal(preCraft.Value.RecipeId);
                    }
                }
            }
            if (ImGui.Button("Disable Endurance"))
            {
                P.Artisan.SetEnduranceStatus(false);
            }
            if (ImGui.Button("Test Toast"))
            {
                string message = "[I.C.E.] You didn't read the little warning in the mission setup\n" +
                    "You need to update autohook for you to be able to fish here on Auxesia. Please swap to testing version";
                Svc.Chat.Print(new()
                {
                    Type = Dalamud.Game.Text.XivChatType.ErrorMessage,
                    Message = message,
                });
                Svc.Toasts.ShowNormal($"{message}");
            }
            if (ImGui.Button("Test Glamour"))
            {
                P.GlamourIpc.SetClownHead();
            }
            if (ImGui.Button("Test Hat"))
            {
                P.GlamourIpc.SetHat();
            }
            if (ImGui.Button("Test Visor"))
            {
                P.GlamourIpc.SetVisor();
            }
        }

        // SwapBaitById は AutoHook 側の実体が同期 bool のため、await せず直接呼ぶ。
        private static void SwapBait(uint baitId)
        {
            baitSwapped = P.AutoHook.SwapBaitById(baitId);
        }
    }
}
