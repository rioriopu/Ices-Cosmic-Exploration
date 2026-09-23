using Dalamud.Interface.Utility.Raii;
using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;
using System.Collections.Generic;
using static ICE.Utilities.LevelingGearShop;

namespace ICE.Ui.Debug_Tabs.Debug_Ui
{
    /// <summary>
    /// レベリング装備ベンダー(ゴッドギス)の品揃えを確認するデバッグ画面。
    /// ゲームデータからの静的解決、開いているショップからの実測取り込み、所持状況の更新、JSON 書き出しを行う。
    /// </summary>
    internal class Ui_LevelingGearShop
    {
        private static int _npcIdInput = 0;
        private static bool _onlyCurrentJob = true;
        private static bool _onlyMissing = false;

        public static void Draw()
        {
            uint vendor = CurrentVendorNpcId();
            if (_npcIdInput == 0 && vendor != 0)
                _npcIdInput = (int)vendor;

            ImGui.Text($"Territory: {Player.Territory.RowId} | Vendor NPC (NpcData Repair): {vendor}");
            ImGui.SetNextItemWidth(160);
            ImGui.InputInt("NPC Id##lgs_npc", ref _npcIdInput);
            ImGui.SameLine();
            if (ImGui.Button("Resolve from sheets"))
                Resolve((uint)_npcIdInput, force: true);
            ImGui.SameLine();
            if (ImGui.Button("Capture open shop"))
            {
                var d = Resolve((uint)_npcIdInput);
                if (CaptureOpenShop(d) < 0)
                    IceLogging.Info("ショップが開いていません(ゴッドギスに話しかけて購入画面を開いてから押してください)", "[LevelingGearShop]");
            }
            ImGui.SameLine();
            if (ImGui.Button("Refresh ownership") && TryGetCached((uint)_npcIdInput, out var cached))
                UpdateOwnership(cached);
            ImGui.SameLine();
            if (ImGui.Button("Export JSON") && TryGetCached((uint)_npcIdInput, out var toExport))
                ExportJson(toExport);

            ImGui.Checkbox("Only current job", ref _onlyCurrentJob);
            ImGui.SameLine();
            ImGui.Checkbox("Only not owned", ref _onlyMissing);
            if (!string.IsNullOrEmpty(LastMessage))
                ImGui.TextDisabled(LastMessage);

            if (!TryGetCached((uint)_npcIdInput, out var data))
            {
                ImGui.TextWrapped("「Resolve from sheets」でゲームデータから品揃えを取得します。NPC に話しかける必要はありません。");
                return;
            }

            uint job = (uint)Player.Job;
            var items = data.AllItems
                .Where(x => !_onlyCurrentJob || (x.Jobs.Contains(job) && KindMatchesJob(x.Kind, job)))
                .Where(x => !_onlyMissing || (x.ArmouryCount == 0 && !x.IsEquipped))
                .OrderBy(x => x.LevelEquip).ThenBy(x => x.Slot).ThenBy(x => x.ItemId)
                .ToList();

            ImGui.Text($"{data.NpcName}[{data.NpcId}] shops={data.Shops.Count} items={data.AllItems.Count()} (filtered {items.Count}) resolved {data.ResolvedAt:HH:mm:ss}");
            foreach (var shop in data.Shops)
                ImGui.BulletText($"[{shop.ShopId}] '{shop.Name}' {shop.Items.Count} items — menu#{shop.MenuIndex} shop#{shop.ShopIndex} — {shop.Path}");

            // 部位ごとの空き枠(購入可否判定の材料)
            var slots = new List<GearSlot> { GearSlot.MainHand, GearSlot.OffHand, GearSlot.Head, GearSlot.Body, GearSlot.Hands, GearSlot.Legs, GearSlot.Feet, GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.Ring };
            ImGui.TextDisabled("Armoury free slots: " + string.Join(" | ", slots.Select(s => $"{SlotNameJp(s)}:{ArmouryFreeSlots(s)}")));

            using var table = ImRaii.Table("LevelingGearShopTable", 10, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY);
            if (!table.Success) return;
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Bracket");
            ImGui.TableSetupColumn("Shop");
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Lv");
            ImGui.TableSetupColumn("iLv");
            ImGui.TableSetupColumn("Slot");
            ImGui.TableSetupColumn("Jobs");
            ImGui.TableSetupColumn("Price");
            ImGui.TableSetupColumn("Armoury");
            ImGui.TableSetupColumn("Equipped");
            ImGui.TableHeadersRow();

            foreach (var item in items)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(item.Bracket);
                ImGui.TableNextColumn(); ImGui.Text(string.IsNullOrEmpty(item.ShopName) ? item.ShopId.ToString() : item.ShopName);
                ImGui.TableNextColumn(); ImGui.Text($"{item.Name}{(item.IsHQ ? " (HQ)" : "")} [{item.ItemId}]");
                ImGui.TableNextColumn(); ImGui.Text(item.LevelEquip.ToString());
                ImGui.TableNextColumn(); ImGui.Text(item.ItemLevel.ToString());
                ImGui.TableNextColumn(); ImGui.Text($"{item.Slot} ({SlotNameJp(item.Slot)}) {item.Kind}");
                ImGui.TableNextColumn(); ImGui.Text(string.Join(",", item.Jobs.Select(j => CosmicHelper.GetJobName(j))));
                ImGui.TableNextColumn(); ImGui.Text(item.RuntimePrice >= 0 ? $"{item.RuntimePrice:N0} (sheet {item.Price:N0})" : $"{item.Price:N0}");
                ImGui.TableNextColumn(); ImGui.Text(item.ArmouryCount.ToString());
                ImGui.TableNextColumn(); ImGui.Text(item.IsEquipped ? "yes" : "");
            }
        }
    }
}
