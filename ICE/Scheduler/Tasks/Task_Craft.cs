using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ICE.Utilities.Cosmic_Helper;
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

        // Artisan が「動作中(IsBusy)」なのに製作状態(Crafting/PreparingToCraft 等)に一度も入らない時間の起点。
        // Artisan 側がレシピ選択や開始に失敗して固まると IsBusy=true のまま何も起きず、ICE も無言で待ち続けてしまう
        // (実機: 「Telling Artisan to craft」の直後にログが途切れて停止)。一定時間で Artisan を止めて製作をやり直す。
        private static DateTime _artisanNotCraftingSince = DateTime.MinValue;
        private static int _artisanStallRecoveries = 0;      // 同一ミッション内での復旧回数(上限を超えたら放棄)
        private static uint _artisanStallMission = 0;         // 復旧回数を数えているミッション
        private const double ArtisanStallSeconds = 60;       // 通常は指示後 10 秒以内に製作が始まる(x3 製作の合間も数秒)
        private const int ArtisanStallMaxRecoveries = 2;

        private static bool? WaitingForArtisan()
        {
            string tag = "Craft: Waiting for Artisan";

            if (!P.Artisan.IsBusy())
            {
                _craftActionLockSince = DateTime.MinValue;
                _artisanNotCraftingSince = DateTime.MinValue;
                IceLogging.Info("Artisan is no longer running, continuing the process", tag);
                return true;
            }
            else
            {
                // Artisan が動作中なのに製作が始まらない状態の監視
                bool craftingNow = Svc.Condition[ConditionFlag.Crafting]
                                   || Svc.Condition[ConditionFlag.PreparingToCraft]
                                   || Svc.Condition[ConditionFlag.ExecutingCraftingAction];
                if (craftingNow)
                {
                    _artisanNotCraftingSince = DateTime.MinValue;
                    // 実際に製作できているので復旧回数はリセット
                    if (_artisanStallMission == CosmicHelper.CurrentLunarMission)
                        _artisanStallRecoveries = 0;
                }
                else
                {
                    if (_artisanNotCraftingSince == DateTime.MinValue)
                        _artisanNotCraftingSince = DateTime.Now;
                    double idle = (DateTime.Now - _artisanNotCraftingSince).TotalSeconds;
                    if (idle >= 20 && EzThrottler.Throttle("Artisan stall verbose", 20000))
                        IceLogging.Verbose($"Artisan 待機中 {idle:F0} 秒: busy=true, endurance={SafeArtisan(P.Artisan.GetEnduranceStatus)}, list={SafeArtisan(P.Artisan.IsListRunning)}, 製作状態=なし", tag);
                    if (idle >= ArtisanStallSeconds)
                    {
                        _artisanNotCraftingSince = DateTime.MinValue;
                        if (_artisanStallMission != CosmicHelper.CurrentLunarMission)
                        {
                            _artisanStallMission = CosmicHelper.CurrentLunarMission;
                            _artisanStallRecoveries = 0;
                        }
                        _artisanStallRecoveries++;
                        bool endurance = SafeArtisan(P.Artisan.GetEnduranceStatus);
                        bool list = SafeArtisan(P.Artisan.IsListRunning);
                        if (_artisanStallRecoveries <= ArtisanStallMaxRecoveries)
                        {
                            IceLogging.Warning($"Artisan が {ArtisanStallSeconds:F0} 秒以上「動作中」のまま製作を始めません(endurance={endurance}, list={list})。Artisan を停止して製作をやり直します({_artisanStallRecoveries}/{ArtisanStallMaxRecoveries})", tag);
                            IceLogging.ChatInfo(Loc.T("Artisan did not start crafting, so ICE stopped it and will retry the craft."), "[I.C.E.]");
                            StopArtisan(endurance, list);
                            // 次のフレームで IsBusy=false になれば上の分岐で「no longer running」→ スコア確認 → 材料確認 → 再指示、と流れる
                            return false;
                        }
                        IceLogging.Warning($"Artisan の停止・再指示を {ArtisanStallMaxRecoveries} 回行っても製作が始まらないため、ミッションを放棄して復帰します", tag);
                        IceLogging.ChatError(Loc.T("Artisan kept failing to start crafting, so the mission was abandoned."), "[I.C.E.]");
                        StopArtisan(endurance, list);
                        _artisanStallRecoveries = 0;
                        SchedulerMain.State = IceState.AbandonMission;
                        P.TaskManager.Tasks.Clear();
                        return true;
                    }
                }

                // 単一の製作アクションが異常に長くロックしている状態からの脱出。通常1アクションは数秒なので、
                // 20秒以上続く場合はハングとみなしてミッションを放棄し、棒立ちのまま止まるのを防ぐ。
                if (Svc.Condition[ConditionFlag.ExecutingCraftingAction])
                {
                    if (_craftActionLockSince == DateTime.MinValue)
                        _craftActionLockSince = DateTime.Now;
                    else if ((DateTime.Now - _craftActionLockSince).TotalSeconds >= 20)
                    {
                        IceLogging.Warning("製作アクションが20秒以上ロックしています(アニメーションロックの可能性)。ミッションを放棄して復帰します", tag);
                        _craftActionLockSince = DateTime.MinValue;
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

        // Artisan の IPC 問い合わせ(Func<bool>)を安全に評価する(Artisan 未ロード時は false)
        private static bool SafeArtisan(Func<bool> f)
        {
            try { return f != null && f(); } catch { return false; }
        }

        // 固まった Artisan を止める。Endurance(CraftX)を無効化し、リストが走っていれば停止要求を出す。
        private static void StopArtisan(bool endurance, bool list)
        {
            try
            {
                if (endurance)
                    P.Artisan.SetEnduranceStatus(false);
                if (list)
                {
                    P.Artisan.SetStopRequest(true);
                    P.Artisan.SetStopRequest(false);
                }
            }
            catch (Exception ex)
            {
                IceLogging.Debug($"Artisan の停止に失敗: {ex.Message}", "Craft: Waiting for Artisan");
            }
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
                if (EzThrottler.Throttle("Artisan Crafting Task"))
                {
                    IceLogging.Debug($"Telling Artisan to craft: {itemId} -> {amount} times");
                    P.Artisan.CraftItem(craftId, amount);
                }

                if (P.TaskManager.IsBusy)
                {
                    throttleCounter = 0;
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
