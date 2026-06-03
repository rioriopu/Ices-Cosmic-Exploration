using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using System.Collections.Generic;

namespace ICE.Ui.DebugWindowTabs
{
    internal class Ui_RelicInfo
    {
        public static List<string> XPtypes = ["I", "II", "III", "IV", "V", "VI", "VII"];  // 2026.05.25でⅦ追加
        public static List<(string Name, uint Id)> jobOptions = new()
        {
            ("CRP", 8),
            ("BSM", 9),
            ("ARM", 10),
            ("GSM", 11),
            ("LTW", 12),
            ("WVR", 13),
            ("ALC", 14),
            ("CUL", 15),
            ("MIN", 16),
            ("BTN", 17),
            ("FSH", 18),
        };

        public static unsafe void Draw()
        {
            var wksManager = WKSManager.Instance();
            if (wksManager == null || wksManager->ResearchModule == null || !wksManager->ResearchModule->IsLoaded)
                return;

            // 列数 = Class + Stage + (タイプ数 × Current/Need/Max) + Score。XPtypes(=Ⅶまで)に追従させて整合を保つ
            int totalColumns = 2 + XPtypes.Count * 3 + 1;
            if (ImGui.BeginTable("Relic Info", totalColumns, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
            {
                ImGui.TableSetupColumn("Class");
                ImGui.TableSetupColumn("Stage");
                for (int i = 0; i < XPtypes.Count; i++)
                {
                    ImGui.TableSetupColumn($"{XPtypes[i]} - Current");
                    ImGui.TableSetupColumn($"{XPtypes[i]} - Need");
                    ImGui.TableSetupColumn($"{XPtypes[i]} - Max");
                }
                ImGui.TableSetupColumn("Score");

                ImGui.TableHeadersRow();

                foreach (var job in jobOptions)
                {
                    ImGui.TableNextRow();

                    // Class Name
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(job.Name);

                    ImGui.TableNextColumn();
                    var toolClassId = (byte)(job.Id - 7);
                    var stage = wksManager->ResearchModule->CurrentStages[toolClassId - 1];
                    ImGui.TextUnformatted(stage.ToString());

                    for (byte type = 1; type <= XPtypes.Count; type++)  // 4固定→XPtypes.Count(Ⅶまで)
                    {
                        if (!wksManager->ResearchModule->IsTypeAvailable(toolClassId, type))
                            break;

                        var neededXP = wksManager->ResearchModule->GetNeededAnalysis(toolClassId, type);

                        var maxXP = wksManager->ResearchModule->GetMaxAnalysis(toolClassId, type);

                        var currentXp = wksManager->ResearchModule->GetCurrentAnalysis(toolClassId, type);

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{currentXp}");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{neededXP}");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{maxXP}");
                    }

                    ImGui.TableSetColumnIndex(2 + XPtypes.Count * 3);  // Score列(最終列)。14固定→タイプ数追従
                    var scores = wksManager->State.Scores;
                    int classScore = scores[(int)job.Id - 8];

                    ImGui.TextUnformatted($"{classScore}");
                }

                ImGui.EndTable();
            }
        }
    }
}
