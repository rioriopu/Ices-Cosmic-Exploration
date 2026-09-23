using Dalamud.Interface;
using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.Ui.Waypoint_Manager;

public static class WaypointUi
{
    private static readonly PathManager _pathManager = new();
    private static string _currentPathName = string.Empty;
    private static PathFile? _currentPathFile = null;
    private static int _selectedPathIndex = -1;

    private static Vector3 _newWaypoint = Vector3.Zero;
    private static bool _newJumpFlag = false;

    public static void WPUi()
    {
        DrawPathSelector();

        if (_currentPathFile != null)
        {
            ImGui.Separator();
            DrawWaypointList();
            DrawAddWaypointSection();
            DrawSaveSection();
        }
    }

    private static string _newFileName = "new_path";

    private static void DrawPathSelector()
    {
        var paths = _pathManager.ListAllPaths();
        if (paths.Count == 0)
        {
            ImGui.Text(Loc.T("No path files found."));
            ImGui.InputText(Loc.T("New Path Name"), ref _newFileName, 64);
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Create New Path")))
            {
                _currentPathName = _newFileName;
                _currentPathFile = new PathFile { PathName = _currentPathName };
                _pathManager.Save(_currentPathFile);
                _selectedPathIndex = -1;
            }
            return;
        }

        if (_selectedPathIndex >= paths.Count)
            _selectedPathIndex = 0;

        if (ImGui.BeginCombo(Loc.T("Path File"), _selectedPathIndex >= 0 ? paths[_selectedPathIndex] : "Select..."))
        {
            for (int i = 0; i < paths.Count; i++)
            {
                bool isSelected = i == _selectedPathIndex;
                if (ImGui.Selectable(paths[i], isSelected))
                {
                    _selectedPathIndex = i;
                    _currentPathName = paths[i].Replace("path_", "");
                    _currentPathFile = _pathManager.Load(_currentPathName);
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Delete")))
        {
            _pathManager.Delete(_currentPathName);
        }

        ImGui.InputText(Loc.T("New Path Name"), ref _newFileName, 64);
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Create New Path")))
        {
            _currentPathName = _newFileName;
            _currentPathFile = new PathFile { PathName = _currentPathName };
            _pathManager.Save(_currentPathFile);
            _selectedPathIndex = -1;
        }
    }

    private static void DrawWaypointList()
    {
        ImGui.Text($"Waypoints in {_currentPathFile!.PathName}");

        if (_currentPathFile.Waypoints.Count > 0)
        {
            if (ImGui.Button(Loc.T("Test Route")))
            {
                Vector3[] waypoints = _currentPathFile.Waypoints.Select(wp => wp.ToVector3()).ToArray();

                IceLogging.DestinationLogs.Log(waypoints.Last());
                P.Navmesh.MoveTo(new System.Collections.Generic.List<Vector3>(waypoints), false);
            }
        }

        for (int i = 0; i < _currentPathFile.Waypoints.Count; i++)
        {
            var wp = _currentPathFile.Waypoints[i];

            ImGui.PushID(i);

            ImGui.AlignTextToFramePadding();
            ImGui.Text($"[{i}] X:{wp.X:0.0} Y:{wp.Y:0.0} Z:{wp.Z:0.0}");
            ImGui.SameLine();
            bool jump = wp.Jump;
            if (ImGui.Checkbox(Loc.T("Jump"), ref jump))
            {
                wp.Jump = jump;
                _currentPathFile.Waypoints[i] = wp; // write back
            }
            ImGui.SameLine();

            if (i > 0 && ImGui.Button(Loc.T("↑")))
            {
                (_currentPathFile.Waypoints[i], _currentPathFile.Waypoints[i - 1]) =
                    (_currentPathFile.Waypoints[i - 1], _currentPathFile.Waypoints[i]);
            }
            ImGui.SameLine();
            if (i < _currentPathFile.Waypoints.Count - 1 && ImGui.Button(Loc.T("↓")))
            {
                (_currentPathFile.Waypoints[i], _currentPathFile.Waypoints[i + 1]) =
                    (_currentPathFile.Waypoints[i + 1], _currentPathFile.Waypoints[i]);
            }
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button(FontAwesome.Trash))
            {
                _currentPathFile.Waypoints.RemoveAt(i);
            }
            ImGui.PopFont();

            ImGui.PopID();
        }
    }

    private static void DrawAddWaypointSection()
    {
        ImGui.Separator();
        ImGui.Text(Loc.T("Add New Waypoint:"));

        if (ImGui.Button(Loc.T("Add Current POS")))
        {
            _newWaypoint.X = Player.Position.X;
            _newWaypoint.Y = Player.Position.Y;
            _newWaypoint.Z = Player.Position.Z;

            _currentPathFile!.Waypoints.Add(WaypointUtil.FromVector3(_newWaypoint, _newJumpFlag));
            _newWaypoint = Vector3.Zero;
            _newJumpFlag = false;
        }
        ImGui.SetNextItemWidth(75);
        ImGui.InputFloat(Loc.T("X"), ref _newWaypoint.X);
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Set X")))
        {
            _newWaypoint.X = Player.Position.X;
        }
        ImGui.SetNextItemWidth(75);
        ImGui.InputFloat(Loc.T("Y"), ref _newWaypoint.Y);
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Set Y")))
        {
            _newWaypoint.Y = Player.Position.Y;
        }
        ImGui.SetNextItemWidth(75);
        ImGui.InputFloat(Loc.T("Z"), ref _newWaypoint.Z);
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Set Z")))
        {
            _newWaypoint.Z = Player.Position.Z;
        }
        ImGui.Checkbox(Loc.T("Jump"), ref _newJumpFlag);

        if (ImGui.Button(Loc.T("Add Waypoint")))
        {
            _currentPathFile!.Waypoints.Add(WaypointUtil.FromVector3(_newWaypoint, _newJumpFlag));
            _newWaypoint = Vector3.Zero;
            _newJumpFlag = false;
        }
    }

    private static void DrawSaveSection()
    {
        ImGui.Separator();
        if (ImGui.Button(Loc.T("Save Path File")))
        {
            _pathManager.Save(_currentPathFile!);
        }
    }
}
