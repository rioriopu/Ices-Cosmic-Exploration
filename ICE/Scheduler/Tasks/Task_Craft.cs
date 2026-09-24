using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Craft
    {
        public static void Enqueue()
        {
            // ミッション境界では製作対象が無いので何もしない(以降のミッション情報参照を保護する)。
            if (CosmicHelper.CurrentLunarMission == 0)
                return;

            if (P.Artisan.IsBusy())
            {
                P.TaskManager.Enqueue(() => WaitingForArtisan(), "Waiting for artisan to finish crafting");
                P.TaskManager.Enqueue(() => Task_CheckScore.Enqueue(), "Checking score");
            }
            else
            {
                P.TaskManager.Enqueue(() => Task_CheckScore.Enqueue(), "Checking Score");
                P.TaskManager.Enqueue(() => CheckMaterials(), "Checking materials", Utils.TaskConfig);
            }
        }

        // 単一の製作アクションがロックし始めた時刻(アニメーションロック検知用)。MinValue=未計測。
        private static DateTime _craftActionLockSince = DateTime.MinValue;

        // ---- Artisan 停止監視 ----
        // Artisan が「動作中(IsBusy)」を返し続けているのに、製作アクション(ExecutingCraftingAction)が一定時間まったく出ない
        // 状態を「停止」とみなす。製作が進んでいれば数秒おきに必ずアクションが出るので、Artisan 内部の状態や
        // 製作スタンスの有無に関係なく検知できる。
        // 1.0.0.19 の監視は「製作スタンス(Crafting/PreparingToCraft)に入っていない時間」を数えていたため、
        // レシピ帳を開いたまま(スタンスに入ったまま)Artisan が製作を始められないケースを検知できなかった
        // (実機: 前工程 x2 の直後に主製作へレシピを切り替えるところで、ログが「Telling Artisan to craft」で途切れて停止)。
        private static DateTime _artisanWaitSince = DateTime.MinValue;    // 今回の待機の開始時刻(MinValue=待機していない)
        private static DateTime _lastCraftActionAt = DateTime.MinValue;   // 最後に製作アクションを観測した時刻(待機開始時に初期化)
        private static int _artisanStallRecoveries = 0;                   // 同一ミッション内での復旧回数(上限を超えたら放棄)
        private static uint _artisanStallMission = 0;                     // 復旧回数を数えているミッション
        private const double ArtisanStallSeconds = 60;                    // 製作画面が開いていないときの許容秒数(レシピ選択・食事・開始は通常 10 秒以内)
        private const double ArtisanStallInCraftSeconds = 180;            // 製作画面(Synthesis)が開いているときの許容秒数(ソルバーの計算待ちを考慮)
        private const int ArtisanStallMaxRecoveries = 2;
        // 復旧時の後始末(製作の中止・スタンス解除)は、失敗しても次へ進めるよう時間制限付き・打ち切り無しで実行する
        private static readonly ECommons.Automation.NeoTaskManager.TaskManagerConfiguration CleanupTaskConfig = new(timeLimitMS: 20000, abortOnTimeout: false);

        private static bool? WaitingForArtisan()
        {
            string tag = "Craft: Waiting for Artisan";

            if (!P.Artisan.IsBusy())
            {
                _craftActionLockSince = DateTime.MinValue;
                ResetArtisanWatch();
                IceLogging.Info("Artisan is no longer running, continuing the process", tag);
                return true;
            }
            else
            {
                var now = DateTime.Now;
                if (_artisanWaitSince == DateTime.MinValue)
                {
                    _artisanWaitSince = now;
                    _lastCraftActionAt = now;
                }

                bool executing = Svc.Condition[ConditionFlag.ExecutingCraftingAction];
                bool synthOpen = AddonHelper.IsAddonActive("Synthesis");
                if (executing)
                {
                    _lastCraftActionAt = now;
                    // 実際に製作が進んでいるので、このミッションの復旧回数はリセット
                    if (_artisanStallMission == CosmicHelper.CurrentLunarMission)
                        _artisanStallRecoveries = 0;
                }

                double idle = (now - _lastCraftActionAt).TotalSeconds;
                double limit = synthOpen ? ArtisanStallInCraftSeconds : ArtisanStallSeconds;
                if (idle >= 20 && EzThrottler.Throttle("Artisan stall log", 20000))
                    IceLogging.Debug($"Artisan 待機中: 最後の製作アクションから {idle:F0} 秒(上限 {limit:F0} 秒) {ArtisanStateSummary(synthOpen)}", tag);

                if (idle >= limit)
                    return HandleArtisanStall(idle, synthOpen, tag);

                // 単一の製作アクションが異常に長くロックしている状態からの脱出。通常1アクションは数秒なので、
                // 20秒以上続く場合はハングとみなしてミッションを放棄し、棒立ちのまま止まるのを防ぐ。
                if (executing)
                {
                    if (_craftActionLockSince == DateTime.MinValue)
                        _craftActionLockSince = now;
                    else if ((now - _craftActionLockSince).TotalSeconds >= 20)
                    {
                        IceLogging.Warning("製作アクションが20秒以上ロックしています(アニメーションロックの可能性)。ミッションを放棄して復帰します", tag);
                        _craftActionLockSince = DateTime.MinValue;
                        ResetArtisanWatch();
                        SchedulerMain.State = IceState.AbandonMission;
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }
                }
                else
                {
                    _craftActionLockSince = DateTime.MinValue; // アクションの合間はリセットする
                }

                if (GenericHelpers.TryGetAddonMaster<WKSHud>(out var moonHud))
                {
                    if (!AddonHelper.IsAddonActive("WKSMissionInfomation"))
                    {
                        if (EzThrottler.Throttle("Opening mission scoring info"))
                        {
                            moonHud.Mission();
                        }
                    }
                }
            }
            return false;
        }

        private static void ResetArtisanWatch()
        {
            _artisanWaitSince = DateTime.MinValue;
            _lastCraftActionAt = DateTime.MinValue;
        }

        // 停止を検知したときの復旧。上限回数までは Artisan を止めて後始末をしてから通常の流れ(スコア確認→材料確認→再指示)に戻し、
        // 超えたらミッションを放棄する。どちらも製作画面・レシピ帳を閉じてからでないと次の操作(再指示・放棄)ができない。
        private static bool? HandleArtisanStall(double idle, bool synthOpen, string tag)
        {
            if (_artisanStallMission != CosmicHelper.CurrentLunarMission)
            {
                _artisanStallMission = CosmicHelper.CurrentLunarMission;
                _artisanStallRecoveries = 0;
            }
            _artisanStallRecoveries++;
            string state = ArtisanStateSummary(synthOpen);
            ResetArtisanWatch();
            _craftActionLockSince = DateTime.MinValue;

            if (_artisanStallRecoveries <= ArtisanStallMaxRecoveries)
            {
                IceLogging.Warning($"Artisan が {idle:F0} 秒間製作アクションを出していません。Artisan を停止して製作をやり直します({_artisanStallRecoveries}/{ArtisanStallMaxRecoveries}) {state}", tag);
                IceLogging.ChatInfo(Loc.T(synthOpen
                    ? "Artisan stopped making progress in the middle of a craft, so ICE cancelled it and will retry."
                    : "Artisan did not start crafting, so ICE stopped it and will retry the craft."), "[I.C.E.]");
                StopArtisan();
                // 後始末のあと待機タスクを終える。キューが空になると Tick が Craft 状態を再度組み立て、
                // スコア確認→材料確認→再指示(スタンス解除済みなので Artisan は最初からレシピ帳を開き直す)と流れる
                P.TaskManager.InsertMulti(
                    new(() => CancelSynthesisIfOpen(), "Cancelling stalled synthesis", CleanupTaskConfig),
                    new(() => ExitCraftingStance(), "Exiting crafting stance", CleanupTaskConfig),
                    new(() => WaitArtisanSettled(), "Waiting for Artisan to settle", CleanupTaskConfig));
                return true;
            }

            IceLogging.Warning($"Artisan の停止・再指示を {ArtisanStallMaxRecoveries} 回行っても製作が進まないため、ミッションを放棄して復帰します {state}", tag);
            IceLogging.ChatError(Loc.T("Artisan kept failing to start crafting, so the mission was abandoned."), "[I.C.E.]");
            StopArtisan();
            _artisanStallRecoveries = 0;
            P.TaskManager.Tasks.Clear();
            SchedulerMain.State = IceState.AbandonMission;
            // 製作画面やレシピ帳が開いたままだと放棄できないので、先に閉じてから Tick に放棄させる
            P.TaskManager.EnqueueMulti(
                new(() => CancelSynthesisIfOpen(), "Cancelling stalled synthesis", CleanupTaskConfig),
                new(() => ExitCraftingStance(), "Exiting crafting stance", CleanupTaskConfig));
            return true;
        }

        // ログ用: Artisan と製作関連の状態を一行にまとめる
        private static string ArtisanStateSummary(bool synthOpen)
        {
            return $"[busy={SafeArtisan(P.Artisan.IsBusy)}, endurance={SafeArtisan(P.Artisan.GetEnduranceStatus)}, list={SafeArtisan(P.Artisan.IsListRunning)}, stopReq={SafeArtisan(P.Artisan.GetStopRequest)}, "
                 + $"Crafting={Svc.Condition[ConditionFlag.Crafting]}, Preparing={Svc.Condition[ConditionFlag.PreparingToCraft]}, Executing={Svc.Condition[ConditionFlag.ExecutingCraftingAction]}, "
                 + $"製作画面={synthOpen}, レシピ帳={AddonHelper.IsAddonActive("WKSRecipeNotebook")}, 選択中={SelectedNotebookItem() ?? "-"}]";
        }

        // コスモレシピ帳で現在選択されているアイテム名(取得できなければ null)
        private static string SelectedNotebookItem()
        {
            try
            {
                if (GenericHelpers.TryGetAddonMaster<WKSRecipeNotebook>("WKSRecipeNotebook", out var notebook) && notebook.IsAddonReady)
                    return notebook.SelectedCraftingItem;
            }
            catch { }
            return null;
        }

        // Artisan の IPC 問い合わせ(Func<bool>)を安全に評価する(Artisan 未ロード時は false)
        private static bool SafeArtisan(Func<bool> f)
        {
            try { return f != null && f(); } catch { return false; }
        }

        // 固まった Artisan を止める。
        // 1) Endurance(CraftX の繰り返し)を無効化: Artisan 側で前処理タスク(レシピ選択など)も一緒に破棄される
        // 2) 停止要求→解除: 停止要求で Artisan は残った前処理を捨ててレシピ帳を閉じる(スタンス解除)。
        //    Endurance を先に止めているので、解除しても Artisan が勝手に再開することはない
        private static void StopArtisan()
        {
            try
            {
                P.Artisan.SetEnduranceStatus(false);
                P.Artisan.SetStopRequest(true);
                P.Artisan.SetStopRequest(false);
            }
            catch (Exception ex)
            {
                IceLogging.Debug($"Artisan の停止に失敗: {ex.Message}", "Craft: Waiting for Artisan");
            }
        }

        // 製作画面(Synthesis)が開いたままなら中止する(中止確認の SelectYesno は「はい」)。閉じていれば即完了。
        private static bool? CancelSynthesisIfOpen()
        {
            string tag = "Craft: Cancel synthesis";
            if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesno) && yesno.IsAddonReady)
            {
                if (EzThrottler.Throttle("Craft stall cancel yesno", 500))
                    yesno.Yes();
                return false;
            }
            if (!AddonHelper.IsAddonActive("Synthesis"))
                return true;
            if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
                return false; // アクション中は操作できない
            if (EzThrottler.Throttle("Craft stall cancel synthesis", 1000))
            {
                IceLogging.Warning("製作が進まないため製作画面を中止します", tag);
                GenericHandlers.FireCallback("Synthesis", true, -1);
            }
            return false;
        }

        // 製作スタンス(Crafting/PreparingToCraft)を抜ける。レシピ帳を閉じれば抜けられる。抜けていれば即完了。
        private static bool? ExitCraftingStance()
        {
            if (!Svc.Condition[ConditionFlag.Crafting] && !Svc.Condition[ConditionFlag.PreparingToCraft])
                return true;
            if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
                return false;
            if (AddonHelper.IsAddonActive("WKSRecipeNotebook"))
            {
                if (EzThrottler.Throttle("Craft exit stance", 1000))
                    GenericHandlers.FireCallback("WKSRecipeNotebook", true, -1);
            }
            else if (AddonHelper.IsAddonActive("RecipeNote"))
            {
                if (EzThrottler.Throttle("Craft exit stance", 1000))
                    GenericHandlers.FireCallback("RecipeNote", true, -1);
            }
            return false;
        }

        // Artisan が停止処理を終えて IsBusy=false になるのを待つ(時間制限は CleanupTaskConfig)
        private static bool? WaitArtisanSettled()
        {
            return !P.Artisan.IsBusy();
        }

        // レシピ切り替え前にスタンスを抜け始めた時刻(MinValue=未実施)
        private static DateTime _stanceExitSince = DateTime.MinValue;
        private const double StanceExitMaxSeconds = 15;

        // 製作スタンスに入ったまま(レシピ帳が開いたまま)別のレシピを Artisan に指示するべきかどうか。
        // 実機で前工程→主製作の切り替え時に Artisan がレシピ帳内での選択・開始に失敗して固まったため、
        // レシピが変わるときは一度スタンスを抜け、Artisan に最初(レシピ帳を開くところ)からやらせる。
        // 同じレシピの続き(スコア用の追加製作など)はそのままで問題ないので抜けない。
        private static bool NeedsFreshCraftStance(uint itemId)
        {
            if (!Svc.Condition[ConditionFlag.PreparingToCraft] && !Svc.Condition[ConditionFlag.Crafting])
                return false;
            if (!AddonHelper.IsAddonActive("WKSRecipeNotebook"))
                return false;
            var selected = SelectedNotebookItem();
            if (string.IsNullOrEmpty(selected))
                return true; // 何が選ばれているか分からないので安全側(開き直す)
            string targetName = "";
            try
            {
                if (Svc.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
                    targetName = item.Name.ToString();
            }
            catch { }
            if (string.IsNullOrEmpty(targetName))
                return true;
            return !string.Equals(selected.Trim(), targetName.Trim(), StringComparison.Ordinal);
        }

        private static uint throttleCounter = 0;
        private static void InsertArtisanWait(KeyValuePair<ushort, CosmicHelper.CraftingInfo> item, int amount)
        {
            P.TaskManager.InsertMulti(
                new(() => ThrottleArtisanTaskV2(item, amount), "Telling artisan to craft"),
                new(() => WaitingForArtisan(), "Waiting for artisan")
            );
        }

        private static bool? ThrottleArtisanTaskV2(KeyValuePair<ushort, CosmicHelper.CraftingInfo> item, int amount)
        {
            int delay = C.DelayCraft ? C.DelayCraftIncrease : 25;

            var craftId = item.Key;
            var recipeId = item.Value.RecipeId;
            var itemId = item.Value.ItemId;
            var expert = item.Value.ExpertCraft;

            var missionId = CosmicHelper.CurrentLunarMission;
            // ミッション境界(missionId==0)や未設定ミッションでは MissionConfig 未登録で例外になるため早期return。
            if (missionId == 0 || !C.MissionConfig.TryGetValue(missionId, out var missionConfig))
                return true;

            if (missionConfig.CraftSettings.TryGetValue(recipeId, out var recipeConfig))
            {
                if (EzThrottler.Throttle("Applying Config States", 1000))
                {
                    IceLogging.Info($"Applying config states for the following recipeID: {recipeId}");
                    P.Artisan.CheckArtisanSettings((ushort)recipeId, CosmicHelper.CurrentLunarMission, expert, Mission_Settings.Mode == ModeSelect.LevelMode);
                }
            }
            else
            {
                missionConfig.CraftSettings[recipeId] = new();
                C.Save();
            }

            if (EzThrottler.Throttle("Waiting X Amount of seconds for artisan", delay))
            {
                throttleCounter += 1;
            }

            if (throttleCounter >= 3)
            {
                // レシピを切り替えるときは製作スタンスを一度抜けてから指示する(上限秒数を過ぎたらそのまま続行)
                if (!P.Artisan.IsBusy() && NeedsFreshCraftStance(itemId))
                {
                    if (_stanceExitSince == DateTime.MinValue)
                    {
                        _stanceExitSince = DateTime.Now;
                        IceLogging.Info($"レシピ帳の選択({SelectedNotebookItem() ?? "-"})と製作対象({itemId})が異なるため、製作スタンスを一度抜けてから Artisan に指示します", "[Task Craft]");
                    }
                    if ((DateTime.Now - _stanceExitSince).TotalSeconds < StanceExitMaxSeconds)
                    {
                        ExitCraftingStance();
                        return false;
                    }
                    if (EzThrottler.Throttle("Stance exit give up", 5000))
                        IceLogging.Warning($"{StanceExitMaxSeconds:F0} 秒以内に製作スタンスを抜けられなかったため、そのまま Artisan に指示します", "[Task Craft]");
                }

                if (EzThrottler.Throttle("Artisan Crafting Task"))
                {
                    IceLogging.Debug($"Telling Artisan to craft: {itemId} -> {amount} times");
                    P.Artisan.CraftItem(craftId, amount);
                }

                if (P.TaskManager.IsBusy)
                {
                    throttleCounter = 0;
                    _stanceExitSince = DateTime.MinValue;
                    return true;
                }
            }

            return false;
        }

        private static bool? CheckMaterials()
        {
            var id = CosmicHelper.CurrentLunarMission;
            var mission = CosmicHelper.SheetMissionDict[id];

            bool provisional = mission.IsProvisional;

            if (!P.Artisan.IsBusy())
            {
                if (mission.Crafts_Pre.Count > 0)
                {
                    // Mission has pre-crafts that are required. 
                    // Checking to see if you have enough pre-crafts first
                    var preCraft = mission.Crafts_Pre.FirstOrDefault();
                    var mainCraft = mission.Crafts_Main.FirstOrDefault();

                    var preItemId = preCraft.Value.ItemId;
                    var mainItemId = mainCraft.Value.ItemId;

                    PlayerHelper.GetItemCount(preCraft.Value.ItemId, out var preItemAmount);
                    PlayerHelper.GetItemCount(preCraft.Value.RequiredItems.FirstOrDefault().Key, out var moonCrateCount);
                    PlayerHelper.GetItemCount(mainCraft.Value.ItemId, out var mainItemCount);

                    if (preItemAmount >= mainCraft.Value.RequiredItems[preItemId])
                    {
                        IceLogging.Info($"Required pre-Item count: {mainCraft.Value.RequiredItems[preItemId]} | amount necessary: {preItemAmount}");

                        // There's enough items to craft the mainhand. Telling it to craft it instead. 
                        if (mainItemCount < mainCraft.Value.RequiredAmount)
                        {
                            // you don't have enough of the pre-crafts to craft the main item. 
                            // going to tell artisan to just kick it into gear
                            var craftAmount = mainCraft.Value.RequiredAmount - mainItemCount;
                            InsertArtisanWait(mainCraft, craftAmount);
                            IceLogging.Info($"Telling artisan to craft: {mainCraft.Value.ItemId} -> {craftAmount}", "[Task Craft: Check Materials]");
                            return true;
                        }
                        else
                        {
                            // you have enough of the main hand item. But you still are crafting. So time to just craft 1 more
                            InsertArtisanWait(mainCraft, 1);
                            IceLogging.Info($"Current item count of: {mainCraft.Value.ItemId} | {mainItemCount}");
                            IceLogging.Info($"Telling artisan to craft: {mainCraft.Value.ItemId} -> 1", "[Task Craft: Check Materials]");
                            return true;
                        }

                    }
                    else if (moonCrateCount >= preCraft.Value.RequiredAmount)
                    {
                        // You should have enough to make this pre-craft. Initiating the thing now.
                        var craftAmount = preCraft.Value.RequiredAmount - preItemAmount;

                        if (mainCraft.Value.RequiredAmount > 1 && mainItemCount == 0)
                        {
                            craftAmount = mainCraft.Value.RequiredAmount * (preCraft.Value.RequiredAmount - preItemAmount);
                        }
                        if (craftAmount < 1)
                            craftAmount = 1;

                        bool SpecialExpert = preCraft.Value.ExpertCraft && provisional;
                        InsertArtisanWait(preCraft, craftAmount);
                        IceLogging.Info($"Found a material that still needed to be crafted", "[Task Craft: Check Materials]");
                        return true;
                    }
                    else
                    {
                        IceLogging.Info($"Somehow, out of mats. Need to exit. And either attempt to turnin, or just straight up abandon.", "[Task Craft: Check Materials]");
                        SchedulerMain.State = IceState.AbandonMission;
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }
                }
                else
                {
                    // This is the case when you need multiple items, or even just a single item.
                    foreach (var craft in mission.Crafts_Main)
                    {
                        PlayerHelper.GetItemCount(craft.Value.ItemId, out var reqAmount);
                        if (reqAmount < craft.Value.RequiredAmount)
                        {
                            // If you need less than what is necessary, this should change the count to be proper
                            reqAmount = craft.Value.RequiredAmount - reqAmount;

                            // Found an item that needs to be crafted. Time to check if you have enough of the material
                            var craftMaterial = ExcelHelper.RecipeSheet.GetRow(craft.Key).Ingredient[0].RowId;
                            if (PlayerHelper.GetItemCount(craftMaterial, out var itemAmount) && itemAmount >= reqAmount)
                            {
                                bool SpecialExpert = craft.Value.ExpertCraft && provisional;
                                InsertArtisanWait(craft, reqAmount);
                                IceLogging.Info($"Telling artisan to craft: {craft.Value.ItemId} -> {reqAmount}", "[Craft: No Pre-Mats]");
                                return true;
                            }
                            else
                            {
                                // You don't have enough to craft this for the mission. Exiting out and checking for score/force abandon
                                IceLogging.Info("You have no remaining items to craft the main crafting items. Going to abandon the mission now", "[Crafts: No Pre-Mats]");
                                SchedulerMain.State = IceState.AbandonMission;
                                P.TaskManager.Tasks.Clear();
                                return true;
                            }
                        }
                    }

                    var moreCraft = mission.Crafts_Main.FirstOrDefault();
                    // If you've gotten this far, that means you still need scoring. Just going to queue up the first mission (if possible)
                    // If you need less than what is necessary, this should change the count to be proper
                    var AdditionalItem = 1;

                    // Found an item that needs to be crafted. Time to check if you have enough of the material
                    var moreCraftMaterial = ExcelHelper.RecipeSheet.GetRow(moreCraft.Key).Ingredient[0].RowId;
                    if (PlayerHelper.GetItemCount(moreCraftMaterial, out var moreItemAmount) && moreItemAmount >= AdditionalItem)
                    {
                        InsertArtisanWait(moreCraft, AdditionalItem);
                        IceLogging.Info($"Telling artisan to craft: {moreCraft.Value.ItemId} -> {AdditionalItem}", "[Craft: No Pre-Mats]");
                        return true;
                    }
                    else
                    {
                        // You don't have enough to craft this for the mission. Exiting out and checking for score/force abandon
                        SchedulerMain.State = IceState.AbandonMission;
                        IceLogging.Info("You have no remaining items to craft the pre-crafts. Going to abandon the mission now", "[Crafts: No Pre-Mats]");
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }

                }
            }
            else
            {
                if (EzThrottler.Throttle("Artisan Busy Log", 3000))
                {
                    IceLogging.Debug("Artisan is currently busy... so we're properly waiting for it to finish");
                }
            }

            return false;
        }
    }
}
