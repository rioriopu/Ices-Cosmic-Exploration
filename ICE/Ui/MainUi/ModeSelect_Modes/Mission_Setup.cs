using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ICE.Ui.MainUi.ModeSelect_Modes.CosmicTable;
using ICE.Ui.MainUi.Settings;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.ImGuiTools;
using System.Collections.Generic;

namespace ICE.Ui.MainUi.ModeSelect_Modes
{
    internal class Mission_Setup
    {
        private static readonly Dictionary<string, uint> BattleJobs = new()
        {
            // Tanks
            { "Paladin", 19 },
            { "Warrior", 21 },
            { "Dark Knight", 32 },
            { "Gunbreaker", 37 },
    
            // Healers
            { "White Mage", 24 },
            { "Scholar", 28 },
            { "Astrologian", 33 },
            { "Sage", 40 },
    
            // Melee DPS
            { "Monk", 20 },
            { "Dragoon", 22 },
            { "Ninja", 30 },
            { "Samurai", 34 },
            { "Reaper", 39 },
            { "Viper", 41 },
    
            // Physical Ranged DPS
            { "Bard", 23 },
            { "Machinist", 31 },
            { "Dancer", 38 },
    
            // Magical Ranged DPS
            { "Black Mage", 25 },
            { "Summoner", 27 },
            { "Red Mage", 35 },
            { "Pictomancer", 42 }
        };

        public static CosmicTables.Mission_Table? MissionTable;
        private static List<CosmicHelper.MissionInfo> TableItems = [];
        private static int ItemCount = 0;
        private static string newListName = string.Empty;

