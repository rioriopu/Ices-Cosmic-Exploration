using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using System;
using System.Collections.Generic;
using System.Text;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.Ui.DebugWindowTabs.Ui_OyzinMap;

namespace ICE.Scheduler.Tasks
{
    internal class Task_ArtifactSearch
    {
        public static void EnqueueBuy()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(Drone_PathToVendor, "Drone NPC: Path to"),
                    new(TalkToDroneNpc, "Drone NPC: Talk"),
                    new(SelectShop, "Drone NPC: Select Shop"),
                    new(BuyDroneCrates, "Drone NPC: Buying"),
                    new(ExitDroneShop, "Drone NPC: Leaving Shop")
                );
        }
        private static bool? Drone_PathToVendor()
        {
            string handle = "[Task_Artifact: PathTo]";
            var zoneId = Player.Territory.RowId;

            if (NpcData.MoonNpcs[zoneId].TryGetValue(NpcData.NpcType.Drone, out var npcEntry))
            {
                Vector3 randomPos = NpcData.GetRandomPointInCircle(npcEntry.Location_Circle, 0.5f);
                if (!Task_NavmeshMove.Task_NavTo(randomPos, distance: 6, npcLoc: npcEntry.Location_Npc).Value)
                {
                    if (EzThrottler.Throttle("Drone Move Message", 1000))
                        IceLogging.Verbose($"Pathing to drone NPC. Current distance: {Player.DistanceTo(npcEntry.Location_Npc)}", handle);
                }
                else
                {
                    IceLogging.Debug("We're close enough to the repair npc! Continuing on", handle);
                    return true;
                }
            }
            else
            {
                if (EzThrottler.Throttle("Error message: NPC", 5000))
                    IceLogging.Error("Hey! We don't have this npc coded yet, which means I forgot bout it, could you let me know\n" +
                                     $"Planet Territory ID: {Player.Territory.RowId}", handle);
            }
            return false;
        }
        private static bool? TalkToDroneNpc()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var iconString) && iconString.IsAddonReady)
            {
                IceLogging.Info("Icon string is visible! Time to shop");
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<Talk>("Talk", out var talk) && talk.IsAddonReady)
            {
                if (EzThrottler.Throttle("Throttle talking", 100))
                    talk.Click();
            }
            else
            {
                if (NpcData.MoonNpcs[Player.Territory.RowId].TryGetValue(NpcData.NpcType.Drone, out var droneInfo))
                {
                    Utils.TryGetObjectByDataId(droneInfo.NpcId, out var droneNpc);
                    if (EzThrottler.Throttle("Interacting with researchingway"))
                    {
                        Utils.TargetgameObject(droneNpc);
                        Utils.InteractWithObject(droneNpc);
                    }
                }
            }

            return false;
        }
        private static bool? SelectShop()
        {
            if (GenericHelpers.TryGetAddonMaster<ShopExchangeCurrency>("ShopExchangeCurrency", out var shopExchange) && shopExchange.IsAddonReady)
            {
                IceLogging.Debug("Shop Exchange Currency Addon is Ready!");
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var selectString) && selectString.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting Materia Selection"))
                {
                    var select = selectString.Entries[0];
                    IceLogging.Verbose($"Selecting: {select.Text}");
                    select.Select();
                }
            }

            return false;
        }
        private static bool? BuyDroneCrates()
        {
            string tag = "[Task_ArtifactSearch: Buy Drones]";

            if (EzThrottler.Throttle("General Buying Throttle"))
            {
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var YesNo) && YesNo.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Buy Drone Boxes"))
                    {
                        YesNo.Yes();
                    }
                }
                else if (GenericHelpers.TryGetAddonMaster<ShopExchangeCurrency>("ShopExchangeCurrency", out var shopExchange) && shopExchange.IsAddonReady)
                {
                    // There's only 1 item here. So we just need to make sure we have enough to buy it
                    var currency = shopExchange.CurrencyAmount;
                    var item = shopExchange.BasicShopItems[0];
                    var maxAmount = (int)(currency / item.CostAmount); // Maximum we could afford
                    PlayerHelper.GetItemCount(item.ItemId, out var currentAmount);

                    if (C.Cosmodrone_MaxKeep != 0)
                    {
                        var remainingSpace = C.Cosmodrone_MaxKeep - currentAmount;
                        if (remainingSpace <= 0)
                        {
                            IceLogging.Debug($"Already at or above max keep limit ({currentAmount}/{C.Cosmodrone_MaxKeep}), skipping purchase", tag);
                            return true;
                        }

                        maxAmount = Math.Min(maxAmount, remainingSpace);
                    }

                    if (maxAmount > 0 && item.CostAmount <= currency)
                    {
                        if (EzThrottler.Throttle("Selecting to buy this item", 1000))
                        {
                            item.Select(maxAmount);
                            IceLogging.Debug($"Purchasing {maxAmount} items (current: {currentAmount}, max keep: {C.Cosmodrone_MaxKeep})", tag);
                        }
                    }
                    else
                    {
                        IceLogging.Debug("We don't have any more currency to buy the dronebit, so continuing on", tag);
                        return true;
                    }
                }
            }

            return false;
        }
        private static bool? ExitDroneShop()
        {
            string tag = "[Task: ArtifactBuy | Exit Shop]";
            if (GenericHelpers.TryGetAddonMaster<ShopExchangeCurrency>("ShopExchangeCurrency", out var shopExchange) && shopExchange.IsAddonReady)
            {
                if (EzThrottler.Throttle("Closing the window"))
                {
                    IceLogging.Verbose("Closing the shop exchange window", tag);
                    GenericHandlers.FireCallback("ShopExchangeCurrency", true, -1);
                }
            }
            else
            {
                return true;
            }

            return false;
        }
        public static bool CanBuyDroneBoxes()
        {
            var territoryId = Player.Territory.RowId;

            // 惑星ごとのドローン通貨/ボックスIDをDronebitInfoから取得(旧実装はOizys固定で、Auxesiaでは起動しなかった)
            if (!CosmicHelper.DronebitInfo.TryGetValue(territoryId, out var info))
                return false;

            uint dronebitId = info.creditId; // 例: Oizys=49170 / Auxesia=49171
            uint droneBoxId = info.boxId;    // 例: Oizys=50414 / Auxesia=50415

            bool shouldBuyItems = false;

            if (PlayerHelper.GetItemCount(dronebitId, out var bitCount))
            {
                var buyAt = C.Cosmodrone_BuyAt;
                if (buyAt <= bitCount)
                {
                    shouldBuyItems = true;
                }
            }

            if (PlayerHelper.GetItemCount(droneBoxId, out var boxCount))
            {
                var maxBox = C.Cosmodrone_MaxKeep;
                if (maxBox != 0 && boxCount >= maxBox)
                {
                    shouldBuyItems = false;
                }
            }

            return shouldBuyItems;
        }

        // Going to drone locations
        private static Vector3 droneLoc = Vector3.Zero;
        private static bool _hadDroneMarkers = false; // 宝マーカーを収集中だったか(収集完了後にドローンNPCで鑑定するためのトリガ)
        private static long _appraiseGraceStart = 0;   // 鑑定ウィンドウ完了判定の猶予計測
        public static void Enqueue_DroneCheck()
        {
            P.TaskManager.EnqueueMulti
                (
                    new(CloseMapInfo, "Making sure map is close"),
                    new(OpenMapInfo, "Re-opening map to refresh"),
                    new(CloseMapInfo, "Closing one more time cause we don't need it"),
                    new(CheckBoxStatus, "Checking Box Status")
                );
        }
        public class TempMapMarkerData
        {
            public Vector3 Position { get; set; }
            public uint IconId { get; set; }
        }
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

            return markers;
        }
        public static unsafe bool IsTreasureDetected()
        {
            var mapMarkers = GetAllEventMarkers();
            var marker = mapMarkers.Where(x => x.IconId == 63989).FirstOrDefault();

            return marker != null;
        }
        public static bool? RefreshMapInfo()
        {
            P.TaskManager.InsertMulti
                (
                    new(CloseMapInfo, "Making sure map is close"),
                    new(OpenMapInfo, "Re-opening map to refresh"),
                    new(CloseMapInfo, "Closing one more time cause we don't need it"),
                    new(CheckBoxStatus, "Checking Box Status")
                );
            return true;
        }
        public static unsafe bool? CheckBoxStatus()
        {
            droneLoc = Vector3.Zero;
            string tag = "[Task_Artifact: CheckBoxStatus]";

            // ドローン宝箱から得た古代の記録等の自動鑑定(精選/アイテム鑑定)を最優先で処理。
            // 鑑定ウィンドウが開いている間はここで完結させ、後続の宝マーカー判定へ進まない(rimuru版より移植)。
            if (HandleDroneAppraisal())
                return false;

            var mapMarkers = GetAllEventMarkers();
            var marker = mapMarkers.Where(x => x.IconId == 63989).FirstOrDefault();

            // === 一時診断: ドローンフローの状態をファイルへ記録 ===
            if (EzThrottler.Throttle("DroneDiagFile", 2000))
            {
                try
                {
                    var bId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var di) ? di.boxId : 0u;
                    PlayerHelper.GetItemCount(bId, out var bcnt);
                    bool hasDroneNpc = NpcData.MoonNpcs.TryGetValue(Player.Territory.RowId, out var ne) && ne.ContainsKey(NpcData.NpcType.Drone);
                    System.IO.File.AppendAllText(@"\\rio-pc\DevPlugins\master_diag.log",
                        $"[Drone] terr={Player.Territory.RowId} marker={(marker != null)} markerCnt={mapMarkers.Count(x => x.IconId == 63989)} hadMarkers={_hadDroneMarkers} boxId={bId} boxCnt={bcnt} hasKaede={hasDroneNpc} state={SchedulerMain.State}\n");
                }
                catch { }
            }



            if (marker != null)
            {
                if (GenericHelpers.TryGetAddonMaster<WKSMission>("WKSMission", out var hud) && hud.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Close mission window"))
                        GenericHandlers.FireCallback("WKSMission", true, -1);
                }

                IceLogging.Debug("We've found the map flag! Setting it for us to travel to", tag);
                _hadDroneMarkers = true; // 宝を収集中。マーカーが尽きたらドローンNPCで鑑定する
                droneLoc = marker.Position;
                P.TaskManager.Insert(InteractWithDrone, "Interact with drone");
                Task_NavmeshMove.Enqueue_NavmeshTask(droneLoc, false, 3.5f);
                return true;
            }
            else
            {
                // 宝マーカーを収集していたが尽きた(=1サイクル収集完了) → ドローンNPC(Kaede)へ戻って鑑定する。
                // 鑑定後はタスクが空になり、次tickのドローンチェックで通常処理(箱使用/次サイクル)へ自然に復帰する。
                if (_hadDroneMarkers)
                {
                    _hadDroneMarkers = false;
                    IceLogging.Info("宝の収集が一段落したので、ドローンNPCに話しかけて鑑定します", tag);
                    EnqueueAppraisal();
                    return true;
                }

                // 惑星別のドローンボックスIDで所持数を確認(Oizys=50414 / Auxesia=50415)
                var boxId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var dInfo) ? dInfo.boxId : 50414u;
                if (PlayerHelper.GetItemCount(boxId, out var count) && count > 0)
                {
                    IceLogging.Debug("We have a crate to use! Initiating the task to start using it", tag);
                    P.TaskManager.Insert(UseDroneBox, "Use Drone Box");
                    return true;
                }
                else
                {
                    IceLogging.Debug($"We are out of boxes, and we have no markers. So we're continuing on with the normal task");
                    if (SchedulerMain.State == IceState.ArtifactSearch)
                    {
                        SchedulerMain.State = IceState.Idle;
                        P.TaskManager.Tasks.Clear();
                    }
                    return true;
                }
            }
        }
        // ドローン自動鑑定システム(rimuru版より移植)。
        // ドローン宝探索で得た「古代の記録」等を自動で鑑定(精選/アイテム鑑定)する。
        // 鑑定中(Occupied39)/確認ダイアログ/PurifyItemSelector(鑑定開始)/PurifyResult(結果を一括処理して閉じる)を順に捌く。
        // 何か処理中ならtrueを返し、CheckBoxStatus側で後続処理(宝マーカー探索)を保留させる。
        private static unsafe bool HandleDroneAppraisal()
        {
            // 鑑定実行中(アイテム精選アニメ等)は待機
            if (Svc.Condition[ConditionFlag.Occupied39])
                return true;

            // 「鑑定しますか?」等の確認ダイアログ → はい
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Drone appraisal yesno", 250))
                    yesno.Yes();
                return true;
            }

            // 鑑定対象選択ウィンドウ(精選/アイテム鑑定) → 鑑定開始(Callback 12,0)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("PurifyItemSelector", out var purifySelector) && purifySelector->IsReady)
            {
                if (EzThrottler.Throttle("Drone purify", 250) && !Player.IsBusy)
                    ECommons.Automation.Callback.Fire(purifySelector, true, 12, 0);
                return true;
            }

            // 鑑定結果ウィンドウ → 自動で全処理して閉じる
            if (GenericHelpers.TryGetAddonMaster<PurifyResult>("PurifyResult", out var purifyResult) && purifyResult.IsAddonReady)
            {
                if (EzThrottler.Throttle("Drone purify result", 250))
                {
                    purifyResult.Automatic();
                    purifyResult.Close();
                }
                return true;
            }

            return false;
        }

        /// <summary>ドローンNPC(Kaede)へ戻って鑑定(精選)を行う一連のタスクを積む。完了後はタスクが空になり通常のドローン処理へ復帰する。</summary>
        public static void EnqueueAppraisal()
        {
            _appraiseGraceStart = 0;
            P.TaskManager.EnqueueMulti
                (
                    new(Drone_PathToVendor, "Drone Appraise: Path to NPC"),
                    new(TalkToDroneNpc, "Drone Appraise: Talk"),
                    new(SelectAppraisalOption, "Drone Appraise: Select appraisal"),
                    new(ProcessAppraisal, "Drone Appraise: Process windows"),
                    new(CloseDroneMenu, "Drone Appraise: Close menu")
                );
        }

        // Kaedeのメニュー(SelectString)から鑑定/精選の項目を選ぶ。買い物はEntries[0]なので、テキストで鑑定項目を特定する。
        private static bool? SelectAppraisalOption()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
            {
                // メニュー項目のテキストを診断ログに記録(鑑定項目名の確認用)
                if (EzThrottler.Throttle("AppraiseMenuLog", 1000))
                {
                    try
                    {
                        var list = new System.Collections.Generic.List<string>();
                        foreach (var e in ss.Entries) list.Add(e.Text);
                        System.IO.File.AppendAllText(@"\\rio-pc\DevPlugins\master_diag.log", $"[Appraise] SelectString entries=[{string.Join(" | ", list)}]\n");
                    }
                    catch { }
                }

                foreach (var e in ss.Entries)
                {
                    var t = e.Text ?? "";
                    if (t.Contains("鑑定") || t.Contains("精選") || t.Contains("Appraise") || t.Contains("Purif"))
                    {
                        if (EzThrottler.Throttle("Select appraise entry", 300))
                            e.Select();
                        return true;
                    }
                }

                // 鑑定項目が無い(=鑑定対象なし) → そのまま終了(後続のProcessAppraisalは猶予後に即完了)
                IceLogging.Info("ドローンNPCメニューに鑑定項目が見つかりませんでした(鑑定対象なし)。スキップします", "[Drone Appraise]");
                return true;
            }
            return false;
        }

        // 鑑定ウィンドウ(PurifyItemSelector/PurifyResult/SelectYesno/Occupied39)を完了まで捌く。
        // ウィンドウが無い状態が一定時間続いたら鑑定完了(または対象なし)とみなす。
        private static bool? ProcessAppraisal()
        {
            if (HandleDroneAppraisal())
            {
                _appraiseGraceStart = Environment.TickCount64; // 処理中は猶予タイマーをリセット
                return false;
            }

            if (_appraiseGraceStart == 0)
                _appraiseGraceStart = Environment.TickCount64;

            if (Environment.TickCount64 - _appraiseGraceStart >= 3000)
            {
                _appraiseGraceStart = 0;
                return true;
            }
            return false;
        }

        // 鑑定後に残るドローンNPCメニュー(SelectString)を閉じて通常処理へ戻る。
        private static bool? CloseDroneMenu()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
            {
                if (EzThrottler.Throttle("Close drone menu", 250))
                    GenericHandlers.FireCallback("SelectString", true, -1);
                return false;
            }
            return true;
        }

        private static bool? InteractWithDrone()
        {
            string tag = "[Task_Artifact: Drone Interact]";

            var artifact = Svc.Objects.Where(x => x.BaseId == 2015138).FirstOrDefault();

            if (artifact != null)
            {
                if (Player.DistanceTo(artifact.Position) < 4)
                {
                    if (P.Navmesh.IsRunning())
                        P.Navmesh.Stop();

                    if (!Svc.Condition[ConditionFlag.OccupiedInQuestEvent])
                    {
                        Utils.TargetgameObject(artifact);
                        Utils.InteractWithObject(artifact);
                        IceLogging.Verbose($"Drone has been found! Interacting with it", tag);
                    }
                }
            }
            else
            {
                P.TaskManager.Tasks.Clear();
                return true;
            }

            return false;
        }
        private static unsafe bool? UseDroneBox()
        {
            string tag = "[Task_ArtifactSearch: Use Drone Box]";

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("AreaMap", out var mapAddon) && GenericHelpers.IsAddonReady(mapAddon))
            {
                P.TaskManager.Insert(() => RefreshMapInfo(), "Task Artifact: Check Box Status", Utils.TaskConfig);
                return true;
            }
            else if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var YesNo) && YesNo.IsAddonReady)
            {
                if (EzThrottler.Throttle("Selecting yes"))
                {
                    IceLogging.Verbose($"Text: {YesNo.Text}");
                    YesNo.Yes();
                }
            }
            else
            {
                if (Player.Mounted || Player.IsJumping)
                {
                    Utils.Dismount();
                    return false;
                }

                var actionManager = ActionManager.Instance();
                uint itemId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var dInfo) ? dInfo.boxId : 50414u;

                var status = actionManager->GetActionStatus(ActionType.Item, itemId);

                if (status == 0)
                {
                    if (EzThrottler.Throttle("Use Drone", 1000))
                        UseDrone();
                }
                else
                {
                    if (EzThrottler.Throttle("Using drone throttle"))
                        IceLogging.Verbose("We're waiting for the addon map to be visible. If it's not then there's a problem", tag);
                }
            }
                
            return false;
        }
        public static unsafe bool? OpenMapInfo()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("AreaMap", out var mapAddon) && GenericHelpers.IsAddonReady(mapAddon))
            {
                return true;
            }
            else
            {
                if (EzThrottler.Throttle("Opening map again", 500))
                {
                    var map = Player.Territory.Value.Map.Value;
                    var territoryid = Player.Territory.RowId;
                    var agent = AgentMap.Instance();

                    agent->OpenMap(map.RowId, territoryid);
                }
                return false;
            }
        }
        public static unsafe bool? CloseMapInfo()
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("AreaMap", out var mapAddon) && GenericHelpers.IsAddonReady(mapAddon))
            {
                if (EzThrottler.Throttle("Closing map temp"))
                {
                    GenericHandlers.FireCallback("AreaMap", true, -1);
                }
                return false;
            }
            else
            {
                return true;
            }
        }
        private static unsafe void UseDrone()
        {
            uint itemId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var dInfo) ? dInfo.boxId : 50414u;
            var inventoryManager = InventoryManager.Instance();

            // Array of inventory types to check
            var inventoryTypes = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4
            };

            foreach (var invType in inventoryTypes)
            {
                var container = inventoryManager->GetInventoryContainer(invType);
                if (container == null) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var item = container->GetInventorySlot(i);
                    if (item != null && item->ItemId == itemId)
                    {
                        // Use the item from inventory
                        AgentInventoryContext.Instance()->UseItem(item->ItemId, invType, (uint)i, 0);
                        return;
                    }
                }
            }

            // If we get here, item wasn't found
            PluginLog.Warning($"Item {itemId} not found in any inventory container");
        }
    }
}
