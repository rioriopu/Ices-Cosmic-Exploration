using Dalamud.Interface.Utility.Raii;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.ImGuiTools;

namespace ICE.Ui.MainUi.Settings;

public static class Settings_TableColumns
{
    private static string[] missionSortOptions = 
        ["Id", "Name", "Cosmo Credits", "Lunar Credits", 
        "Exp I", "Exp II", "Exp III", "Exp IV", "Exp V", 
        "Map Location", "Class Score", "Class Exp"];

    public static void ColumnSettings()
    {
        int missionSelectedOption = C.TableSortOption;
        if (ImGui.BeginCombo(Loc.T("Sort By"), missionSortOptions[missionSelectedOption]))
        {
            for (int i = 0; i < missionSortOptions.Length; i++)
            {
                bool isSelected = (i == missionSelectedOption);
                if (ImGui.Selectable(missionSortOptions[i], isSelected))
                {
                    missionSelectedOption = i;
                }
                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
                if (missionSelectedOption != C.TableSortOption)
                {
                    C.TableSortOption = missionSelectedOption;
                    C.Save();
                }
            }
            ImGui.EndCombo();
        }

        bool hideUnsupported = C.HideUnsupportedMissions;
        if (ImGui.Checkbox(Loc.T("Hide Unsupported Missions"), ref hideUnsupported))
        {
            C.HideUnsupportedMissions = hideUnsupported;
            C.Save();
        }

        bool autoShowToken = C.Auto_ShowTokens;
        if (ImGui.Checkbox(Loc.T("Auto Hide/Show Planet Tokens"), ref autoShowToken))
        {
            C.Auto_ShowTokens = autoShowToken;
            C.Save();
        }

        ImGuiEx.HelpMarker(Loc.T("Only enable this if you want plan on doing missions YOURSELF. AND NOT AUTOMATING IT. " +
                           "Or if you're letting a different plugin do all the automating of turning in, craftings, gathering... and not letting I.C.E. handle interacting with those plugins"));
    }

    private static bool ApplyToAllClasses = true;
    private static bool ApplyToSpecicClass = false;
    private static int SpecificClass = 8;
    private static int selectedClassIndex = 0;

    private static readonly string[] classOptions = new[]
    {
        "Carpenter (CRP)",      // 0
        "Blacksmith (BSM)",     // 1
        "Armorer (ARM)",        // 2
        "Goldsmith (GSM)",      // 3
        "Leatherworker (LTW)",  // 4
        "Weaver (WVR)",         // 5
        "Alchemist (ALC)",      // 6
        "Culinarian (CUL)",     // 7
        "Miner (MIN)",          // 8
        "Botanist (BTN)",       // 9
        "Fisher (FSH)"          // 10
    };

    private static readonly int[] classIds = new[]
    {
        8,  // Carpenter
        9,  // Blacksmith
        10, // Armorer
        11, // Goldsmith
        12, // Leatherworker
        13, // Weaver
        14, // Alchemist
        15, // Culinarian
        16, // Miner
        17, // Botanist
        18  // Fisher
    };

    private static TurninState HighestTurnin = TurninState.Gold;

    public static void GeneralMissionSettings()
    {
        if (ImGui.Button(Loc.T("Quick Apply Turnins")))
        {
            ImGui.OpenPopup("Quick Apply_Mission Turnins");
        }

        if (ImGui.BeginPopup("Quick Apply_Mission Turnins"))
        {
            if (ImGui.RadioButton(Loc.T("Apply to all classes"), ApplyToAllClasses))
            {
                ApplyToAllClasses = true;
                ApplyToSpecicClass = false;
            }

            if (ImGui.RadioButton(Loc.T("Apply to specific class"), ApplyToSpecicClass))
            {
                ApplyToAllClasses = false;
                ApplyToSpecicClass = true;
            }
            if (ImGui.Combo("##ClassSelector", ref selectedClassIndex, classOptions, classOptions.Length))
            {
                // Update SpecificClass when selection changes
                SpecificClass = classIds[selectedClassIndex];
                IceLogging.Debug($"Selected class: {classOptions[selectedClassIndex]}, ID: {SpecificClass}");
            }
            ImGui.Separator();
            ImGui.Text(Loc.T("Select Turnin Options"));
            ImGui.Dummy(new Vector2(0, 2));

            if (ImGui.RadioButton(Loc.T("Gold"), HighestTurnin is TurninState.Gold))
            {
                HighestTurnin = TurninState.Gold;
            }
            if (ImGui.RadioButton(Loc.T("Silver"), HighestTurnin is TurninState.Silver))
            {
                HighestTurnin = TurninState.Silver;
            }
            if (ImGui.RadioButton(Loc.T("Bronze"), HighestTurnin is TurninState.Bronze))
            {
                HighestTurnin = TurninState.Bronze;
            }

            ImGui.Separator();

            if (ImGui.Button(Loc.T("Apply")))
            {
                var amountApplied = 0;
                foreach (var mission in C.MissionConfig)
                {
                    if (CosmicHelper.SheetMissionDict.TryGetValue(mission.Key, out var sheetInfo))
                    {
                        if (ApplyToSpecicClass && !sheetInfo.Jobs.Contains((uint)SpecificClass))
                            continue;

                        if (sheetInfo.Attributes.HasFlag(MissionAttributes.Score_TimeRemaining))
                            continue;

                        if (C.MissionConfig.TryGetValue(mission.Key, out var config))
                        {
                            config.TurninGoal = HighestTurnin;
                        }
                        amountApplied += 1;
                    }
                }
                C.SaveDebounced();

                Notify.Success($"Applied settings to: {amountApplied} missions, just for you buddy.");
                ImGui.CloseCurrentPopup();
            }


            ImGui.EndPopup();
        }
    }
}