        // 「レベリング装備を売却」の確認ダイアログ。
        private static void DrawSellLevelingGearPopup()
        {
            var plan = Task_SellLevelingGear.Current;
            bool jp = Task_SellLevelingGear.IsJapanese;
            if (plan == null)
            {
                ImGui.CloseCurrentPopup();
                return;
            }
            if (!string.IsNullOrEmpty(plan.Error))
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), plan.Error);
                if (ImGui.Button(Loc.T("No")))
                    ImGui.CloseCurrentPopup();
                return;
            }

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * ImGuiHelpers.GlobalScale);
            string category = jp ? (plan.IsGatherer ? "ギャザラー" : "クラフター") : (plan.IsGatherer ? "gatherers" : "crafters");
            ImGui.TextUnformatted(jp
                ? $"レベリング装備品（{plan.Items.Count}）を自動売却します。本当によろしいですか？※全{category}がLv100か確認してください"
                : $"Sell {plan.Items.Count} leveling gear item(s) automatically. Are you sure? *Make sure all {category} are Lv100 first");
            ImGui.TextDisabled(jp
                ? $"対象: {(plan.IsGatherer ? "ギャザラー" : "クラフター")}用のレベリング装備（ゴッドギスで買える Lv10〜95、アーマリーチェスト内の NQ 品のみ。HQ は売りません） / 見込み {plan.TotalGil:N0} ギル"
                : $"Target: {category}' leveling gear (Lv10–95 sold by the vendor, NQ items in the Armoury Chest only; HQ is never sold) / about {plan.TotalGil:N0} gil");
            ImGui.PopTextWrapPos();

            if (plan.Items.Count > 0 && ImGui.CollapsingHeader(Loc.T("Items to sell")))
            {
                using var list = ImRaii.Child("##lgear_sell_list", new Vector2(520 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale), true);
                if (list.Success)
                    foreach (var e in plan.Items)
                        ImGui.TextUnformatted($"{LevelingGearShop.SlotNameJp(e.GearSlot)}  {e.Name} (Lv{e.LevelEquip})  {e.Price:N0}g");
            }

            if (ImGui.Button(Loc.T("Yes"), new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                if (plan.Items.Count == 0)
                    IceLogging.ChatInfo(jp ? "売却するレベリング装備はありません" : "No leveling gear to sell", "[I.C.E.]");
                else
                    Task_SellLevelingGear.Enqueue(plan);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("No"), new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
                ImGui.CloseCurrentPopup();
        }

        // 「レベリング装備を購入」の確認ダイアログ。文面は動的なので、クライアント言語が日本語なら日本語で直接描く。
        private static void DrawBuyLevelingGearPopup()
        {
            var plan = Task_BuyLevelingGear.Current;
            bool jp = Task_BuyLevelingGear.IsJapanese;
            if (plan == null)
            {
                ImGui.CloseCurrentPopup();
                return;
            }

            if (!string.IsNullOrEmpty(plan.Error))
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), plan.Error);
                if (ImGui.Button(Loc.T("No")))
                    ImGui.CloseCurrentPopup();
                return;
            }

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * ImGuiHelpers.GlobalScale);
            int fromLv = plan.StepsUsed.Count > 0 ? plan.StepsUsed[0] : Task_BuyLevelingGear.MinLevel;
            ImGui.TextUnformatted(jp
                ? $"現在のジョブ【{plan.JobName} Lv{plan.JobLevel}】のLv{fromLv}～Lv{Task_BuyLevelingGear.MaxLevel}までの装備品をNPC購入します。Lv{plan.StepsText} の各段階で購入する（丁度のLvが無ければ下のLv、現在Lvより下の段階は買いません）ので、消費ギルは【{plan.TotalGil:N0}ギル】掛かりますが宜しいですか？"
                : $"Buy Lv{fromLv}–Lv{Task_BuyLevelingGear.MaxLevel} gear for your current job [{plan.JobName} Lv{plan.JobLevel}] from the NPC. Gear is bought at Lv{plan.StepsText} (the next lower level if none exists; steps below your current level are skipped), so this will cost [{plan.TotalGil:N0} gil]. Proceed?");
            ImGui.TextDisabled(jp
                ? $"購入 {plan.ToBuy.Count} 点（所持済み {plan.OwnedSkipped} 点は除外） / 所持ギル {plan.PlayerGil:N0}"
                : $"{plan.ToBuy.Count} items to buy ({plan.OwnedSkipped} already owned) / gil on hand {plan.PlayerGil:N0}");

            if (plan.PlayerGil < plan.TotalGil)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), jp
                    ? $"所持ギルが {plan.TotalGil - plan.PlayerGil:N0} ギル足りません"
                    : $"You are {plan.TotalGil - plan.PlayerGil:N0} gil short");

            if (plan.Shortage.Count > 0)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), jp
                    ? Loc.T("以下の部位（アーマリーチェスト）に空き枠が足りないため購入出来ません。")
                    : Loc.T("Not enough free Armoury Chest slots in the following slots, so nothing will be bought:"));
                foreach (var kv in plan.Shortage)
                    ImGui.BulletText(jp ? $"{LevelingGearShop.SlotNameJp(kv.Key)}：{kv.Value}枠不足" : $"{kv.Key}: {kv.Value} slot(s) short");
            }
            ImGui.PopTextWrapPos();

            if (plan.ToBuy.Count > 0 && ImGui.CollapsingHeader(Loc.T("Items to buy")))
            {
                using var list = ImRaii.Child("##lgear_list", new Vector2(520 * ImGuiHelpers.GlobalScale, 200 * ImGuiHelpers.GlobalScale), true);
                if (list.Success)
                    foreach (var e in plan.ToBuy)
                        ImGui.TextUnformatted($"Lv{e.StepLevel,2}  {LevelingGearShop.SlotNameJp(e.Item.Slot)}  {e.Item.Name} (Lv{e.Item.LevelEquip})  {e.Item.Price:N0}g");
            }

            if (ImGui.Button(Loc.T("Yes"), new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            {
                if (plan.Shortage.Count > 0)
                    IceLogging.ChatInfo(jp ? "アーマリーチェストに空き枠が足りないため、購入を中止しました" : "Purchase cancelled: not enough free Armoury Chest slots", "[I.C.E.]");
                else if (plan.PlayerGil < plan.TotalGil)
                    IceLogging.ChatInfo(jp ? "所持ギルが足りないため、購入を中止しました" : "Purchase cancelled: not enough gil", "[I.C.E.]");
                else if (plan.ToBuy.Count == 0)
                    IceLogging.ChatInfo(jp ? "購入する装備はありません（すべて所持済みです）" : "Nothing to buy: all gear is already owned", "[I.C.E.]");
                else
                    Task_BuyLevelingGear.Enqueue(plan);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("No"), new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
                ImGui.CloseCurrentPopup();
        }

        public static void Draw()
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 10).Push(ImGuiStyleVar.ChildBorderSize, 1);

            // Header at the top
            float scale = ImGuiHelpers.GlobalScale;

            using (var headerChild = ImRaii.Child("##modeSelect_StandardHeader", new Vector2(0, 45 * scale), true, ImGuiWindowFlags.NoScrollbar))
            {
                if (!headerChild.Success) return;

                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10 * scale);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 5 * scale);

                string modeType = string.Empty;
                FontAwesomeIcon modeIcon = FontAwesomeIcon.List;

                bool standard = C.SelectedMode == ModeSelect.Standard;
                bool relicMode = C.SelectedMode == ModeSelect.RelicMode;
                bool xpLeveling = C.SelectedMode == ModeSelect.LevelMode;
                bool goldMode = C.SelectedMode == ModeSelect.MissionGoldMode;
                bool agendaMode = C.SelectedMode == ModeSelect.AgendaMode;


                if (standard)
                    modeType = "Standard";
                else if (relicMode)
                {
                    modeType = "Relic Grind";
                    modeIcon = FontAwesomeIcon.ArrowUpRightDots;
                }
                else if (xpLeveling)
                {
                    modeType = "Leveling Grind";
                    modeIcon = FontAwesomeIcon.Leaf;
                }
                else if (goldMode)
                {
                    modeType = "Gold Completion Grind";
                    modeIcon = FontAwesomeIcon.Trophy;
                }
                else if (agendaMode)
                {
                    modeType = "Cosmic Agenda";
                    modeIcon = FontAwesomeIcon.ClipboardList;
                }

                ImGuiEx.IconWithText(modeIcon, Loc.T($"{modeType} Mode"));

                // レリックモードの一時レベリング中はその旨を表示する
                if (relicMode && RelicFallback.Active)
                {
                    ImGui.SameLine(0, 6 * scale);
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), Loc.T("→ Leveling (relic fallback)"));
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Task_BuyLevelingGear.IsJapanese
                            ? $"コスモデータ{RelicFallback.NeededTypes}は{RelicFallback.RankName(RelicFallback.RequiredRank)}クラスのミッションでしか得られないため、Lv{RelicFallback.RequiredLevel}到達と{RelicFallback.RankName(RelicFallback.RequiredRank)}クラス解放までレベリングモードで動いています"
                            : $"Analysis {RelicFallback.NeededTypes} only comes from rank {RelicFallback.RankName(RelicFallback.RequiredRank)} missions. Leveling until Lv{RelicFallback.RequiredLevel} and rank {RelicFallback.RankName(RelicFallback.RequiredRank)} is unlocked");
                }

                ImGui.SameLine(0, 10 * scale);

                // Adjust the Y position to center the button vertically with the text
                float textHeight = ImGui.GetTextLineHeight();
                float buttonHeight = ImGui.GetFrameHeight();
                float yOffset = (textHeight - buttonHeight) / 2f;
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                if (ImGuiEx.IconButtonWithText(FontAwesomeIcon.Play, Loc.T("Mode Selection")))
                {
                    ImGui.OpenPopup("Mode Select | Select Mode Window");
                }
                if (ImGui.BeginPopup("Mode Select | Select Mode Window"))
                {
                    MainWindow.ModeSelection();

                    ImGui.EndPopup();
                }

                uint currentJobId = (uint)Player.Job;
                bool usingSupportedJob = CosmicHelper.CrafterJobList.Contains(currentJobId) || CosmicHelper.GatheringJobList.Contains(currentJobId);

                bool AnyStop = C.StopOnceHitCosmicScore
                             | C.StopWhenLevel
                            || C.StopOnceHitCosmoCredits
                            || C.StopOnceHitLunarCredits
                            || C.StopOnceRelicFinished
                            || C.StopOnceStandardMissionsGolded;
                if (AnyStop)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();

                        ImGui.Text(Loc.T("It appears that you have on of the following enabled"));
                        if (C.StopOnceHitCosmicScore)
                            ImGui.BulletText($"Stop at Cosmic Score [{C.CosmicScoreCap:N0}]");
                        if (C.StopWhenLevel)
                            ImGui.BulletText($"Stop When Level [{C.TargetLevel:N0}]");
                        if (C.StopOnceHitCosmoCredits)
                            ImGui.BulletText($"Stop once cosmo credit hit [{C.CosmoCreditsCap:N0}]");
                        if (C.StopOnceHitLunarCredits)
                            ImGui.BulletText($"Stop once planetary credit hit [{C.LunarCreditsCap:N0}]");
                        if (C.StopOnceRelicFinished)
                            ImGui.BulletText(Loc.T("Stop once relic completed"));
                        if (C.StopOnceStandardMissionsGolded)
                            ImGui.BulletText(Loc.T("Stop when all standard missions are golded"));

                        ImGui.Text(Loc.T("So if you stop and you're unsure why... this might be why"));

                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                bool unsupportedArtisan = false; // xpLeveling && CosmicHelper.CrafterJobList.Contains((uint)Player.Job);
                bool unsupportedMoon = xpLeveling 
                    && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var currentMoon)
                    && !CosmicMoonRegistry.HasLevelingContent(currentMoon);

                // Leveling on a hub requires QuickLevelList entries; gathering still needs route YAML per territory
                using (ImRaii.Disabled(SchedulerMain.State != IceState.Idle || !usingSupportedJob || unsupportedMoon))
                {
                    if (ImGui.Button(Loc.T("Start"), new Vector2(150 * scale, 0)))
                    {
                        SchedulerMain.EnablePlugin();
                    }
                }

                if (unsupportedArtisan)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text(Loc.T("Hey! You need to update artisan to use this mode, please update to at minimum:"));
                        ImGui.Text(Loc.T("4.0.4.29"));
                        ImGui.EndTooltip();
                    }
                }
                else if (unsupportedMoon && CosmicMoonRegistry.TryGetMoon(Player.Territory.RowId, out var unsupportedHub))
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text($"Hey! {unsupportedHub.DisplayName} is not supported for leveling yet.");
                        var missing = new List<string>();
                        if (!CosmicMoonRegistry.HasLevelingContent(unsupportedHub))
                            missing.Add("QuickLevelList missions");
                        if (!CosmicMoonContent.HasGatheringRoutes(unsupportedHub.TerritoryId))
                            missing.Add("gathering routes");
                        if (missing.Count > 0)
                            ImGui.Text($"Still needed: {string.Join(", ", missing)}.");
                        ImGui.EndTooltip();
                    }
                }
                if (!P.AutoHook.UpdatedPlugin() && CosmicMoonRegistry.Auxesia.TerritoryId == Player.Territory.RowId)
                {
                    ImGui.SameLine(0, 10 * scale);
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                    ImGuiEx.Icon(EColor.Red, FontAwesomeIcon.ExclamationTriangle);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Text(Loc.T("Hey! Your version of autohook is not currently supported on this planet"));
                        ImGui.Text(Loc.T("You need to (currently) be on the testing version to be able fish automated here"));
                        ImGui.Text(Loc.T("There will be another warning to pop up if you try and run this still and it selects a fishing mission..."));
                        ImGui.EndTooltip();
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                // 停止ボタンはレベリング装備の購入中(State は Idle のまま)にも押せるようにする(緊急停止)
                using (ImRaii.Disabled(SchedulerMain.State == IceState.Idle && !Task_BuyLevelingGear.Running && !Task_SellLevelingGear.Running))
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1.0f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f)))
                    using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.1f, 0.1f, 1.0f)))
                    {
                        if (ImGui.Button(Loc.T("Stop"), new Vector2(150 * scale, 0)))
                        {
                            SchedulerMain.DisablePlugin();
                        }
                    }
                }

                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);

                if (ImGui.Button(Loc.T("Mission Settings")))
                {
                    ImGui.OpenPopup("Mission Settings: Popup");
                }
                if (ImGui.BeginPopup("Mission Settings: Popup"))
                {
                    // TODO: Mission Settings
                    bool grindAllProvisionals = C.GrindAllProvisionals;
                    if (ImGui.Checkbox(Loc.T("Provisional: Allow All Classes"), ref grindAllProvisionals))
                    {
                        C.GrindAllProvisionals = grindAllProvisionals;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker(Loc.T("Enabling this will show you all weather/timed/sequence missions that you can grind,\n" +
                                       "ON TOP OF doing the normal missions for whichever class you start on.\n" +
                                       "If you just want to focus one specific class, set this to false"));

                    bool allowCriticalsAllClass = C.GrindOffClassRedAlert;
                    if (ImGui.Checkbox(Loc.T("Critical: Allow All Classes"), ref allowCriticalsAllClass))
                    {
                        C.GrindOffClassRedAlert = allowCriticalsAllClass;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker(Loc.T("This will allow you to grind other classes for criticals/red alerts. " +
                        "(So if you're on crp, but a bsm red alert pops up)"));

                    bool removeGold = C.RemoveAfterGold;
                    if (ImGui.Checkbox(Loc.T("Remove Mission Upon Gold Completion"), ref removeGold))
                    {
                        C.RemoveAfterGold = removeGold;
                        C.Save();
                    }
                    using (ImRaii.Disabled(!removeGold))
                    {
                        bool keepARanks = C.KeepARanks;
                        if (ImGui.Checkbox(Loc.T("Keep \"A Rank\" missions and below"), ref keepARanks))
                        {
                            C.KeepARanks = keepARanks;
                            C.Save();
                        }
                    }

                    ImGui.Checkbox(Loc.T("Stop after current mission"), ref Mission_Settings.StopAfterCurrent);
                    bool relicTurnin = C.TurninRelic;
                    if (ImGui.Checkbox(Loc.T("Turnin if relic is complete##RelicTurnin_GeneralSetting"), ref relicTurnin))
                    {
                        C.TurninRelic = relicTurnin;
                        C.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(Loc.T("?"));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(Loc.T("THIS IS YOUR HEADS UP ON HOW THIS WORKS. If I change this in the future, this tooltip will also change.\n" +
                                         "1: This will check for your current CLASS [not menu class, actual current class] for relic turnin.\n" +
                                         "2: The tool being upgraded must not be equipped, so the plugin swaps to another job for the turnin.\n" +
                                         "\t- Uses the job from \"Job Swap Settings\" if it has a gearset, otherwise any other job that has a gearset.\n" +
                                         "3: This will take prio over \"Stop @ Relic Turnin\", in the sense that if you have both enabled, it will turnin vs stop. And continue about it's day\n" +
                                         "4: If you're on a crafting class, it will return you back to the stop you were crafting post turnin. \n" +
                                         "\t- This is optional, you can disable it at your own free will, I just like this so I can just go back to an isolated area of my choosing\n" +
                                         "5: In Relic Grind mode this all happens automatically (see \"Relic Mode: Auto Upgrade Tool\"), even if this box is unchecked."));
                    }

                    ImGui.Separator();
                    bool relic_AllowRedAlert = C.Relic_IncludeCriticals;
                    if (ImGui.Checkbox(Loc.T("Relic Mode: Allow Red Alerts"), ref relic_AllowRedAlert))
                    {
                        C.Relic_IncludeCriticals = relic_AllowRedAlert;
                        C.Save();
                    }

                    bool OnlySelected = C.XPRelicOnlyEnabled;
                    if (ImGui.Checkbox(Loc.T("Relic Mode: Only Enabled"), ref OnlySelected))
                    {
                        C.XPRelicOnlyEnabled = OnlySelected;
                        C.Save();
                    }

                    bool relicAutoUpgrade = C.Relic_AutoUpgradeInRelicMode;
                    if (ImGui.Checkbox(Loc.T("Relic Mode: Auto Upgrade Tool"), ref relicAutoUpgrade))
                    {
                        C.Relic_AutoUpgradeInRelicMode = relicAutoUpgrade;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker(Loc.T("When the tool's analysis reaches the required value: return to the hub, walk to Researchingway,\n" +
                                       "swap to another job (the tool must not be equipped), upgrade the tool, swap back to the original job,\n" +
                                       "equip the best gear (Stylist if installed, otherwise the game's recommended gear) and resume missions.\n" +
                                       "The temporary job comes from \"Job Swap Settings\" if it has a gearset, otherwise any other job with a gearset."));

                    bool relicPrioritizeIncomplete = C.Relic_PrioritizeIncomplete;
                    if (ImGui.Checkbox(Loc.T("Relic Mode: Prioritize Incomplete D-B"), ref relicPrioritizeIncomplete))
                    {
                        C.Relic_PrioritizeIncomplete = relicPrioritizeIncomplete;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker(Loc.T("Take D/C/B rank missions you have never completed before picking by relic exp,\n" +
                                       "so the completion count needed to unlock the next rank keeps growing."));

                    ImGui.Separator();
                    bool autoEquipBest = C.LevelingGear_AutoEquipBest;
                    if (ImGui.Checkbox(Loc.T("Leveling: Auto Equip Best Gear"), ref autoEquipBest))
                    {
                        C.LevelingGear_AutoEquipBest = autoEquipBest;
                        C.Save();
                    }
                    ImGuiEx.HelpMarker(Loc.T("After each leveling mission and after buying leveling gear, equip the best gear\n" +
                                       "(Stylist if installed, otherwise the game's recommended gear) and update the gearset.\n" +
                                       "Turned on automatically when you press \"Buy Leveling Gear\"."));

                    if (ImGui.Button(Loc.T("Open Job Swap Settings")))
                    {
                        C.SelectedTab = WindowSelection.CharacterSettings;
                    }

                    if (ImGui.Button(Loc.T("Save Current Mission Preset")))
                    {
                        ImGui.OpenPopup("Preset Save Editor");
                    }

                    if (ImGui.BeginPopup("Preset Save Editor"))
                    {
                        ImGui.InputText(Loc.T("Playlist Name"), ref newListName);
                        using (ImRaii.Disabled(string.IsNullOrEmpty(newListName)))
                        {
                            if (ImGui.Button(Loc.T("Save New List")))
                            {
                                List<uint> new_Playlist = new();
                                foreach (var mission in C.MissionConfig.Where(x => x.Value.Enabled))
                                {
                                    new_Playlist.Add(mission.Key);
                                }
                                if (C.Mission_Playlist.ContainsKey(newListName))
                                {
                                    C.Mission_Playlist[newListName] = new_Playlist;
                                }
                                else
                                {
                                    C.Mission_Playlist.Add(newListName, new_Playlist);
                                }
                                C.Save();
                                ImGui.CloseCurrentPopup();
                            }
                        }

                        ImGui.EndPopup();
                    }

                    if (C.Mission_Playlist.Count > 0)
                    {
                        if (ImGui.Button(Loc.T("View All Presets")))
                        {
                            ImGui.OpenPopup("Preset: List Viewer");
                        }

                        if (ImGui.BeginPopup("Preset: List Viewer"))
                        {
                            ImGui.Text(Loc.T("Load Mission Preset"));

                            if (ImGui.BeginTable($"Preset: TableViewer", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders))
                            {
                                ImGui.TableSetupColumn(Loc.T("Name"));
                                ImGui.TableSetupColumn(Loc.T("Amount Enabled"));

                                ImGui.TableHeadersRow();

                                ImGui.TableNextRow();
                                ImGui.TableSetColumnIndex(0);
                                ImGui.AlignTextToFramePadding();
                                ImGui.Text(Loc.T("Clear All"));
                                ImGui.SameLine();
                                if (ImGuiEx.IconButton(FontAwesomeIcon.ArrowUpRightFromSquare, $"FreshPreset_Button"))
                                {
                                    foreach (var mission in C.MissionConfig)
                                    {
                                        mission.Value.Enabled = false;
                                    }
                                    C.Save();
                                    ImGui.CloseCurrentPopup();
                                }

                                foreach (var item in C.Mission_Playlist)
                                {
                                    ImGui.TableNextRow();
                                    ImGui.TableSetColumnIndex(0);
                                    ImGui.AlignTextToFramePadding();
                                    ImGui.Text($"{item.Key}");
                                    ImGui.SameLine();
                                    if (ImGuiEx.IconButton(FontAwesomeIcon.ArrowUpRightFromSquare, $"{item.Key}_Button"))
                                    {
                                        foreach (var mission in C.MissionConfig)
                                        {
                                            if (item.Value.Contains(mission.Key))
                                                mission.Value.Enabled = true;
                                            else
                                                mission.Value.Enabled = false;
                                        }
                                        C.Save();
                                        ImGui.CloseCurrentPopup();
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        ImGui.SetTooltip(Loc.T("Import Missions"));
                                    }

                                    ImGui.TableNextColumn();
                                    ImGui.AlignTextToFramePadding();
                                    ImGui.Text($"{item.Value.Count}");

                                    ImGui.TableNextColumn();
                                    if (ImGuiEx.IconButton(FontAwesomeIcon.Trash, $"{item.Key}_Remove"))
                                    {
                                        C.Mission_Playlist.Remove(item);
                                        C.Save();
                                    }
                                    if (ImGui.IsItemHovered())
                                    {
                                        ImGui.SetTooltip(Loc.T("Remove from list"));
                                    }
                                }

                                ImGui.EndTable();
                            }

                            ImGui.EndPopup();
                        }
                    }


                ImGui.EndPopup();
                }

                // レベリング装備の購入: 現在ジョブの Lv10〜95 の装備をゴッドギスから自動購入する(確認ダイアログ付き)
                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                using (ImRaii.Disabled(SchedulerMain.State != IceState.Idle || Task_BuyLevelingGear.Running || !usingSupportedJob))
                {
                    if (ImGui.Button(Loc.T("Buy Leveling Gear")))
                    {
                        // ボタンを押したら最強装備の自動化を ON にする(購入後にすぐ着替えられるように)
                        C.LevelingGear_AutoEquipBest = true;
                        C.Save();
                        Task_BuyLevelingGear.BuildPlan(currentJobId);
                        ImGui.OpenPopup("Buy Leveling Gear: Confirm");
                    }
                }
                if (ImGui.IsItemHovered() && Task_BuyLevelingGear.Running)
                    ImGui.SetTooltip(Loc.T("Purchase in progress"));
                if (ImGui.BeginPopup("Buy Leveling Gear: Confirm"))
                {
                    DrawBuyLevelingGearPopup();
                    ImGui.EndPopup();
                }

                // レベリング装備の売却: ゴッドギスで買える Lv10〜95 の装備(現在ジョブの種類=クラフター/ギャザラー分)の NQ 品をアーマリーから売る
                ImGui.SameLine(0, 10 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + yOffset);
                using (ImRaii.Disabled(SchedulerMain.State != IceState.Idle || Task_BuyLevelingGear.Running || Task_SellLevelingGear.Running || !usingSupportedJob))
                {
                    if (ImGui.Button(Loc.T("Sell Leveling Gear")))
                    {
                        Task_SellLevelingGear.BuildPlan(currentJobId);
                        ImGui.OpenPopup("Sell Leveling Gear: Confirm");
                    }
                }
                if (ImGui.BeginPopup("Sell Leveling Gear: Confirm"))
                {
                    DrawSellLevelingGearPopup();
                    ImGui.EndPopup();
                }
            }

            using (var bodyChild = ImRaii.Child("##modeSelect_Body", new Vector2(0, -1), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (!bodyChild.Success) return;

                float scrollbarSize = ImGui.GetStyle().ScrollbarSize;
                float buttonRowHeight = (ImGui.GetTextLineHeight() + 8 * scale + 4 * scale) + scrollbarSize;

                using (var missionButtons = ImRaii.Child("##tab_scroll", new Vector2(0, buttonRowHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
                {
                    if (!missionButtons.Success)
                        return;

                    ImGui_Ice.DrawRankButton(Loc.T("Red Alert"), MissionFilter.RedAlert, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("Sequence"), MissionFilter.Sequence, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("Weather"), MissionFilter.Weather, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("Timed"), MissionFilter.Timed, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("Master"), MissionFilter.Master, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("A Rank"), MissionFilter.ARank, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("B Rank"), MissionFilter.BRank, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("C Rank"), MissionFilter.CRank, MissionTable);
                    ImGui_Ice.DrawRankButton(Loc.T("D Rank"), MissionFilter.DRank, MissionTable);

                    ImGui_Ice.EndCategoryButtonRow();
                }

                var bottomSpace = ImGui.GetTextLineHeight() + 6f;
                bottomSpace += 12f; // prevent the tabs from creating a scrollbar

                Vector2 size = new(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y - bottomSpace);
                if (ImGui.BeginChild("###MissionTableV3", size, false))
                {
                    try
                    {
                        if (MissionTable == null && CosmicHelper.SheetMissionDict.Count > 0)
                        {
                            foreach (var mission in CosmicHelper.SheetMissionDict)
                            {
                                CosmicHelper.MissionInfo missionDetails = new() { Id = mission.Key };
                                TableItems.Add(missionDetails);
                            }
                            ItemCount = TableItems.Count();
                            MissionTable = new(TableItems);
                        }
                        var filterActive = MissionTable.FilteredItems.Count != 0 && MissionTable.FilteredItems.Count != ItemCount;
                        var filterCount = filterActive ? $" (of {ItemCount})" : "";
                        var height = ImGui.GetFrameHeight();
                        MissionTable.Draw(height + 4f);
                    }
                    catch (Exception ex)
                    {
                        IceLogging.Error(ex.Message, "Drawing Mission Table");
                    }
                }
                ImGui.EndChild();
            }
        }
    }
}
