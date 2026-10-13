using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.ImGuiTools;

namespace ICE.Ui.MainUi.HelpFolder
{
    /// <summary>
    /// Estell 版 ICE への支援(Patreon)のページ。サイドバー上部のボタンとタイトルバーのボタンから開く。
    /// 支援は外部サービス(Patreon)で行い、ICE は決済に一切関与しない。
    /// </summary>
    internal static class helpSelect_Support
    {
        /// <summary>支援ページ(Estell の Patreon)。</summary>
        public const string PatreonUrl = "https://www.patreon.com/c/SuppotToEstell";

        private static DateTime copiedAt = DateTime.MinValue;

        public static void Draw()
        {
            float scale = ImGuiHelpers.GlobalScale;

            ImGui.Dummy(new Vector2(0, 4 * scale));
            using (ImRaii.PushColor(ImGuiCol.Text, ImGui_Ice.PatreonColor))
                ImGuiEx.IconWithText(FontAwesomeIcon.HandHoldingHeart, Loc.T("Support ICE (forked by Estell)"));
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 4 * scale));

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.5f, 1f));
            ImGui.TextWrapped(Loc.T("Thank you for using ICE (forked by Estell)."));
            ImGui.PopStyleColor();
            ImGui.Dummy(new Vector2(0, 4 * scale));

            ImGui.TextWrapped(Loc.T("This fork builds on the original ICE with Japanese support, updates for each FFXIV patch, and additional automation.\nYour support goes toward new features, bug fixes, and keeping up with FFXIV patches."));
            ImGui.Dummy(new Vector2(0, 4 * scale));

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f, 1f, 0.85f, 1f));
            ImGui.TextWrapped(Loc.T("Thank you for your continued support."));
            ImGui.PopStyleColor();

            ImGui.Dummy(new Vector2(0, 8 * scale));
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 8 * scale));

            // Patreon を開くボタン(ブランド色)
            using (ImRaii.PushColor(ImGuiCol.Button, ImGui_Ice.PatreonColor)
                .Push(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.36f, 0.40f, 1.0f))
                .Push(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.20f, 0.24f, 1.0f))
                .Push(ImGuiCol.Text, new Vector4(1, 1, 1, 1)))
            {
                if (ImGuiEx.IconButtonWithText(FontAwesomeIcon.HandHoldingHeart, Loc.T("Support on Patreon"), size: new Vector2(240 * scale, 36 * scale)))
                    OpenPatreon();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Opens the Patreon page in your browser."));

            ImGui.SameLine();
            if (ImGuiEx.IconButtonWithText(FontAwesomeIcon.Copy, Loc.T("Copy URL"), size: new Vector2(160 * scale, 36 * scale)))
            {
                ImGui.SetClipboardText(PatreonUrl);
                copiedAt = DateTime.Now;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.T("Copies the Patreon URL to the clipboard."));

            ImGui.Dummy(new Vector2(0, 4 * scale));
            ImGui.TextDisabled(PatreonUrl);
            if ((DateTime.Now - copiedAt).TotalSeconds < 2)
            {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.HealerGreen, Loc.T("Copied!"));
            }

            ImGui.Dummy(new Vector2(0, 8 * scale));
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 4 * scale));

            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudGrey);
            ImGui.TextWrapped(Loc.T("* Patreon is an external service. ICE is not involved in any payment."));
            ImGui.TextWrapped(Loc.T("* To support the original author of ICE, use the heart (Ko-fi) button on the title bar."));
            ImGui.PopStyleColor();
        }

        /// <summary>ブラウザで Patreon のページを開く。</summary>
        public static void OpenPatreon()
        {
            try
            {
                GenericHelpers.ShellStart(PatreonUrl);
            }
            catch (Exception ex)
            {
                IceLogging.Warning($"支援ページを開けませんでした: {ex.Message}", "[Support]");
            }
        }
    }
}
