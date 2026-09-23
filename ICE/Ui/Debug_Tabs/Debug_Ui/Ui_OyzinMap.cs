using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Scheduler.Handlers.PictoStuff;
using Lumina.Excel.Sheets;
using Pictomancy;
using SharpDX.Direct2D1.Effects;
using System;
using System.Collections.Generic;
using System.Text;

namespace ICE.Ui.Debug_Tabs.Debug_Ui
{
    internal class Ui_OyzinMap
    {
        public static unsafe void Draw()
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null)
            {
                ImGui.Text(Loc.T("AgentMap is null!"));
                return;
            }

            if (ImGui.CollapsingHeader(Loc.T("Event Markers")))
            {
                DrawEventMarkersTable(GetAllEventMarkers());
            }
        }

        // Event Markers
        public unsafe static List<TempMapMarkerData> GetAllEventMarkers()
        {
            var markers = new List<TempMapMarkerData>();
            var agentMap = AgentMap.Instance();

            if (agentMap == null) return markers;

            // Event markers (what you already have)
            foreach (var marker in agentMap->EventMarkers)
            {
                markers.Add(new TempMapMarkerData
                {
                    Position = marker.Position,
                    IconId = marker.IconId,
                });
            }

            /*
            // Temp markers (player-placed map markers)
            foreach (var marker in agentMap->TempMapMarkers)
            {
                Vector3 pos = new(marker.MapMarker.X, 0, marker.MapMarker.Y);

                markers.Add(new TempMapMarkerData
                {
                    Position = pos,
                    IconId = marker.MapMarker.IconId,
                });
            }
            */

            // Mini map markers
            foreach (var marker in agentMap->MiniMapMarkers)
            {
                Vector3 pos = new(marker.MapMarker.X, 0, marker.MapMarker.Y);
                markers.Add(new TempMapMarkerData
                {
                    Position = pos,
                    IconId = marker.MapMarker.IconId,
                });
            }

            return markers;
        }

        private static void DrawEventMarkersTable(List<TempMapMarkerData> markers)
        {
            if (markers.Count == 0)
            {
                ImGui.Text(Loc.T("No markers found!"));
                return;
            }

            if (ImGui.Button(Loc.T("Stop Current Task")))
            {
                P.TaskManager.AbortCurrent();
                P.Navmesh.Stop();
            }

            ImGui.SameLine();
            ImGui.Text($"Player Moving: {Player.IsMoving}");

            if (ImGui.BeginTable("Event Markers", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn(Loc.T("Position"));
                ImGui.TableSetupColumn(Loc.T("Icon ID"));
                ImGui.TableSetupColumn(Loc.T("Territory"));
                ImGui.TableSetupColumn(Loc.T("SubLevel"));
                ImGui.TableHeadersRow();

                foreach (var marker in markers)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text($"({marker.Position.X:F2}, {marker.Position.Y:F2}, {marker.Position.Z:F2})");

                    ImGui.TableNextColumn();
                    ImGui.Text(marker.IconId.ToString());
                    if (Svc.Texture.TryGetFromGameIcon(marker.IconId, out var texture))
                    {
                        ImGui.SameLine();
                        ImGui.Image(texture.GetWrapOrEmpty().Handle, new Vector2(24, 24));
                    }
                    ImGui.TableNextColumn();
                    if (ImGui.Button($"Move to##{marker.Position:N2}"))
                    {
                        P.TaskManager.Enqueue(() => Task_NavmeshMove.Task_NavTo(marker.Position), "Marker move task");
                    }


                    if (marker.IconId == 63989)
                    {
                        PictoManager.DrawArrow(marker.Position, 0x80FF0000, 0xFFFFFFFF);
                    }
                }

                ImGui.EndTable();
            }
        }

        public class TempMapMarkerData
        {
            public Vector3 Position { get; set; }
            public uint IconId { get; set; }
        }
    }
}