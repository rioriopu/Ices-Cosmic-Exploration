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
using static ICE.Ui.Debug_Tabs.Debug_Ui.Ui_OyzinMap;

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

            if (NpcData.TryGetNpc(zoneId, NpcData.NpcType.Drone, out var npcEntry))
            {
                // 既に近ければ完了とみなして会話へ進む。停止距離より少し大きい2fにして取りこぼしを防ぐ。
                if (Player.DistanceTo(npcEntry.Location_Npc) <= 2f)
                {
                    IceLogging.Debug("We're close enough to the drone npc! Continuing on", handle);
                    return true;
                }

                // 遠距離はボード(コスモライナー)/エーテネット/ステラリターンも評価する移動で向かう。
                // 直線徒歩固定だと、拠点と採掘エリアがボードで繋がるゾーンで全行程を歩いてしまう。
                Vector3 randomPos = NpcData.GetRandomPointInCircle(npcEntry.Location_Circle, 0.5f);
                if (EzThrottler.Throttle("Drone Move Message", 1000))
                    IceLogging.Verbose($"ドローンNPCへ移動中(ボード等も使用)。距離: {Player.DistanceTo(npcEntry.Location_Npc):F0}", handle);
                Task_NavmeshMove.Enqueue_NavmeshTask(randomPos, false, 1.5f);
                return true;
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
                if (NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Drone, out var droneInfo))
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
            var territory = Player.Territory.RowId;
            if (!CosmicMoonRegistry.TryGetDronebit(territory, out var dronebitInfo))
                return false;

            bool shouldBuyItems = false;

            if (PlayerHelper.GetItemCount(dronebitInfo.creditId, out var bitCount))
            {
                var buyAt = C.Cosmodrone_BuyAt;
                if (buyAt <= bitCount)
                {
                    shouldBuyItems = true;
                }
            }

            if (PlayerHelper.GetItemCount(dronebitInfo.boxId, out var boxCount))
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

            // ドローン宝箱から得た古代の記録等の自動鑑定を最優先で処理する。
            // 鑑定ウィンドウが開いている間はここで完結させ、後続の宝マーカー判定へは進まない。
            if (HandleDroneAppraisal())
                return false;

            var mapMarkers = GetAllEventMarkers();
            var marker = mapMarkers.Where(x => x.IconId == 63989).FirstOrDefault();
            if (!CosmicMoonRegistry.TryGetDronebit(Player.Territory.RowId, out var dronebit))
                return false;

            uint itemId = dronebit.boxId;

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
                // 宝マーカーが尽きても、所持品にエネルギーパックが残っていれば鑑定より先に採掘を続ける。
                // パックが完全に尽きて初めて、溜まった戦利品をまとめて鑑定する。
                if (PlayerHelper.GetItemCount(itemId, out var count) && count > 0)
                {
                    IceLogging.Debug("エネルギーパックがまだあるので、鑑定より先に使って採掘を続けます", tag);
                    P.TaskManager.Insert(UseDroneBox, "Use Drone Box");
                    return true; // _hadDroneMarkers は維持(未鑑定の戦利品はパックが尽きたときにまとめて鑑定する)
                }

                // パックが尽きた → 収集済み(未鑑定)があればドローンNPCに話しかけて鑑定する。
                // 「探索後、古代の記録を自動鑑定する」が無効なら鑑定はせず、そのまま完了扱いにする。
                if (_hadDroneMarkers)
                {
                    _hadDroneMarkers = false;
                    if (C.Cosmodrone_AutoAppraise)
                    {
                        IceLogging.Info("エネルギーパックが尽きたので、ドローンNPCに話しかけて鑑定します", tag);
                        // 先行して積まれたタスクをクリアし、鑑定(NPCへ移動→鑑定)を確実に走らせる。
                        P.TaskManager.Tasks.Clear();
                        EnqueueAppraisal();
                        return true;
                    }
                    IceLogging.Info("エネルギーパックが尽きました。自動鑑定は無効なので、鑑定せずに探索を完了します", tag);
                }

                // パックも無く鑑定も済んだ(または鑑定しない) → 完了。
                // 手動(ドローン探索ボタン)で始めた場合は、コスモデジョンで拠点へ戻ってから停止する。
                // ミッションループ内から呼ばれた場合(State は GrabMission 等)は、そのまま通常処理に戻る。
                if (SchedulerMain.State == IceState.ArtifactSearch)
                {
                    IceLogging.Info("ドローン探索を完了しました。拠点へ戻って停止します", tag);
                    P.TaskManager.Tasks.Clear();
                    Enqueue_FinishManualSearch();
                }
                else
                {
                    IceLogging.Info("ドローン探索を完了しました。通常処理へ復帰します", tag);
                }
                return true;
            }
        }
        private static bool _hadDroneMarkers = false; // 宝マーカーを収集中だったか(収集完了後にドローンNPCで鑑定するためのトリガ)
        private static long _appraiseGraceStart = 0;   // 鑑定ウィンドウ完了判定の猶予計測
        private static long _iilAloneSince = 0;         // ItemInspectionListのみが残った時刻(全件完了→一覧を閉じる判定)
        private static long _lastResultActivity = 0;    // 鑑定処理が最後に動いた時刻。一覧の早すぎる再クリック防止。
        private static int _resultNextCount = 0;        // 1つの結果窓でNextを連打した回数(無限ループ保険)

        // ItemInspectionListの1行レンダラから表示テキスト(項目名)を読む。子のテキストノードを連結する。
        private static unsafe string ReadRendererText(FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentListItemRenderer* rend)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var uld = &rend->AtkComponentButton.AtkComponentBase.UldManager;
                for (var n = 0; n < uld->NodeListCount; n++)
                {
                    var node = uld->NodeList[n];
                    if (node == null) continue;
                    if (node->Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text)
                    {
                        var tn = (FFXIVClientStructs.FFXIV.Component.GUI.AtkTextNode*)node;
                        var s = tn->NodeText.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) sb.Append(s).Append(' ');
                    }
                }
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        // 鑑定の優先順位: 緑金(0) > 青銀(1) > 白銀/白銅(2) > その他(100)。小さいほど先に鑑定する。
        private static int AppraisePriorityRank(string t)
        {
            if (string.IsNullOrEmpty(t)) return 99;
            if (t.Contains("緑金")) return 0;
            if (t.Contains("青銀")) return 1;
            if (t.Contains("白銀") || t.Contains("白銅")) return 2;
            return 100;
        }

        // ドローン自動鑑定。ドローン宝探索で得た「古代の記録」等を自動で鑑定(精選/アイテム鑑定)する。
        // 鑑定中(Occupied39)/確認ダイアログ/ItemInspection系/PurifyItemSelector/PurifyResult を順に捌く。
        // 何か処理中ならtrueを返し、呼び出し側で後続処理(宝マーカー探索)を保留させる。
        private static unsafe bool HandleDroneAppraisal()
        {
            // 鑑定実行中(アイテム精選アニメ等)は待機
            if (Svc.Condition[ConditionFlag.Occupied39])
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64;
                return true;
            }

            // 「鑑定しますか?」等の確認ダイアログ → はい
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64;
                if (EzThrottler.Throttle("Drone appraisal yesno", 250))
                    yesno.Yes();
                return true;
            }

            // 鑑定中アニメ窓
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemInspection", out var iiProgress) && iiProgress->IsVisible)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64;
                return true;
            }

            // 結果窓: Nextが押せれば次の記録へ、押せなくなったら閉じる(param=-1)。
            if (GenericHelpers.TryGetAddonMaster<ItemInspectionResult>("ItemInspectionResult", out var iiResult) && iiResult.IsAddonReady)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64;
                // 「次を鑑定する」は早すぎると不安定になるため 600ms 間隔にする。
                if (EzThrottler.Throttle("Drone iiresult", 600))
                {
                    bool nextEnabled = iiResult.NextButton != null && iiResult.NextButton->IsEnabled;
                    // 上限は暴走保険。通常はNextの無効化で自然に終了する。
                    if (nextEnabled && _resultNextCount < 500)
                    {
                        iiResult.Next();
                        _resultNextCount++;
                    }
                    else
                    {
                        iiResult.Close();
                        _resultNextCount = 0;
                    }
                }
                return true;
            }

            // 一覧窓(ItemInspectionList): 優先度の高い行に ListItemClick(35) を発火して鑑定を進める。
            // AtkEventData が空だと ReceiveEvent 内で null 参照になるため、必ず実際の項目レンダラを Data[0] に入れる。
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemInspectionList", out var iiList) && iiList->IsVisible)
            {
                long now = Environment.TickCount64;
                if (_iilAloneSince == 0) _iilAloneSince = now;
                long alone = now - _iilAloneSince;
                // 直近に鑑定処理が動いていたら結果窓の遷移中。ここで再クリックすると鑑定フローを中断してしまう。
                long sinceActivity = _lastResultActivity == 0 ? long.MaxValue : now - _lastResultActivity;
                if (sinceActivity < 1500)
                    return true;

                if (alone < 3500)
                {
                    if (EzThrottler.Throttle("iil select", 1200))
                    {
                        _resultNextCount = 0;
                        var addonIIL = (FFXIVClientStructs.FFXIV.Client.UI.AddonItemInspectionList*)iiList;
                        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList* list = null;
                        for (uint id = 2; id <= 100 && list == null; id++)
                            list = addonIIL->GetComponentListById(id);

                        // 優先順位(緑金>青銀>白銀/白銅)が最も高い行を選ぶ。
                        int pickIndex = 0;
                        int bestRank = int.MaxValue;
                        if (list != null)
                        {
                            int rows = list->ListLength;
                            if (rows <= 0) rows = list->GetItemCount();
                            for (int i = 0; i < rows; i++)
                            {
                                var r = list->GetItemRenderer(i);
                                if (r == null) continue;
                                int rank = AppraisePriorityRank(ReadRendererText(r));
                                if (rank < bestRank) { bestRank = rank; pickIndex = i; }
                            }
                        }

                        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentListItemRenderer* renderer = null;
                        if (list != null)
                            renderer = list->GetItemRenderer(pickIndex);

                        if (renderer != null)
                        {
                            var eventData = ECommons.Automation.UIInput.EventData.ForNormalTarget(&FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance()->AtkEventTarget, iiList);
                            var inputData = ECommons.Automation.UIInput.InputData.Empty();
                            inputData.Data[0] = renderer;            // 必須: 項目レンダラ(空だとクラッシュする)
                            inputData.Data[2] = (void*)(long)pickIndex;
                            iiList->ReceiveEvent((FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType)35, 0, eventData.Data, (FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData*)inputData.Data);
                            eventData.Dispose();
                            inputData.Dispose();
                            _lastResultActivity = now;
                        }
                    }
                    return true;
                }

                // 一定時間、結果窓も精選アニメも出ず対象もない = 全件完了 → 一覧を閉じる
                if (EzThrottler.Throttle("iil close", 500))
                {
                    ECommons.Automation.Callback.Fire(iiList, true, -1);
                    _iilAloneSince = 0;
                }
                return true;
            }

            // 鑑定対象選択ウィンドウ(精選/アイテム鑑定) → 鑑定開始
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("PurifyItemSelector", out var purifySelector) && purifySelector->IsReady)
            {
                if (EzThrottler.Throttle("Drone purify", 250) && !Player.IsBusy)
                {
                    ECommons.Automation.Callback.Fire(purifySelector, true, 12, 0);
                }
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

        /// <summary>ドローンNPCへ戻って鑑定(精選)を行う一連のタスクを積む。完了後は通常のドローン処理へ復帰する。</summary>
        public static void EnqueueAppraisal()
        {
            ResetAppraisalCounters();
            P.TaskManager.EnqueueMulti
                (
                    new(Drone_PathToVendor, "Drone Appraise: Path to NPC"),
                    new(TalkToDroneNpc, "Drone Appraise: Talk"),
                    new(SelectAppraisalOption, "Drone Appraise: Select appraisal"),
                    new(ProcessAppraisal, "Drone Appraise: Process windows"),
                    new(CloseDroneMenu, "Drone Appraise: Close menu")
                );
        }

        private static void ResetAppraisalCounters()
        {
            _appraiseGraceStart = 0;
            _iilAloneSince = 0;
            _lastResultActivity = 0;
            _resultNextCount = 0;
        }

        /// <summary>手動のドローン探索(ボタン)完了時: 拠点から離れていればコスモデジョンで戻り、その後停止(Idle)する。</summary>
        private static void Enqueue_FinishManualSearch()
        {
            Task_BuyLevelingGear.ResetReturnStart();
            P.TaskManager.EnqueueMulti
                (
                    new(Task_BuyLevelingGear.ReturnToHub, "Drone Search: Stellar Return to the hub"),
                    new(WalkToHubIfFar, "Drone Search: Walk to the hub"),
                    new(FinishAndIdle, "Drone Search: Finish")
                );
        }

        /// <summary>
        /// コスモデジョンが無効(AvoidStellarReturn)/発動できずタイムアウトした場合、ReturnToHub は「徒歩に任せる」として true を返す。
        /// その場合にまだ拠点から遠ければ、徒歩(ボード等も含む経路)でドローンNPC付近まで戻る。Enqueue_NavmeshTask は InsertMulti なので
        /// 移動タスクは FinishAndIdle の前に差し込まれ、到着後に Idle になる。
        /// </summary>
        private static bool? WalkToHubIfFar()
        {
            const string tag = "[Drone Search]";
            if (!CosmicMoonRegistry.TryGetHubCenter(Player.Territory.RowId, out var hub)
                || Player.DistanceTo(hub) < C.HubReturn_Distance)
                return true; // 既に拠点付近(コスモデジョンで戻れた)
            if (!NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Drone, out var npc))
                return true; // 目的地が無ければ諦める(無限ループ防止)
            IceLogging.Info($"コスモデジョンで戻れなかったため、徒歩で拠点へ戻ります(距離 {Player.DistanceTo(hub):F0}m)", tag);
            Task_NavmeshMove.Enqueue_NavmeshTask(NpcData.GetRandomPointInCircle(npc.Location_Circle, 0.5f), false, 3f);
            return true;
        }

        private static bool? FinishAndIdle()
        {
            if (P.Navmesh.Installed && P.Navmesh.IsRunning())
                P.Navmesh.Stop();
            SchedulerMain.State = IceState.Idle;
            return true;
        }

        // ---- 手動の「すぐに自動鑑定する」 ----

        /// <summary>この惑星に鑑定を行うドローンNPC(カエデ)がいるか。</summary>
        public static bool CanAppraiseHere()
            => NpcData.TryGetNpc(Player.Territory.RowId, NpcData.NpcType.Drone, out _);

        /// <summary>手動鑑定を開始する(設定画面のボタンから)。他の処理が動いていない(Idle)ときだけ受け付ける。</summary>
        public static void StartManualAppraisal()
        {
            const string tag = "[Drone Appraise]";
            if (!Player.Available)
                return;
            if (!CanAppraiseHere())
            {
                IceLogging.ChatError(Loc.T("There is no drone NPC on this planet, so nothing can be appraised here."), tag);
                return;
            }
            if (SchedulerMain.State != IceState.Idle || P.TaskManager.NumQueuedTasks > 0)
            {
                IceLogging.ChatError(Loc.T("ICE is busy. Stop the current run before starting the appraisal."), tag);
                return;
            }
            IceLogging.ChatInfo(Loc.T("Heading to the drone NPC to appraise the Ancient Records."), tag);
            // タスクは SchedulerMain.Tick が Appraisal 状態を見て Enqueue_ManualAppraisal を積む
            _manualAppraisalRuns = 0;
            SchedulerMain.State = IceState.Appraisal;
        }

        private static int _manualAppraisalRuns = 0; // タスク列が途中で落ちて Tick から再投入された回数(暴走防止)

        /// <summary>手動鑑定の一連のタスク(拠点へ帰還→NPCへ移動→会話→鑑定を選択→鑑定を連続処理→メニューを閉じる→停止)。</summary>
        public static void Enqueue_ManualAppraisal()
        {
            // 例外などでタスク列が途中で消えると Tick が再投入する。何度も繰り返すなら諦めて停止する。
            if (++_manualAppraisalRuns > 3)
            {
                IceLogging.ChatError(Loc.T("The appraisal kept failing, so it was stopped."), "[Drone Appraise]");
                SchedulerMain.DisablePlugin(); // navmesh も止めてから Idle にする(移動中に落ちた場合の走り続け防止)
                return;
            }
            ResetAppraisalCounters();
            Task_BuyLevelingGear.ResetReturnStart();
            P.TaskManager.EnqueueMulti
                (
                    new(Task_BuyLevelingGear.ReturnToHub, "Manual Appraise: Stellar Return to the hub"),
                    new(Drone_PathToVendor, "Manual Appraise: Path to NPC"),
                    new(TalkToDroneNpc, "Manual Appraise: Talk"),
                    new(SelectAppraisalOption, "Manual Appraise: Select appraisal"),
                    new(ProcessAppraisal, "Manual Appraise: Process windows"),
                    new(CloseDroneMenu, "Manual Appraise: Close menu"),
                    new(FinishManualAppraisal, "Manual Appraise: Finish")
                );
        }

        private static bool? FinishManualAppraisal()
        {
            IceLogging.ChatInfo(Loc.T("Appraisal finished."), "[Drone Appraise]");
            SchedulerMain.State = IceState.Idle;
            return true;
        }

        /// <summary>鑑定の停止ボタン。タスクを全て破棄し、開いている鑑定ウィンドウ/NPCメニューを閉じる。</summary>
        public static unsafe void StopAppraisal()
        {
            const string tag = "[Drone Appraise]";
            bool wasAppraising = SchedulerMain.State == IceState.Appraisal;
            SchedulerMain.DisablePlugin();
            _hadDroneMarkers = false;
            ResetAppraisalCounters();
            try
            {
                // 「鑑定しますか?」の確認ダイアログが出ていたら、先に「いいえ」で閉じる(下の一覧だけ閉じると取り残される)
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
                    yesno.No();
                if (GenericHelpers.TryGetAddonMaster<ItemInspectionResult>("ItemInspectionResult", out var iiResult) && iiResult.IsAddonReady)
                    iiResult.Close();
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemInspectionList", out var iiList) && iiList->IsVisible)
                    ECommons.Automation.Callback.Fire(iiList, true, -1);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("PurifyItemSelector", out var purify) && purify->IsVisible)
                    ECommons.Automation.Callback.Fire(purify, true, -1);
                if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
                    GenericHandlers.FireCallback("SelectString", true, -1);
            }
            catch (Exception ex)
            {
                IceLogging.Debug($"鑑定ウィンドウを閉じる際にエラー: {ex.Message}", tag);
            }
            IceLogging.ChatInfo(wasAppraising ? Loc.T("Appraisal stopped.") : Loc.T("Stopped."), tag);
        }

        // ドローンNPCのメニュー(SelectString)から鑑定/精選の項目を選ぶ。買い物はEntries[0]なのでテキストで特定する。
        private static bool? SelectAppraisalOption()
        {
            if (GenericHelpers.TryGetAddonMaster<SelectString>("SelectString", out var ss) && ss.IsAddonReady)
            {
                foreach (var e in ss.Entries)
                {
                    var t = e.Text ?? "";
                    // 「〜について聞く」は説明項目なので除外する。
                    if (t.Contains("について聞く") || t.Contains("聞く"))
                        continue;
                    // Auxesia(カエデ)の鑑定項目は「『古代の記録』と報酬の交換」。他の月の「鑑定/精選」にも対応する。
                    if (t.Contains("報酬の交換") || t.Contains("鑑定") || t.Contains("精選") || t.Contains("Appraise") || t.Contains("Purif"))
                    {
                        if (EzThrottler.Throttle("Select appraise entry", 300))
                        {
                            e.Select();
                        }
                        return true;
                    }
                }

                IceLogging.Info("ドローンNPCメニューに鑑定項目が見つかりませんでした(鑑定対象なし)。スキップします", "[Drone Appraise]");
                return true;
            }
            return false;
        }

        // 鑑定ウィンドウを完了まで捌く。ウィンドウが無い状態が一定時間続いたら鑑定完了(または対象なし)とみなす。
        private static unsafe bool? ProcessAppraisal()
        {
            if (HandleDroneAppraisal())
            {
                _appraiseGraceStart = Environment.TickCount64; // 処理中は猶予タイマーをリセット
                return false;
            }

            if (_appraiseGraceStart == 0)
                _appraiseGraceStart = Environment.TickCount64;

            if (Environment.TickCount64 - _appraiseGraceStart >= 8000)
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
                if (!CosmicMoonRegistry.TryGetDronebit(Player.Territory.RowId, out var dronebit))
                    return false;

                uint itemId = dronebit.boxId;

                var status = actionManager->GetActionStatus(ActionType.Item, itemId);

                if (status == 0)
                {
                    if (EzThrottler.Throttle("Use Drone", 1000))
                        UseDrone();
                }
                else
                {
                    if (EzThrottler.Throttle("Using drone throttle"))
                        IceLogging.Verbose("We're waiting for the addon map to be visible. If it's not then there's a problem\n" +
                            $"Status is currently: {status}", tag);
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
            if (!CosmicMoonRegistry.TryGetDronebit(Player.Territory.RowId, out var dronebit))
                return;

            uint itemId = dronebit.boxId;
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
                        if (EzThrottler.Throttle("Using item"))
                            IceLogging.Verbose($"Use Item: {itemId} | Inventory Type: {invType.ToString()} | Slot: {i}");
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
