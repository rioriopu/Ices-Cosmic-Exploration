using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Pictomancy;
using System.Numerics;

namespace PictomancyDemo;

internal sealed class VfxTab
{
    private string[]? _omenNames;
    private string[]? _lockonNames;
    private string[]? _channelingNames;
    private string[]? _commonNames;

    private Vector4 _color = Vector4.One;
    private Vector3 _omenScale = new(10f);
    private bool _omenScaleLock = true;

    private string _lastClick = "";

    private static Vector2 ListSize => new(0, ImGui.GetContentRegionAvail().Y);

    public void Draw()
    {
        if (PctService.VfxRenderer is null)
        {
            ImGui.TextUnformatted("VFX renderer is disabled in PctOptions.");
            return;
        }

        var player = DemoPlugin.Objects.LocalPlayer;
        if (player is null)
        {
            ImGui.TextUnformatted("Log into the game to preview VFX.");
            return;
        }

        EnsureNames();

        ImGui.ColorEdit4("Color tint", ref _color, ImGuiColorEditFlags.NoInputs);

        if (!ImGui.BeginTabBar("##vfx_tabs")) return;

        if (ImGui.BeginTabItem("Omen"))
        {
            DrawOmenTab(player);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Lockon"))
        {
            DrawSimpleListTab("lockon_list", _lockonNames, name =>
                PctService.VfxRenderer.AddLockon("preview", name, player, color: _color));
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Channeling (requires target)"))
        {
            DrawSimpleListTab("channeling_list", _channelingNames, name =>
            {
                var target = DemoPlugin.TargetManager.Target;
                if (target is null) return;
                PctService.VfxRenderer.AddChanneling("preview", name, player, target, color: _color);
            });
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Common"))
        {
            DrawSimpleListTab("common_list", _commonNames, name =>
                PctService.VfxRenderer.AddCommon("preview", name, player, color: _color));
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawOmenTab(IPlayerCharacter player)
    {
        ImGui.Checkbox("Lock proportions", ref _omenScaleLock);
        if (_omenScaleLock)
        {
            var scale = _omenScale.X;
            if (ImGui.SliderFloat("Scale", ref scale, 0.1f, 100f))
                _omenScale = new Vector3(scale);
        }
        else
        {
            ImGui.SliderFloat("Width", ref _omenScale.X, 0.1f, 100f);
            ImGui.SliderFloat("Length", ref _omenScale.Z, 0.1f, 100f);
            ImGui.SliderFloat("Height", ref _omenScale.Y, 0.1f, 100f);
        }

        if (!ImGui.BeginTable("omen_list", 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY, ListSize))
            return;
        ImGui.TableSetupColumn("Core", ImGuiTableColumnFlags.WidthStretch);

        foreach (var name in _omenNames!)
        {
            if (string.IsNullOrEmpty(name)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            bool pop = false;
            if (name == _lastClick)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0f, 1f, 1f));
                pop = true;
            }
            if (ImGui.Button(name))
            {
                ImGui.SetClipboardText(name);
                _lastClick = _lastClick == name ? "" : name;
            }
            if (pop) ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                PctService.VfxRenderer.AddOmen("preview", name, player.Position, _omenScale, player.Rotation, _color);
            }
            else if (_lastClick != "")
            {
                PctService.VfxRenderer.AddOmen("preview", _lastClick, player.Position, _omenScale, player.Rotation, _color);
            }
        }
        ImGui.EndTable();
    }

    private static void DrawSimpleListTab(string tableId, string[]? names, Action<string> onHover)
    {
        if (names is null) return;
        if (!ImGui.BeginTable(tableId, 1, ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY, ListSize))
            return;
        ImGui.TableSetupColumn("Core", ImGuiTableColumnFlags.WidthStretch);

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Button(name))
                ImGui.SetClipboardText(name);
            if (ImGui.IsItemHovered())
                onHover(name);
        }
        ImGui.EndTable();
    }

    private void EnsureNames()
    {
        if (_omenNames is not null) return;
        var data = DemoPlugin.DataManager;
        _omenNames = data.GetExcelSheet<Lumina.Excel.Sheets.Omen>()
            .Select(x => x.Path.ExtractText())
            .Where(x => !string.IsNullOrEmpty(x))
            .Order().Distinct().ToArray();
        _lockonNames = data.GetExcelSheet<Lumina.Excel.Sheets.Lockon>()
            .Select(x => x.IconName.ExtractText())
            .Where(x => !string.IsNullOrEmpty(x))
            .Order().Distinct().ToArray();
        _channelingNames = data.GetExcelSheet<Lumina.Excel.Sheets.Channeling>()
            .Select(x => x.File.ExtractText())
            .Where(x => !string.IsNullOrEmpty(x))
            .Order().Distinct().ToArray();
        _commonNames = data.GetExcelSheet<Lumina.Excel.Sheets.VFX>()
            .Select(x => x.Location.ExtractText())
            .Where(x => !string.IsNullOrEmpty(x))
            .Order().Distinct().ToArray();
    }
}
