using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using ICE.Utilities.ImGuiTools;
using System.Collections.Generic;
using System.Reflection;
using static ICE.ConfigFiles.Config.MissionSettings;

namespace ICE.Ui
{
    internal class Window_ExternalDetails : Window
    {
        public static uint SelectedMission = 0;

        // 秒数を mm:ss.ff 形式に整形する。double.MaxValue や NaN/Infinity、TimeSpan の範囲外など
        // 不正値は TimeSpan.FromSeconds が OverflowException を投げてクラッシュするため、安全に "--:--" を返す。
        private static string FormatSeconds(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return "--:--";
            }

            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.ff");
        }

        public static List<string> JokeList = new()
        {
            "What is a pirates favorite letter?\n" +
            "You might thing it's R, but tis first love was the C\n" +
            "(It helps if you verbally say it like a pirate)",

            "You know, I was reading this book about anti-gravity recently,\n" +
            "and honestly I'm having a hard time putting it down",

            "Why are tennis pros always hugging each other?\n" +
            "Because they start their match at \"Love All\"",

            "Why can't ghost have babies?\n" +
            "Because they have hallow-eenies",

            "How do you save a drowning pirate?\n" +
            "You give him Cprrrrrr",

            "What is a skeleton's favorite snack?\n" +
            "Ribs! Spare Ribs!",

            "Honestly, just wanted to say thank you for using my plugin, you're appreciated <3",

            "Knock knock\n" +
            "[This is where you say who's there]\n" +
            "Lettuce\n" +
            "[Lettuce who]\n" +
            "Lettuce in",

            "What do you a dinosaur that only has one eye?" +
            "A \"Doyouthinkheseemesaurs\"",

            "So... you're telling me a shrimp fried this rice?",

            "Thank you everyone who's helped make this possible.\n" +
            "Strife special shoutout to you for doing what I didn't want to with fishing\n" +
            "(Sorry for making you start big fish #NotSorry#MuchLove)\n" +
            "Wah thank you for the UI, this is fucking beautiful as always\n" +
            "Puni.sh in general for each one of your help my dumb questions"
        };
        public static int jokeId = 0;

        public Window_ExternalDetails() : base($"Ice's Cosmic Exploration | Mission Details")
        {
            Flags = ImGuiWindowFlags.None;
            SizeConstraints = new()
            {
                MinimumSize = new Vector2(500, 500)
            };
            P.windowSystem.AddWindow(this);
        }

        public void Dispose()
        {
            P.windowSystem.RemoveWindow(this);
        }

        public override void OnOpen()
        {
            Collapsed = false;
            BringToFront();
            CollapsedCondition = ImGuiCond.Appearing;
        }

        private bool _openStatsTab = false;
        public void OpenToStatsTab(uint missionId)
        {
            SelectedMission = missionId;
            P.externalDetails.IsOpen = true;
            _openStatsTab = true;
        }

