using Lumina.Excel.Sheets;

namespace ICE.Ui.Debug_Tabs.Debug_Tables
{
    internal class Table_TimeWeather
    {
        public static unsafe void Draw()
        {
            var timeSheet = Svc.Data.GetExcelSheet<WKSMissionLotterySpecialCond>();

            if (ImGui.BeginTable($"WKSMission Time Sheet", 4, ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn(Loc.T("Key"));
                ImGui.TableSetupColumn(Loc.T("Weather Required"));
                ImGui.TableSetupColumn(Loc.T("Start Hour"));
                ImGui.TableSetupColumn(Loc.T("End Hour"));

                ImGui.TableHeadersRow();

                foreach (var entry in timeSheet)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text($"{entry.RowId}");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{entry.WeatherRequired.Value.Name}"); // Unknown 0

                    ImGui.TableNextColumn();
                    ImGui.Text($"{entry.StartTimeHour}"); // Unknown 1

                    ImGui.TableNextColumn();
                    ImGui.Text($"{entry.EndTimeHour}"); // Unknown 2

                }

                ImGui.EndTable();
            }
        }
    }
}
