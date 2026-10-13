using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace ICE.Utilities.ImGuiTools;

public static partial class ImGui_Ice
{
    /// <summary>Patreon のブランド色(コーラル)。支援まわりのボタンで使う。</summary>
    public static readonly Vector4 PatreonColor = new(1.0f, 0.259f, 0.302f, 1.0f);

    /// <summary>
    /// サイドバー用の目立つボタン。見出し(Sidebar_CollaspableHeader)と同じ角丸・高さで、背景を指定色で塗り、アイコンと文字を中央に置く。
    /// selected のときは白い枠を付ける(そのページを開いている印)。
    /// </summary>
    public static bool Sidebar_AccentButton(string id, string label, FontAwesomeIcon icon, Vector4 color, bool selected = false)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 32 * scale);
        var max = min + size;

        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        // 押している間は少し暗く、乗せている間は少し明るく
        var fill = held ? Shade(color, -0.10f) : hovered ? Shade(color, 0.10f) : color;
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 5.0f * scale);
        if (selected)
            drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.85f)), 5.0f * scale, ImDrawFlags.None, 2.0f * scale);

        // アイコンと文字をまとめて中央に
        var white = ImGui.GetColorU32(new Vector4(1, 1, 1, 1));
        var iconText = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);
        ImGui.PopFont();
        var textSize = ImGui.CalcTextSize(label);
        float gap = 6 * scale;
        float startX = min.X + (size.X - (iconSize.X + gap + textSize.X)) / 2;

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(new Vector2(startX, min.Y + (size.Y - iconSize.Y) / 2), white, iconText);
        ImGui.PopFont();
        drawList.AddText(new Vector2(startX + iconSize.X + gap, min.Y + (size.Y - textSize.Y) / 2), white, label);

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        ImGui.Spacing();
        return clicked;
    }

    private static Vector4 Shade(Vector4 color, float amount) =>
        new(Math.Clamp(color.X + amount, 0, 1), Math.Clamp(color.Y + amount, 0, 1), Math.Clamp(color.Z + amount, 0, 1), color.W);
}