        public override void Draw()
        {
            if (CosmicHelper.SheetMissionDict.TryGetValue(SelectedMission, out var sheetInfo))
            {
                ImGui.Text($"Mission:");
                ImGui.SameLine(0, 5);
                ImGui.TextDisabled($"[{SelectedMission}]");
                ImGui.SameLine(0, 5);
                ImGui.Text($"{sheetInfo.Name}");

                if (ImGui.BeginTabBar("Mission Details Master Tabs"))
                {
                    if (ImGui.BeginTabItem("Details"))
                    {
                        MissionDetails(sheetInfo);
                        ImGui.EndTabItem();
                    }

                    if (CosmicHelper.CrafterJobList.ContainsAny(sheetInfo.Jobs))
                    {
                        if (ImGui.BeginTabItem("Craft Details"))
                        {
                            CraftDetails(sheetInfo);
                            ImGui.EndTabItem();
                        }
                    }

                    var statsFlag = _openStatsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    _openStatsTab = false;

                    if (ImGui.BeginTabItem("Completion Stats", statsFlag))
                    {
                        StatInfo(sheetInfo);
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
        }
        private static IDalamudTextureWrap TrophyIcon(TurninState state)
        {
            string resource = state switch
            {
                TurninState.Bronze => "ICE.Resources.TrophyIcons.bronze_trophy.png",
                TurninState.Silver => "ICE.Resources.TrophyIcons.silver_trophy.png",
                TurninState.Gold => "ICE.Resources.TrophyIcons.gold_trophy.png",
                _ => "ICE.Resources.TrophyIcons.bronze_trophy.png",
            };

            var texture = Svc.Texture.GetFromManifestResource(Assembly.GetExecutingAssembly(), resource).GetWrapOrEmpty();
            return texture;
        }
        private static void MissionDetails(CosmicHelper.CosmicInfo mission)
        {
            float scale = ImGuiHelpers.GlobalScale;
            Vector2 size = new Vector2(24 * scale, 24 * scale);

            void ItemInfo(uint itemId, uint amount)
            {
                if (amount != 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    if (ExcelHelper.ItemSheet.TryGetRow(itemId, out var itemSheet))
                    {
                        var iconId = (int)itemSheet.Icon;
                        if (Svc.Texture.TryGetFromGameIcon(iconId, out var iconTexture))
                        {
                            ImGui_Ice.ImageButtonWithText(iconTexture.GetWrapOrEmpty(), $"{itemSheet.Name}", $"{itemSheet.Name}", size);
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{amount:N0}");
                }
            }

            void ScoreInfo(TurninState state, string label, uint score)
            {
                var texture = TrophyIcon(state);
                if (score != 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui_Ice.ImageButtonWithText(texture, label, label, size);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text($"{score:N0}");
                }
            }

            void RelicInfo()
            {
                var exps = mission.RelicXpInfo
                    .Where(x => x.Value != 0)
                    .OrderBy(x => x.Key)
                    .ToList();

                if (exps.Count != 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"Mission Exp[s]");

                    ImGui.TableNextColumn();
                    for (int i = 0; i < exps.Count; i++)
                    {
                        var (tier, value) = (exps[i].Key, exps[i].Value);

                        Vector4 pillColor = tier switch
                        {
                            1 => new Vector4(0.9f, 0.8f, 0.1f, 0.8f), // I   - Yellow
                            2 => new Vector4(0.9f, 0.5f, 0.1f, 0.8f), // II  - Orange
                            3 => new Vector4(0.8f, 0.2f, 0.2f, 0.8f), // III - Red
                            4 => new Vector4(0.6f, 0.2f, 0.8f, 0.8f), // IV  - Purple
                            5 => new Vector4(0.2f, 0.4f, 0.9f, 0.8f), // V   - Blue
                            6 => new Vector4(0.4f, 0.8f, 1.0f, 0.8f), // VI  - Light Blue
                            7 => new Vector4(0.2f, 0.8f, 0.3f, 0.8f), // VII - Green
                            _ => new Vector4(0.5f, 0.5f, 0.5f, 0.8f),
                        };

                        string roman = tier switch
                        {
                            1 => "I",
                            2 => "II",
                            3 => "III",
                            4 => "IV",
                            5 => "V",
                            6 => "VI",
                            7 => "VII",
                            _ => "?"
                        };


                        using (ImRaii.PushColor(ImGuiCol.Button, pillColor)
                                     .Push(ImGuiCol.ButtonHovered, pillColor with { W = 1.0f })
                                     .Push(ImGuiCol.ButtonActive, pillColor))
                        {
                            ImGui.SmallButton($"{roman}:{value}##exp{tier}");
                        }

                        if (i < exps.Count - 1)
                            ImGui.SameLine();
                    }
                }
            }

            if (ImGui.BeginTable("Detailed Mission Info", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Name");
                ImGui.TableSetupColumn("Info");

                // Cosmocredits
                ItemInfo(45690, mission.CosmoCredit);

                // Planetary Credit
                if (CosmicMoonRegistry.TryGetPlanetCreditItemId(mission.TerritoryId, out var planetCreditId))
                {
                    ItemInfo(planetCreditId, mission.LunarCredit);
                }

                if (CosmicMoonRegistry.TryGetDronebit(mission.TerritoryId, out var dronebitId))
                {
                    ItemInfo(dronebitId.creditId, mission.DronebitReward);
                }

                if (CosmicMoonRegistry.TokenIds.TryGetValue(mission.TerritoryId, out var tokenId))
                {
                    ItemInfo(tokenId.tokenId, mission.TokenItemAmount);
                }

                RelicInfo();

                ScoreInfo(TurninState.Gold, "Class Score", mission.ClassScore);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Job(s)");

                ImGui.TableNextColumn();
                ImGui_Ice.DrawJobIconButton("Jobs", mission.Jobs);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Completed:");

                ImGui.TableNextColumn();
                ImGui_Ice.CompletionStatusIcon(mission);

                ScoreInfo(TurninState.Bronze, "Bronze Requirement", mission.BronzeScore);
                ScoreInfo(TurninState.Silver, "Silver Requirement", mission.SilverScore);
                ScoreInfo(TurninState.Gold, "Gold Requirement", mission.GoldScore);

                if (mission.MarkerId != 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Gathering Zone");

                    ImGui.TableNextColumn();

                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGui.Text(FontAwesomeIcon.Flag.ToIconString());
                    ImGui.PopFont();
                    if (ImGui.IsItemClicked())
                    {
                        Utils.SetGatheringRing(mission.TerritoryId, (int)mission.MapPosition.X, (int)mission.MapPosition.Y, mission.Radius, mission.Name);
                    }
                }

                if (GatheringUtil.CriticalSpots.TryGetValue(mission.Critical_MapKey, out var criticalInfo))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Critical Area");

                    ImGui.TableNextColumn();
                    ImGuiEx.Icon(FontAwesomeIcon.Flag);
                    if (ImGui.IsItemClicked())
                    {
                        Utils.SetFlagForNPC(mission.TerritoryId, criticalInfo.X, criticalInfo.Y);
                    }
                }

                if (mission.TemporaryAction.ActionId != 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text("Mission Skill");

                    ImGui.TableNextColumn();
                    ImGui_Ice.ImageButtonWithText(mission.TemporaryAction.Icon.GetWrapOrEmpty(), $"{mission.TemporaryAction.Name}", "tempAction", size);
                    if (ImGui.IsItemHovered() && mission.TemporaryAction.UseAmount != 0)
                    {
                        ImGui.SetTooltip($"Max Use: {mission.TemporaryAction.UseAmount}");
                    }
                }

                if (mission.Supplies.Count > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Supplied Items");

                    ImGui.TableNextColumn();
                    for (int i = 0; i < mission.Supplies.Count(); i++)
                    {
                        var supply = mission.Supplies[i];
                        ImGui_Ice.ImageButtonWithText(supply.Icon.GetWrapOrEmpty(), $"{supply.Count:N0}", "Supplyitem", size);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            ImGui.Text($"ItemId: {supply.ItemId}");
                            ImGui.Text($"Name: {supply.Name}");
                            ImGui.EndTooltip();
                        }
                        if (i+1 < mission.Supplies.Count())
                        {
                            ImGui.SameLine();
                            ImGui.Text(" | ");
                            ImGui.SameLine();
                        }
                    }
                }

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Notes [Hover over]");

                ImGui.TableNextColumn();
                var HasSPM = mission.BestSPM.SPM > 0;
                var HasSequence = mission.SequenceMissions_Next.Count() > 0 || mission.SequenceMissions_Previous.Count() > 0;
                var HasUnlockable = mission.MissionUnlock.Count() > 0;

                if (HasSPM)
                {
                    ImGuiEx.IconButton(FontAwesomeIcon.Trophy);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"Average SPM: {mission.BestSPM.SPM:N2}");
                        ImGui.Text($"{mission.BestSPM.NoteInfo}");
                        ImGui.EndTooltip();
                    }
                }
                if (HasSequence)
                {
                    if (HasSPM)
                        ImGui.SameLine();

                    ImGuiEx.IconButton(FontAwesomeIcon.ListOl);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        if (mission.SequenceMissions_Next.Count() > 0)
                        {
                            ImGui.Text("Next Sequence:");
                            foreach (var missionSeq in mission.SequenceMissions_Next)
                            {
                                var seqInfo = CosmicHelper.SheetMissionDict[missionSeq];
                                ImGui.Text($"[{missionSeq}] {seqInfo.Name}");
                            }
                        }
                        if (mission.SequenceMissions_Previous.Count() > 0)
                        {
                            ImGui.Text("Previous Sequence:");
                            foreach (var missionSeq in mission.SequenceMissions_Previous)
                            {
                                var seqInfo = CosmicHelper.SheetMissionDict[missionSeq];
                                ImGui.Text($"[{missionSeq}] {seqInfo.Name}");
                            }
                        }
                        ImGui.EndTooltip();
                    }
                }
                if (HasUnlockable)
                {
                    if (HasSPM || HasSequence)
                    {
                        ImGui.SameLine();
                    }
                    if (Svc.Texture.GetFromGame("ui/uld/WKSMission_hr1.tex") is { } tex)
                    {
                        var frameHeight = ImGui.GetFrameHeight();
                        if (tex.TryGetWrap(out var wrap, out var exc))
                        {
                            ImGui.ImageButton(wrap.Handle, size, new Vector2(0.2347f, 0.3500f), new Vector2(0.2959f, 0.6500f));
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text("The following missions are required to have gold before you can do this one");
                        foreach (var missionUnlock in mission.MissionUnlock)
                        {
                            ImGui_Ice.CompletionStatusIcon(CosmicHelper.SheetMissionDict[missionUnlock]);
                            ImGui.SameLine();
                            ImGui.Text($"[{mission}] - {CosmicHelper.SheetMissionDict[missionUnlock].Name}");
                        }
                        ImGui.EndTooltip();
                    }
                }
                if (CosmicMissionLists.QuickLevelList.Contains(SelectedMission))
                {
                    ImGuiEx.IconButton(FontAwesomeIcon.Leaf);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Leveling mode mission");
                    }
                }

                ImGui.EndTable();
            }

            if (mission.ExpModifier_3 != 0)
            {
                if (ImGui.BeginTable("Exp Rewards", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Class Exp");
                    ImGui.TableSetupColumn("% of Level");

                    ImGui.TableHeadersRow();

                    if (mission.ExpModifier_1 != 0)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text("Lv. 10-49");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{mission.ExpModifier_1}%");
                    }

                    if (mission.ExpModifier_2 != 0)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text("Lv. 50-89");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{mission.ExpModifier_2}%");
                    }

                    if (mission.ExpModifier_3 != 0)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text("Lv. 90-99");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{mission.ExpModifier_3}%");
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.Text("Mission Atributes");
            if (mission.Attributes == MissionAttributes.None)
            {
                ImGui.Text("None");
                return;
            }
            else
            {
                foreach (MissionAttributes flag in Enum.GetValues<MissionAttributes>())
                {
                    if (flag != MissionAttributes.None && mission.Attributes.HasFlag(flag))
                    {
                        ImGui.Text($"{EnumNameConverter(flag)}");
                    }
                }
            }
        }
        private static void CraftDetails(CosmicHelper.CosmicInfo mission)
        {
            if (mission.Crafts_Main.Count > 0)
            {
                CosmicHelper.CrafterManagement(mission, SelectedMission);
            }
        }
        private static void StatInfo(CosmicHelper.CosmicInfo missionInfo)
        {
            if (C.MissionConfig.TryGetValue(SelectedMission, out var config))
            {
                bool allowDelete = (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift)) && (ImGui.IsKeyDown(ImGuiKey.LeftCtrl) || ImGui.IsKeyDown(ImGuiKey.RightCtrl));

                using (ImRaii.Disabled(!allowDelete))
                {
                    if (ImGui.Button("Reset Stats"))
                    {
                        P.MissionTimer.ResetTimers(SelectedMission);
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("Hold Shift + Control");
                    ImGui.EndTooltip();
                }

                if (config.TurninRecords.Count > 0)
                {
                    ImGui.Text($"Best Time: {FormatSeconds(config.BestTimeOverall())}");
                    ImGui.Text($"Average Time: {FormatSeconds(config.AverageTime())}");
                }
                else
                {
                    ImGui.Text("Best Time: --:--:--");
                    ImGui.Text("Average Time: --:--:--");
                }

                ImGui.Text($"Times Completed: {config.TotalCompletions}");
                ImGui.Text($"Times Attempted: {config.TotalAttempts}");

                var baseScore = missionInfo.ClassScore;
                var comsoCredit = missionInfo.CosmoCredit;
                var planetCredit = missionInfo.LunarCredit;

                ImGui.Separator();
                ImGui.Text("Estimated Score Per Hour:");
                ImGui.SameLine();
                ImGui.TextDisabled("?");
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text("This is ASSUMING:");
                    ImGui.Text("1: You have immaculate rng of getting the mission you want every time");
                    ImGui.Text("2: You're hitting the threshold every time");
                    ImGui.Text("This is based on your average time.\n" +
                               "So get a good couple of runs to get a good feel for the timing");
                    ImGui.EndTooltip();
                }
                if (ImGui.BeginTable("Score Info: External Details", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                {
                    foreach (var entry in missionInfo.ScoreInfo().Where(x => x.Value.Score != 0))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.Text($"{entry.Key} [{entry.Value.Completions:N0}]");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{entry.Value.Score:N2}");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{entry.Value.Cosmocredit:N2}");

                        ImGui.TableNextColumn();
                        ImGui.Text($"{entry.Value.PlanetCredits:N2}");

                        ImGui.TableNextColumn();
                        string tokens = entry.Value.Tokens > 0 ? $"{entry.Value.Tokens:N2}" : "-";
                        ImGui.Text(tokens);
                    }

                    ImGui.EndTable();
                }
                if (config.TotalCompletions != 0)
                {
                    if (ImGui.BeginChild("Mission Timers", ImGui.GetContentRegionAvail()))
                    {
                        // Group records by state, preserving enum order
                        var recordsByState = config.TurninRecords
                            .GroupBy(r => r.State)
                            .OrderBy(g => (int)g.Key)
                            .ToList();

                        if (ImGui.BeginTabBar("Completion Stats"))
                        {
                            // "All" tab always shown if there are any records
                            if (ImGui.BeginTabItem("All"))
                            {
                                DrawTurninTable(config.TurninRecords);
                                ImGui.EndTabItem();
                            }

                            // One tab per state that has at least one record
                            foreach (var group in recordsByState)
                            {
                                var label = group.Key.ToString();
                                if (ImGui.BeginTabItem(label))
                                {
                                    DrawTurninTable(group.ToList());
                                    ImGui.EndTabItem();
                                }
                            }

                            ImGui.EndTabBar();
                        }
                    }
                    ImGui.EndChild();
                }
            }
        }
        public static string EnumNameConverter(MissionAttributes attribute)
        {
            return attribute switch
            {
                MissionAttributes.Craft => "Crafting",
                MissionAttributes.Gather => "Gathering",
                MissionAttributes.Fish => "Fishing",
                MissionAttributes.Limited => "Limited Supplies",
                MissionAttributes.Collectables => "Collectable",
                MissionAttributes.ReducedItems => "Reducable Items",
                MissionAttributes.ExpertCraft => "Expert Crafts",
                MissionAttributes.Score_TimeRemaining => "Timed Scoring",
                MissionAttributes.Score_Chain => "Chained Gather Scoring",
                MissionAttributes.Score_Boon => "Gatherer's Boons Scoring",
                MissionAttributes.Score_LargestSize => "Largest Fish Scored",
                MissionAttributes.Score_Variety => "Variety of Fish Required",
                MissionAttributes.Score_MinimumScore => "Mission Score Required",
                MissionAttributes.Critical => "Critical Mission",
                MissionAttributes.ProvisionalTimed => "Time Required",
                MissionAttributes.ProvisionalWeather => "Weather Required",
                MissionAttributes.ProvisionalSequential => "Sequential Missions Required",
                _ => attribute.ToString()
            };
        }
        private  static void DrawTurninTable(List<TurninData> records)
        {
            if (!ImGui.BeginTable("TurninTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit)) 
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 100f);
            ImGui.TableHeadersRow();

            foreach (var record in records)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{FormatSeconds(record.Time)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(record.State.ToString());
            }

            ImGui.EndTable();
        }
    }
}
