using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ICE.Scheduler.Tasks;
using ICE.Utilities.ImGuiTools;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.Ui.MainUi.Settings
{
    internal class Shop_Dronebit
    {
        public static void Draw()
        {
            if (ImGui.Button(Loc.T("Run Drone Finder")))
            {
                SchedulerMain.State = IceState.ArtifactSearch;
            }

            if (ImGui.Button(Loc.T("Stop")))
            {
                SchedulerMain.DisablePlugin();
            }

            bool buyDrones = C.Cosmodrone_Buy;
            if (ImGui.Checkbox(Loc.T("Buy Drones"), ref buyDrones))
            {
                C.Cosmodrone_Buy = buyDrones;
                C.Save();
            }
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle, 
                Loc.T("Do you want to buy drones? If yes, enable this")
                );

            int drone_buyAtAmount = C.Cosmodrone_BuyAt;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt(Loc.T("Buy At Amount"), ref drone_buyAtAmount, 200, 5000))
            {
                drone_buyAtAmount = (int)Math.Round(drone_buyAtAmount / 200.0) * 200;
                C.Cosmodrone_BuyAt = drone_buyAtAmount;
                C.SaveDebounced();
            }
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle, 
                Loc.T("When do you wanna buy drones from the vendor?\n" +
                "Set in incriments of 200, max of 5,000")
                );

            int maxCrateAmount = C.Cosmodrone_MaxKeep;
            ImGui.SetNextItemWidth(200);
            if (ImGui.InputInt(Loc.T("Maximum Drones"), ref maxCrateAmount))
            {
                if (maxCrateAmount < 0)
                    maxCrateAmount = 0;
                C.Cosmodrone_MaxKeep = maxCrateAmount;
                C.SaveDebounced();
            }
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle,
                Loc.T("What's the maximum amount of drones you wanna keep?\n" +
                "0 = will just keep buying\n" +
                "Anything above 0 will just be a hard cap and will stop buying if it reaches this")
                );

            bool runDroneFinder = C.Cosmodrone_Run;
            if (ImGui.Checkbox(Loc.T("Automate cosmodrone"), ref runDroneFinder))
            {
                C.Cosmodrone_Run = runDroneFinder;
                C.Save();
            }
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle,
                Loc.T("Do you want to run the automated drone finding? If yes, enable this\n" +
                "PLEASE NOTE. DO. NOT. LEAVE. THIS. ALONE. This is still being worked on heavily"));

            // 探索後の自動鑑定(エネルギーパックを使い切ったあと、カエデで「古代の記録」を鑑定する)
            bool autoAppraise = C.Cosmodrone_AutoAppraise;
            if (ImGui.Checkbox(Loc.T("Appraise Ancient Records after the search"), ref autoAppraise))
            {
                C.Cosmodrone_AutoAppraise = autoAppraise;
                C.Save();
            }
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle,
                Loc.T("When enabled, once every energy pack has been used the plugin goes to the drone NPC (Kaede) and appraises all the Ancient Records.\n" +
                "When disabled, it skips the appraisal and only returns to the hub with Stellar Return."));

            // 手動の連続鑑定: カエデへ向かい、鑑定を連続で行う。停止ボタンで途中で止められる。
            bool canAppraise = Task_ArtifactSearch.CanAppraiseHere();
            bool appraising = SchedulerMain.State == IceState.Appraisal;
            // StartManualAppraisal と同じ条件(Idle かつタスク無し)でなければ押せないようにする
            bool busy = SchedulerMain.State != IceState.Idle || P.TaskManager.NumQueuedTasks > 0;
            using (ImRaii.Disabled(!canAppraise || busy))
            {
                if (ImGui.Button(Loc.T("Appraise now")))
                    Task_ArtifactSearch.StartManualAppraisal();
            }
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Stop appraisal")))
                Task_ArtifactSearch.StopAppraisal();
            ImGui_Ice.IconWithTooltip(FontAwesomeIcon.QuestionCircle,
                Loc.T("Appraise now: walks to the drone NPC (Kaede), talks to it and appraises the Ancient Records one after another.\n" +
                "Stop appraisal: aborts the appraisal (and anything else ICE is doing) and closes the appraisal windows."));
            if (appraising)
                ImGui.TextColored(ImGuiColors.HealerGreen, Loc.T("Appraising..."));
            else if (!canAppraise)
                ImGui.TextDisabled(Loc.T("Available only on planets that have a drone NPC (Oizys / Auxesia)."));
            else if (busy)
                ImGui.TextDisabled(Loc.T("ICE is busy. Stop the current run before starting the appraisal."));
        }
    }
}
