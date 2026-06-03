using Dalamud.Interface.Utility;
using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using static ICE.ConfigFiles.Config;

namespace ICE.Ui.MainUi.Settings
{
    internal class GambaWheel
    {
        private static bool gambaEnabled = C.GambaEnabled;
        private static int gambaDelay = C.GambaDelay;
        private static int gambaCreditsMinimum = C.GambaCreditsMinimum;
        private static bool gambaPreferSmallerWheel = C.GambaPreferSmallerWheel;

        public static unsafe void Draw_Old()
        {
            if (ImGui.Checkbox("Enable Auto Gamba", ref gambaEnabled))
            {
                C.GambaEnabled = gambaEnabled;
                C.Save();
            }
            ImGuiEx.HelpMarker("If you want to let it auto select the wheels and gamba, enable this. If you want to not auto run when you're running the gamble wheel, disable this.");
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Mininum credits to keep", ref gambaCreditsMinimum, 0, 10000))
            {
                C.GambaCreditsMinimum = gambaCreditsMinimum;
                C.SaveDebounced();
            }
            bool gambaBetween = C.GambaBetweenRuns;
            if (ImGui.Checkbox("Gamble Between Runs", ref gambaBetween))
            {
                C.GambaBetweenRuns = gambaBetween;
                C.Save();
            }
            ImGui.SameLine();
            GambaSlider();
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Gamba Delay", ref gambaDelay, 50, 2000))
            {
                C.GambaDelay = gambaDelay;
                C.SaveDebounced();
            }

            if (ImGui.Checkbox("Prefer smaller wheel", ref gambaPreferSmallerWheel))
            {
                C.GambaPreferSmallerWheel = gambaPreferSmallerWheel;
                C.Save();
            }
            ImGuiEx.HelpMarker("This will make the Gamba prefer wheels with less items.");

            if (PlayerHelper.IsInCosmicZone())
            {
                var territory = Player.Territory.RowId;
                // 未対応惑星でも設定ウィンドウが落ちないようTryGetValueでガード
                if (CosmicHelper.PlanetCreditInfo.TryGetValue(territory, out var itemId))
                {
                    PlayerHelper.GetItemCount(itemId, out var credits);
                    ImGui.Text($"Current location: {territory} | Currency Amount: {credits}");
                }
                else
                {
                    ImGui.Text($"Current location: {territory} | Currency: (未対応)");
                }
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Configure the weights for each item in the Gamba. Higher weight = more desirable.");
            ImGui.Spacing();
            foreach (GambaType type in Enum.GetValues(typeof(GambaType)))
            {
                var itemsType = C.GambaItemWeights.Where(x => x.Type == type).OrderBy(x => x.ItemId).ToList();
                if (itemsType.Count == 0) continue;
                if (ImGui.TreeNodeEx($"{type} ({itemsType.Count})##gamba_type_{type}", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    foreach (var gamba in itemsType)
                    {
                        var itemName = ExcelItemHelper.GetName(gamba.ItemId);
                        int weight = gamba.Weight;
                        ImGui.SetNextItemWidth(120f);
                        if (ImGui.InputInt($"[{gamba.ItemId}] {itemName}##gamba_weight", ref weight))
                        {
                            gamba.Weight = weight;
                            C.Save();
                        }
                    }
                    ImGui.Unindent();
                    ImGui.TreePop();
                }
            }
            if (ImGui.Button("Reset Weights"))
            {
                Task_Gamba.EnsureGambaWeightsInitialized(true);
            }
        }

        public static unsafe void Draw()
        {
            bool gambaEnabled = C.GambaEnabled;
            if (ImGui.Checkbox("Enable Auto Gamba Wheel", ref gambaEnabled))
            {
                C.GambaEnabled = gambaEnabled;
                C.Save();
            }
            ImGuiEx.HelpMarker("If you want to let it auto select the wheels and gamba, enable this. If you want to not auto run when you're running the gamble wheel, disable this.");
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Mininum credits to keep", ref gambaCreditsMinimum, 0, 10000))
            {
                C.GambaCreditsMinimum = gambaCreditsMinimum;
                C.SaveDebounced();
            }
            bool gambaBetween = C.GambaBetweenRuns;
            if (ImGui.Checkbox("Gamble Between Runs", ref gambaBetween))
            {
                C.GambaBetweenRuns = gambaBetween;
                C.Save();
            }
            GambaSlider();
            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Gamba Delay", ref gambaDelay, 50, 2000))
            {
                C.GambaDelay = gambaDelay;
                C.SaveDebounced();
            }

            if (ImGui.Checkbox("Prefer smaller wheel", ref gambaPreferSmallerWheel))
            {
                C.GambaPreferSmallerWheel = gambaPreferSmallerWheel;
                C.Save();
            }
            ImGuiEx.HelpMarker("This will make the Gamba prefer wheels with less items.");

            if (PlayerHelper.IsInCosmicZone())
            {
                var territory = Player.Territory.RowId;
                // 未対応惑星でも設定ウィンドウが落ちないようTryGetValueでガード
                if (CosmicHelper.PlanetCreditInfo.TryGetValue(territory, out var itemId))
                {
                    PlayerHelper.GetItemCount(itemId, out var credits);
                    ImGui.Text($"Current location: {territory} | Currency Amount: {credits}");
                }
                else
                {
                    ImGui.Text($"Current location: {territory} | Currency: (未対応)");
                }
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Configure the weights for each item in the Gamba. Higher weight = more desirable.");

            if (ImGui.Button("Reset Weights"))
            {
                Task_Gamba.EnsureGambaWeightsInitialized(true);
            }

            if (ImGui.BeginTabBar("Gamba Item Tabs"))
            {
                foreach (GambaType type in Enum.GetValues(typeof(GambaType)))
                {
                    var itemsType = C.GambaItemWeights.Where(x => x.Type == type).OrderBy(x => x.ItemId).ToList();
                    if (itemsType.Count == 0) continue;

                    if (ImGui.BeginTabItem($"{type.ToString()} [{itemsType.Count}]"))
                    {
                        if (ImGui.BeginTable($"{type.ToString()}_GambaItems", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                        {
                            ImGui.TableSetupColumn("Icon");
                            ImGui.TableSetupColumn("Unlocked");
                            ImGui.TableSetupColumn("Name");
                            ImGui.TableSetupColumn("Weight");

                            ImGui.TableHeadersRow();

                            foreach (var item in itemsType)
                            {
                                if (Svc.Data.GetExcelSheet<Item>().TryGetRow(item.ItemId, out var itemInfo))
                                {
                                    var iconId = itemInfo.Icon;
                                    var name = itemInfo.Name;
                                    var weight = item.Weight;

                                    ImGui.TableNextRow();
                                    ImGui.TableSetColumnIndex(0);
                                    if (Svc.Texture.TryGetFromGameIcon((int)iconId, out var iconImage) && iconImage != null)
                                    {
                                        var scale = ImGuiHelpers.GlobalScale;
                                        Vector2 imageSize = new Vector2(25 * scale, 25 * scale);

                                        ImGui.Image(iconImage.GetWrapOrEmpty().Handle, imageSize);
                                        if (ImGui.IsItemHovered())
                                        {
                                            ImGui.BeginTooltip();
                                            ImGui.Image(iconImage.GetWrapOrEmpty().Handle, new Vector2(50, 50));
                                            ImGui.EndTooltip();
                                        }
                                    }

                                    ImGui.TableNextColumn();
                                    ImGui.TextUnformatted(UnlockState.IsItemUnlockable(itemInfo) ? UnlockState.IsItemUnlocked(itemInfo) ? "Yes" : "No" : "-");
                                    
                                    ImGui.TableNextColumn();
                                    ImGui.Text($"{name}");

                                    ImGui.TableNextColumn();
                                    ImGui.SetNextItemWidth(200);
                                    if (ImGui.InputInt($"##weight_{name}_{item.ItemId}", ref weight))
                                    {
                                        item.Weight = weight;
                                        C.SaveDebounced();
                                    }
                                }
                            }

                            ImGui.EndTable();
                        }

                        ImGui.EndTabItem();
                    }
                }

                ImGui.EndTabBar();
            }
        }

        private static int[] allowedValues = { 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000 };
        private static void GambaSlider()
        {
            int currentIndex = Array.IndexOf(allowedValues, C.GambaAtAmount);
            if (currentIndex == -1)
            {
                currentIndex = 0;
                C.GambaAtAmount = allowedValues[0];
                C.SaveDebounced();
            }

            ImGui.SetNextItemWidth(150);
            if (ImGui.SliderInt("Start Gambling @", ref currentIndex, 0, allowedValues.Length - 1,
                allowedValues[currentIndex].ToString()))
            {
                C.GambaAtAmount = allowedValues[currentIndex];
                C.SaveDebounced();
            }
        }
    }
}
