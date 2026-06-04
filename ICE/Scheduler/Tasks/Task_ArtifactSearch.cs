using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Chat;
using ECommons.DalamudServices.Legacy;
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
                // 既に近ければ完了
                if (Player.DistanceTo(npcEntry.Location_Npc) <= 6f)
                {
                    IceLogging.Debug("We're close enough to the drone npc! Continuing on", handle);
                    return true;
                }

                // 遠距離はボード(コスモライナー)/エーテネット/Stellar Return も評価する賢いナビで向かう。
                // 旧実装は Task_NavTo(直線徒歩)固定で、Auxesiaのように拠点と採掘エリアがボードで繋がるゾーンでは
                // ボードを使わず全行程を徒歩で帰っていた(実機報告)。Enqueue_NavmeshTask でボード経由を選択可能にする。
                // FindBestTravel→DestinationPathingが目的地到着までブロックするので、到着後に次タスク(会話)へ進む。
                Vector3 randomPos = NpcData.GetRandomPointInCircle(npcEntry.Location_Circle, 0.5f);
                if (EzThrottler.Throttle("Drone Move Message", 1000))
                    IceLogging.Verbose($"ドローンNPCへボード等も使う賢いナビで移動中。距離: {Player.DistanceTo(npcEntry.Location_Npc):F0}", handle);
                Task_NavmeshMove.Enqueue_NavmeshTask(randomPos, waitForBusy: false, distance: 5f);
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
        private static bool _appraiseKickedOff = false; // ItemInspectionList で最初の1件を選択済みか
        private static long _iilAloneSince = 0;         // ItemInspectionListのみが残った時刻(全件完了→一覧を閉じる判定)
        private static long _lastResultActivity = 0;     // 鑑定処理(Occupied/Yesno/Inspection/Result)が最後に動いた時刻。一覧の早すぎる再クリック防止。
        private static int _resultNextCount = 0;          // 1つの結果窓でNextを連打した回数(無限ループ保険)
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
                // ★採掘優先(ユーザー要望): 宝マーカーが尽きても、所持品にエネルギーパックが残っていれば
                //   鑑定より先にパックを使って採掘を続ける。パックが完全に尽きて初めて、溜まった戦利品をまとめて鑑定する。
                var boxId = CosmicHelper.DronebitInfo.TryGetValue(Player.Territory.RowId, out var dInfo) ? dInfo.boxId : 50414u;
                if (PlayerHelper.GetItemCount(boxId, out var count) && count > 0)
                {
                    IceLogging.Debug("エネルギーパックがまだあるので、鑑定より先に使って採掘を続けます", tag);
                    P.TaskManager.Insert(UseDroneBox, "Use Drone Box");
                    return true; // _hadDroneMarkers は維持(未鑑定の戦利品は、パックが尽きたときにまとめて鑑定)
                }

                // パックが尽きた → 収集済み(未鑑定)があればドローンNPC(Kaede)で鑑定する。
                if (_hadDroneMarkers)
                {
                    _hadDroneMarkers = false;
                    IceLogging.Info("エネルギーパックが尽きたので、ドローンNPCに話しかけて鑑定します", tag);
                    // 先行してキューに積まれた製作ミッションgrab等をクリアし、鑑定(カエデ歩行→鑑定)を確実に走らせる。
                    P.TaskManager.Tasks.Clear();
                    EnqueueAppraisal();
                    return true;
                }

                // パックも無く鑑定も済 → 通常処理へ復帰。
                // 旧実装は IceState.Idle にしてプラグインごと停止していた(ドローン探索後に拠点へ帰らず棒立ち)。
                // コメント通り「通常処理へ続行」するため Start に遷移し、CheckState→拠点帰還(必要ならStellar Return/
                // ボード/コスモライナー)→次ミッションへとループを再開する。
                IceLogging.Info("ドローン探索を完了しました。通常処理(拠点帰還→ミッション)へ復帰します", tag);
                if (SchedulerMain.State == IceState.ArtifactSearch)
                {
                    SchedulerMain.State = IceState.Start;
                    P.TaskManager.Tasks.Clear();
                }
                return true;
            }
        }
        // ItemInspectionListの1行レンダラから表示テキスト(項目名)を読む。子のテキストノードを連結。
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

        // ドローン自動鑑定システム(rimuru版より移植)。
        // ドローン宝探索で得た「古代の記録」等を自動で鑑定(精選/アイテム鑑定)する。
        // 鑑定中(Occupied39)/確認ダイアログ/PurifyItemSelector(鑑定開始)/PurifyResult(結果を一括処理して閉じる)を順に捌く。
        // 何か処理中ならtrueを返し、CheckBoxStatus側で後続処理(宝マーカー探索)を保留させる。
        private static unsafe bool HandleDroneAppraisal()
        {
            // 鑑定実行中(アイテム精選アニメ等)は待機
            if (Svc.Condition[ConditionFlag.Occupied39])
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64; // 進行中
                return true;
            }

            // 「鑑定しますか?」等の確認ダイアログ → はい
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64; // 進行中
                if (EzThrottler.Throttle("Drone appraisal yesno", 250))
                    yesno.Yes();
                return true;
            }

            // ── Auxesia: アイテム鑑定(ItemInspection)系 ──
            // 鑑定中アニメ窓
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemInspection", out var iiProgress) && iiProgress->IsVisible)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64; // 進行中
                return true;
            }
            // 結果窓: Nextが押せれば次の記録へ、押せなくなったら(=報酬表示まで到達)閉じる(param=-1)。
            // 実機の手動操作は「Next(0)を数回 → 閉じる(-1)」。Nextが押せる限り進め、押せなくなったら必ずCloseで締める。
            // 保険: 同一結果窓でNextを連打しすぎたら(想定外でNextが無効化されない)強制的にCloseする。
            if (GenericHelpers.TryGetAddonMaster<ItemInspectionResult>("ItemInspectionResult", out var iiResult) && iiResult.IsAddonReady)
            {
                _iilAloneSince = 0; _lastResultActivity = Environment.TickCount64; // 進行中
                // 「次を鑑定する」(NextButton id74)は早すぎると不安定になるため 600ms 間隔に。
                if (EzThrottler.Throttle("Drone iiresult", 600))
                {
                    bool nextEnabled = iiResult.NextButton != null && iiResult.NextButton->IsEnabled;
                    // Nextが押せる限り「次を鑑定する」で同カテゴリを鑑定し続ける。押せなくなったら(=該当記録が尽きた)閉じる(param=-1)。
                    // 上限は暴走保険(同カテゴリ最大想定を超える 500 回)。通常はNext無効化で自然終了する。
                    if (nextEnabled && _resultNextCount < 500)
                    {
                        iiResult.Next();
                        _resultNextCount++;
                    }
                    else
                    {
                        iiResult.Close();  // CloseButton id73 → param=-1
                        _resultNextCount = 0;
                    }
                }
                return true;
            }
            // 一覧窓(ItemInspectionList): 先頭の記録に ListItemClick(35) を発火して鑑定を進める。
            // 実機捕捉と同一のイベント。ただし AtkEventData が空だと ReceiveEvent内でnull参照→クラッシュするため、
            // 必ず実際の項目レンダラ(GetItemRenderer(0))を Data[0] に入れて発火する(ForPopupMenuと同じ方式)。
            // 選択しても確認窓が一定時間出ない/対象レンダラがない = 全件完了 → 一覧を閉じる。
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemInspectionList", out var iiList) && iiList->IsVisible)
            {
                long now = Environment.TickCount64;
                if (_iilAloneSince == 0) _iilAloneSince = now;
                long alone = now - _iilAloneSince;
                // 直近に鑑定処理(結果窓/精選アニメ/Yesno)が動いていたら、結果窓の遷移中の一瞬の隙間。
                // ここで一覧を再クリックすると鑑定フローを中断してしまう(前回バグの原因)。1.5秒は待つ。
                long sinceActivity = _lastResultActivity == 0 ? long.MaxValue : now - _lastResultActivity;
                if (sinceActivity < 1500)
                    return true;

                if (alone < 3500)
                {
                    if (EzThrottler.Throttle("iil select", 1200))
                    {
                        _resultNextCount = 0; // 新しい記録の鑑定開始
                        var addonIIL = (FFXIVClientStructs.FFXIV.Client.UI.AddonItemInspectionList*)iiList;
                        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList* list = null;
                        for (uint id = 2; id <= 100 && list == null; id++)
                            list = addonIIL->GetComponentListById(id);

                        // 優先順位(緑金>青銀>白銀/白銅)が最も高い行を選ぶ。各行のテキストを読んで判定。
                        int pickIndex = 0;
                        int bestRank = int.MaxValue;
                        string pickText = "";
                        if (list != null)
                        {
                            int rows = list->ListLength;
                            if (rows <= 0) rows = list->GetItemCount();
                            for (int i = 0; i < rows; i++)
                            {
                                var r = list->GetItemRenderer(i);
                                if (r == null) continue;
                                string rtext = ReadRendererText(r);
                                int rank = AppraisePriorityRank(rtext);
                                if (rank < bestRank) { bestRank = rank; pickIndex = i; pickText = rtext; }
                            }
                        }

                        FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentListItemRenderer* renderer = null;
                        if (list != null)
                            renderer = list->GetItemRenderer(pickIndex);

                        if (renderer != null)
                        {
                            // 正しいデータ付きで ListItemClick(35) を発火(クラッシュ回避: Data[0]=有効なレンダラ)。
                            // ClickHelper経由はMarshal.GetDelegateForFunctionPointerでキャスト失敗するため、
                            // addonのReceiveEventを直接呼ぶ(_CharaSelectListMenuと同方式)。
                            var eventData = ECommons.Automation.UIInput.EventData.ForNormalTarget(&FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance()->AtkEventTarget, iiList);
                            var inputData = ECommons.Automation.UIInput.InputData.Empty();
                            inputData.Data[0] = renderer;            // 必須: 項目レンダラ(空だとクラッシュ)
                            inputData.Data[2] = (void*)(long)pickIndex; // 選択する行index
                            iiList->ReceiveEvent((FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType)35, 0, eventData.Data, (FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData*)inputData.Data);
                            eventData.Dispose();
                            inputData.Dispose();
                            // 自分のクリックも「活動」として記録。結果窓が開くまでの隙間に二重クリックするのを防ぐ。
                            _lastResultActivity = now;
                        }
                    }
                    return true;
                }

                // 3.5秒間、結果窓も精選アニメも出ず対象もない = 全件完了 → 一覧を閉じる
                if (EzThrottler.Throttle("iil close", 500))
                {
                    ECommons.Automation.Callback.Fire(iiList, true, -1);
                    _iilAloneSince = 0;
                }
                return true;
            }

            // 鑑定対象選択ウィンドウ(精選/アイテム鑑定) → 鑑定開始(Callback 12,0)
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

        /// <summary>ドローンNPC(Kaede)へ戻って鑑定(精選)を行う一連のタスクを積む。完了後はタスクが空になり通常のドローン処理へ復帰する。</summary>
        public static void EnqueueAppraisal()
        {
            _appraiseGraceStart = 0;
            _appraiseKickedOff = false;
            _iilAloneSince = 0;
            _lastResultActivity = 0;
            _resultNextCount = 0;
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
                foreach (var e in ss.Entries)
                {
                    var t = e.Text ?? "";
                    // 「〜について聞く」は説明項目なので除外(「古代の記録」について聞く 等)
                    if (t.Contains("について聞く") || t.Contains("聞く"))
                        continue;
                    // Auxesia(カエデ)の鑑定項目は「『古代の記録』と報酬の交換」。Oizys等の「鑑定/精選」も併せて対応。
                    if (t.Contains("報酬の交換") || t.Contains("鑑定") || t.Contains("精選") || t.Contains("Appraise") || t.Contains("Purif"))
                    {
                        if (EzThrottler.Throttle("Select appraise entry", 300))
                        {
                            e.Select();
                        }
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
        private static unsafe bool? ProcessAppraisal()
        {
            if (HandleDroneAppraisal())
            {
                _appraiseGraceStart = Environment.TickCount64; // 処理中は猶予タイマーをリセット
                return false;
            }

            if (_appraiseGraceStart == 0)
                _appraiseGraceStart = Environment.TickCount64;

            // 交換窓の出現を観測するため猶予を長め(8秒)に。窓が出れば上のダンプに名前が残る。
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
