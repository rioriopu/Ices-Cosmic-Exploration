using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using ICE.Ui.DebugWindowTabs;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using TerraFX.Interop.Windows;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;
using static ICE.Utilities.GatheringHelper.GatheringUtil;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_Fishing
    {
        // Something to note. 42, 43, 85 are the conditions that you get while you're fishing
        // 43 and 85 are active while you're fishing
        // 42 is active when reeling in a fish
        // Something to consider, start fishing... (that's condition 42 when you start)
        // Whenever all the conditions are cleared, check the inventory for the frame, see if you have enough/meet the score

        private static FishingDebug _fishingDebug = null;

        public static void Enqueue()
        {
            // think the process should be:
            // check score
            // if score not complete, check if can craft
            // if craft not required, fish
            // wait for fishing to be done

            P.TaskManager.EnqueueMulti
                (
                    new(() => Task_CheckScore.Fish(), "Checking Score: Fishing"),
                    new(() => Task_Gather.UseFood(), "Checking for food usage"),
                    new(() => FishingCheck(), "Checking Fishing State")
                );
        }

        private static int StartedFishing = 0;

        // 「魚たちに警戒されてしまった…少し場所を変えたほうがいい」のログ検知時にtrueになり、次サイクルで釣り場を移動する。
        public static bool WaryMoveRequested = false;

        private static unsafe bool? FishCheckV2()
        {
            string handle = "Fishing Task: State Check";
            if (Svc.Condition[ConditionFlag.Fishing])
            {
                IceLogging.Info("We're currently in the middle of fishing, so we're going to wait for us to complete");
                StartedFishing = 0;
                P.TaskManager.Enqueue(() => FinishFishing(), "Waiting for fishing to complete");
                return true;
            }
            else
            {
                IceLogging.Verbose("We're not currently fishing. Checking to see what we should do", handle);
                if (Player.Mounted || Player.IsJumping)
                {
                    if (EzThrottler.Throttle("Log message: Jump/Dismount", 1000))
                        IceLogging.Verbose("We're in the middle of dismounting/jumping, waiting");

                    Utils.Dismount();
                    return false;
                }

                if (Svc.Condition[ConditionFlag.ExecutingGatheringAction])
                {
                    if (EzThrottler.Throttle("Gathering Action Execution", 2000))
                        IceLogging.Info("We're currently executing some gathering action, so waiting");

                    return false;
                }

                // プリセット指定エサを優先して装備対象を決定(無ければMoonBaits先頭)。
                uint firstBait = GetPreferredBait();
                bool hasBait = firstBait != 0;

                if (!hasBait)
                {
                    IceLogging.Info("We are reporting to be out of bait, proceeding to abandon/turnin mission");
                    SchedulerMain.State = IceState.AbandonMission;
                    return true;
                }
                // 未装備、または装備中エサがプリセット指定に適合しない場合は、指定エサへ切り替える。
                if (!IsCurrentBaitAcceptable())
                {
                    if (EzThrottler.Throttle("Bait Message", 2000))
                        IceLogging.Debug($"装備中エサがプリセットに適合しない(または未装備)ため、指定エサを装備します: [{firstBait}] (現在:{CosmicHelper.CurrentBait})", handle);
                    P.AutoHook.SwapBaitById(firstBait);
                    return false;
                }

                if (CosmicHelper.CurrentMissionInfo.Attributes.HasFlag(MissionAttributes.Collectables))
                {
                    if (!PlayerHelper.HasStatusId(805))
                    {
                        if (EzThrottler.Throttle("Log Throttle for fishing", 2000))
                            IceLogging.Debug("We need to apply collector's glove, so we're doing so", handle);

                        if (!Player.IsBusy)
                        {
                            if (EzThrottler.Throttle("Attempting to turn on collectability"))
                                ActionManager.Instance()->UseAction(ActionType.Action, 4101);
                        }
                        return false;
                    }
                }
                if (_fishingDebug == null)
                {
                    _fishingDebug = new FishingDebug();
                }

                // 「魚たちに警戒されてしまった…」を検知した場合、別の釣り場へ移動する(同フラグ内の次スポットへ)。
                if (WaryMoveRequested)
                {
                    WaryMoveRequested = false;
                    if (P.AutoHook.Installed)
                        P.AutoHook.SetPluginState(false);
                    var waryMission = CosmicHelper.CurrentMissionInfo;
                    var waryNext = GetNextFishingSpot(waryMission.TerritoryId, waryMission.MapPosition, Player.Position);
                    if (waryNext != null)
                    {
                        IceLogging.Info($"魚が警戒したため、別の釣り場へ移動します: {waryNext.FishingSpot}", handle);
                        P.TaskManager.Tasks.Clear();
                        P.TaskManager.Enqueue(() => InitiateMoving(waryNext.FishingSpot), "Vnav moving (wary relocate)");
                        return true;
                    }
                    IceLogging.Info("魚が警戒しましたが、移動先の登録座標が無いため現在地で釣りを継続します。", handle);
                }

                if (_fishingDebug.IsFishable())
                {
                    var currentSpot = GetCurrentFishingSpot();
                    if (EzThrottler.Throttle("Fishable Location message"))
                    {
                        if (currentSpot != null)
                        {
                            IceLogging.Verbose("We were told we're in a fishable location, so going to report back we are suppose to be able to fish\n" +
                                               $"Current spot: {currentSpot.FishingSpot:N2}", handle);
                        }
                        else
                        {
                            IceLogging.Verbose($"We are currently in a fishable spot... but the data isn't loaded correctly? MissionID: {CosmicHelper.CurrentLunarMission}", handle);
                        }
                    }

                    if (EzThrottler.Throttle("Start Fishing: AH", 2000))
                    {
                        IceLogging.Verbose("We are telling autohook to start fishing via command...", handle);
                        P.AutoHook.SetPluginState(true);
                        Svc.Commands.ProcessCommand("/ahstart");
                    }

                    if (EzThrottler.Throttle("Started Fishing Throttle", 1000))
                    {
                        StartedFishing += 1;
                        IceLogging.Verbose($"+1 to waiting for fishing to actually start... {StartedFishing}", handle);
                    }
                    if (StartedFishing > 4)
                    {
                        if (EzThrottler.Throttle("Start fishing Error", 2000))
                        {
                            IceLogging.Error("We apperently... didn't start fishing. Which isn't good. Checking to see if we have bait", handle);
                            P.AutoHook.SwapBaitById(firstBait);
                        }

                        if (EzThrottler.Throttle("Attempting to turn on collectability"))
                            ActionManager.Instance()->UseAction(ActionType.Action, 4101);
                    }
                }
                else
                {
                    IceLogging.Verbose("We apperently aren't facing toward the fishing hole... or not close enough to one that we can actually start. So going to attempt to fix it", handle);
                    if (_fishingDebug.FindFishableLocation(out var fishablePos, searchSteps: 64))
                    {
                        IceLogging.Info("We're not in a fishable angle, so going to face one", handle);
                        P.TaskManager.Enqueue(() => FacePosition(fishablePos.Value));
                        return true;
                    }
                    else
                    {
                        IceLogging.Debug("Our current fishing position isn't viable. So going to move to the next fishing spot");
                        var mission = CosmicHelper.CurrentMissionInfo;
                        var flag = mission.MapPosition;
                        var territoryId = mission.TerritoryId;

                        var nextFishingSpot = GetNextFishingSpot(territoryId, flag, Player.Position);
                        if (nextFishingSpot != null)
                        {
                            IceLogging.Info($"We found another fishing spot to move to! {nextFishingSpot.FishingSpot} | moving to it");
                            P.TaskManager.Tasks.Clear();
                            P.TaskManager.Enqueue(() => InitiateMoving(nextFishingSpot.FishingSpot), "Vnav moving to fishing");
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        private static int BaitCounter = 0;
        // 餌切替の試行回数と上限。切替が状態(WKS.State.FishingBait)に反映されず IsCurrentBaitAcceptable が
        // 永久に false になる無限ループ(=キャストしない)を防ぐための保険。上限到達で手動装備を促し停止する。
        private static int BaitSwapAttempts = 0;
        private const int BaitSwapMaxAttempts = 8;

        /// <summary>
        /// 指定エサ(アイテムID)が現在の釣り場の swimbait リストに在れば、そのインデックスで AutoHook に選択させる。
        /// コスモ探査の改良コスモエサ等は swimbait のため、通常餌用の SwapBaitById では装備できない(内部NRE)。
        /// swimbait のアイテムID配列は FishingEventHandler.SwimBaitItemIds(最大3枠)から取得する。
        /// </summary>
        /// <returns>true=swimbait として選択を発行した / false=swimbait ではない(通常餌処理へフォールバック)</returns>
        private static unsafe bool TrySelectSwimbait(uint desiredItemId)
        {
            try
            {
                var ef = EventFramework.Instance();
                if (ef == null)
                    return false;
                var fishing = ef->EventHandlerModule.FishingEventHandler;
                if (fishing == null)
                    return false;

                var ids = fishing->SwimBaitItemIds; // Span<uint>(最大3枠)
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == desiredItemId)
                    {
                        bool ok = P.AutoHook.SwapSwimbaitByIndex((byte)i);
                        IceLogging.Debug($"swimbait選択: index={i} itemId={desiredItemId} 結果={ok}");
                        return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                IceLogging.Error($"TrySelectSwimbait 例外: {e.Message}");
                return false;
            }
        }

        private static unsafe bool? FishingCheck()
        {
            if (_fishingDebug == null)
            {
                _fishingDebug = new FishingDebug();
            }

            if (Player.Mounted)
            {
                Utils.Dismount();
                return false;
            }

            string handle = "[Standard Fishing: Fishing Check]";
            if (EzThrottler.Throttle("Throttling intro message", 1000))
            {
                IceLogging.Debug("Checking to see where we need to be here", handle);
            }
            bool hasBait = false;

            // 未装備、または装備中エサがプリセット指定に適合しない場合は、指定エサへ切り替える。
            if (!IsCurrentBaitAcceptable())
            {
                // プリセット指定エサを優先装備。
                var preferred = GetPreferredBait();
                if (preferred == 0)
                {
                    IceLogging.Info("If we've gotten here, that means we're out of bait. Proceeding to turnin/abandon the mission");
                    SchedulerMain.State = IceState.AbandonMission;
                    P.TaskManager.Tasks.Clear();
                    return true;
                }

                // 無限ループ防止(保険): 規定回数試しても指定エサが状態に反映されないなら、手動装備を促して停止。
                // swimbait の選択が反映されない環境などで延々と待ち続ける(=キャストしない)のを避ける。
                if (BaitSwapAttempts >= BaitSwapMaxAttempts)
                {
                    IceLogging.ChatInfo($"指定エサ(ID:{preferred})を自動で装備できませんでした。お手数ですが手動で装備してください。ICEを一時停止します。", "[I.C.E.]");
                    BaitSwapAttempts = 0;
                    SchedulerMain.State = IceState.Idle;
                    P.TaskManager.Tasks.Clear();
                    return true;
                }

                if (EzThrottler.Throttle("Equipping bait", 2000))
                {
                    BaitSwapAttempts++;
                    // まず swimbait(コスモ改良エサ等)として選択を試み、swimbait でなければ通常餌APIへフォールバック。
                    if (!TrySelectSwimbait(preferred))
                    {
                        P.AutoHook.SwapBaitById(preferred);
                        IceLogging.Debug($"通常餌を装備します(試行{BaitSwapAttempts}/{BaitSwapMaxAttempts}): {preferred} (現在:{CosmicHelper.CurrentBait})", handle);
                    }
                }
                return false;
            }
            else if (BaitSwapAttempts != 0)
            {
                // エサが適合したら試行カウンタをリセット。
                BaitSwapAttempts = 0;
            }

            // 指定エサ(支給エサ/マスターは改良エサ)が入手可能か確認する。指定エサが尽きていれば
            // GetPreferredBaitは0を返すため、釣り継続不可としてミッションを破棄する(別エサで代用しない)。
            hasBait = GetPreferredBait() != 0;
            if (hasBait && EzThrottler.Throttle("Throttling bait message", 1000))
                IceLogging.Debug("We have the bait! Continuing onwards");

            if (!hasBait)
            {
                IceLogging.Info("指定エサが尽きたため釣り継続不可。ミッションを破棄します。");
                SchedulerMain.State = IceState.AbandonMission;
                P.TaskManager.Tasks.Clear();
                return true;
            }
            else if (CosmicHelper.CurrentMissionInfo.Attributes.HasFlag(MissionAttributes.Collectables) && !PlayerHelper.HasStatusId(805))
            {
                if (EzThrottler.Throttle("Log Throttle for fishing"))
                    IceLogging.Debug("We need to apply collector's glove", "Task_Start Fishing");

                if (!Player.IsBusy)
                {
                    if (EzThrottler.Throttle("Attempting to turn on collectability"))
                        ActionManager.Instance()->UseAction(ActionType.Action, 4101);
                }
                return false;
            }
            else if (!Svc.Condition[ConditionFlag.Gathering])
            {
                if (!_fishingDebug.IsFishable())
                {
                    if (_fishingDebug.FindFishableLocation(out var fishablePos, searchSteps: 64))
                    {
                        IceLogging.Info("We're not in a fishable angle, so going to face one", handle);
                        P.TaskManager.Tasks.Clear();
                        P.TaskManager.Enqueue(() => FacePosition(fishablePos.Value));
                        return true;
                    }
                    else
                    {
                        IceLogging.Debug("Our current fishing position isn't viable. So going to move to the next fishing spot");
                        var mission = CosmicHelper.CurrentMissionInfo;
                        var flag = mission.MapPosition;
                        var territoryId = mission.TerritoryId;

                        var nextFishingSpot = GetNextFishingSpot(territoryId, flag, Player.Position);
                        if (nextFishingSpot != null)
                        {
                            IceLogging.Info($"We found another fishing spot to move to! {nextFishingSpot.FishingSpot} | moving to it");
                            P.TaskManager.Tasks.Clear();
                            P.TaskManager.Enqueue(() => InitiateMoving(nextFishingSpot.FishingSpot), "Vnav moving to fishing");
                            return true;
                        }
                    }
                }
                else if (EzThrottler.Throttle("Starting to fish", 1000))
                {
                    IceLogging.Debug("Telling it to start fishing", handle);
                    // ActionManager.Instance()->UseAction(ActionType.Action, 289);
                    Svc.Commands.ProcessCommand("/ahstart");
                }
                else if (EzThrottler.Throttle("Adding counter for bait not equipped"))
                {
                    BaitCounter++;
                    IceLogging.Debug($"Adding 1 to the counter. Counter is at: {BaitCounter}");
                    if (BaitCounter >= 2)
                    {
                        // プリセット指定エサを優先装備。
                        var preferred = GetPreferredBait();
                        if (preferred != 0)
                        {
                            P.AutoHook.SwapBaitById(preferred);
                            IceLogging.Debug($"Telling it to equip bait ID: {preferred}", handle);
                            return false;
                        }
                    }
                }
                return false;
            }
            else
            {
                // Means we are fishing, all we need to do is enable autohook then wait for us to get the amount of fish we need
                P.AutoHook.SetPluginState(true);
                IceLogging.Info("We're starting to fish. So kicking it over to checking the fish items", handle);
                P.TaskManager.Insert(() => FinishFishing(), "Waiting till we actually start fishing", Utils.TaskConfig);
                BaitCounter = 0;
                BaitSwapAttempts = 0;
                return true;
            }
        }

        private static int collectableCounter = 0;
        private static unsafe bool? FinishFishing()
        {
            if (!Svc.Condition[ConditionFlag.Fishing])
            {
                IceLogging.Info("We're done fishing, time to go back to the score check", "[Fishing: Finished]");
                return true;
            }
            else
            {
                if (GenericHelpers.TryGetAddonMaster<SelectYesno>("SelectYesno", out var yesNo) && yesNo.IsAddonReady)
                {
                    if (EzThrottler.Throttle("Adding +1 to counter", 250))
                    {
                        collectableCounter += 1;
                    }
                    if (collectableCounter >= 2)
                    {
                        if (EzThrottler.Throttle("Selecting yes to collectables"))
                        {
                            yesNo.Yes();
                        }
                        return false;
                    }
                    return false;
                }
            }
            if (collectableCounter != 0)
                collectableCounter = 0;

            return false;
        }
        public static unsafe bool? FacePosition(Vector3 pos, float tolerance = 0.1f)
        {
            float currentRotation = Player.Rotation;

            // If rotation is still changing, wait for it to stabilize
            if (Math.Abs(Player.Rotation - currentRotation) > tolerance)
            {
                return false;
            }

            Vector3 direction = pos - Player.Position;
            float targetRotation = (float)Math.Atan2(direction.X, direction.Z);

            float angleDifference = GetShortestAngleDifference(Player.Rotation, targetRotation);

            if (Math.Abs(angleDifference) < tolerance)
            {
                return true;
            }

            if (EzThrottler.Throttle("Facing toward the fishing hole"))
            {
                var fwk = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();

                var autoRotateConfig = fwk->SystemConfig.GetConfigOption((uint)ConfigOption.AutoFaceTargetOnAction);
                var autoRotateOriginal = autoRotateConfig->Value.UInt;

                autoRotateConfig->Value.UInt = 1;

                IceLogging.Debug($"Telling the game to face you to: {pos}");
                Vector3 temp = pos;
                ActionManager.Instance()->AutoFaceTargetPosition(&temp);

                autoRotateConfig->Value.UInt = autoRotateOriginal;
            }

            return false;
        }
        public static float GetShortestAngleDifference(float currentAngle, float targetAngle)
        {
            float difference = targetAngle - currentAngle;

            // Normalize to [-π, π] for shortest path
            while (difference > Math.PI) difference -= (float)(2 * Math.PI);
            while (difference < -Math.PI) difference += (float)(2 * Math.PI);

            return difference;
        }
        // 装備すべきエサを決定する。現行ミッションのAutoHookプリセットが具体的なエサ(PresetBaitIds)を指定している
        // 場合は、所持しているそのエサを優先する(例: マスター「植物魚の多様性調査」=改良コスモカゲロウ)。
        // これにより、コスモカゲロウと改良コスモカゲロウが両方配布されても、プリセット指定の改良を装備し、
        // 別エサ装備によるAutoHookのグローバルプリセット落ちを防ぐ。
        // 汎用(All Baits)プリセットのミッションは指定が広いため対象外とし、従来どおりMoonBaits先頭の所持エサを返す。
        // 該当エサが無ければMoonBaitsの先頭所持エサ、所持エサが皆無なら0を返す。
        private static uint GetPreferredBait()
        {
            var missionId = CosmicHelper.CurrentLunarMission;
            // ミッション別エサ上書き(プリセットは本家のまま・装備エサだけ変更)を最優先で判定。
            // 例: 1676 は改良コスモリーチではなく通常コスモリーチ(52248)を使う。指定エサが尽きたら0で破棄。
            if (missionId != 0 && GatheringUtil.FishingBaitOverride.TryGetValue(missionId, out var overrideBait))
                return (PlayerHelper.GetItemCount(overrideBait, out var oc) && oc > 0) ? overrideBait : 0;
            // マスターミッションは配布される改良コスモエサのみを使用。改良エサが尽きたら0を返し、釣り継続不可として破棄させる。
            if (missionId != 0 && GatheringUtil.MasterFishingMissions.Contains(missionId))
            {
                foreach (var bid in GatheringUtil.ImprovedCosmoBaits)
                {
                    if (PlayerHelper.GetItemCount(bid, out var c) && c > 0)
                        return bid;
                }
                return 0;
            }
            // プリセットが具体的エサを指定しているミッションは、その指定(支給)エサのみを使用。
            // 指定エサが尽きたら0を返し、釣り継続不可としてミッションを破棄させる(別エサで代用しない)。
            if (missionId != 0
                && CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mi)
                && mi.PresetBaitIds.Count > 0)
            {
                foreach (var bid in mi.PresetBaitIds)
                {
                    if (PlayerHelper.GetItemCount(bid, out var c) && c > 0)
                        return bid;
                }
                return 0;
            }
            // 指定エサが無いミッションのみ、MoonBaitsの先頭所持エサで代用する。
            foreach (var bait in GatheringUtil.MoonBaits)
            {
                foreach (var baitId in bait.Value)
                {
                    if (PlayerHelper.GetItemCount(baitId, out var c) && c > 0)
                        return baitId;
                }
            }
            return 0;
        }
        // 現在装備中のエサが、現行ミッションのプリセットに適合しているか判定する。
        // 未装備(0)は不適合(=装備が必要)。汎用(All Baits)プリセットのミッションは全エサ網羅のため常に適合。
        // 具体的エサ指定(PresetBaitIds)のミッションは、装備中エサがその指定に含まれる場合のみ適合。
        // これにより、別エサ(例: コスモカゲロウ)が先に装備されていても、プリセット指定エサ(改良コスモカゲロウ)へ
        // 切り替えるよう促し、AutoHookのグローバルプリセット落ちを防ぐ。
        private static bool IsCurrentBaitAcceptable()
        {
            var current = CosmicHelper.CurrentBait ?? 0;
            if (current == 0)
                return false;
            var missionId = CosmicHelper.CurrentLunarMission;
            if (missionId == 0)
                return true;
            // ミッション別エサ上書き(例:1676=コスモリーチ)は、その指定エサのみ適合(最優先)。
            if (GatheringUtil.FishingBaitOverride.TryGetValue(missionId, out var overrideBait))
                return current == overrideBait;
            // マスターは改良コスモエサのみ適合(別エサが装備されていたら切り替えさせる)。
            if (GatheringUtil.MasterFishingMissions.Contains(missionId))
                return GatheringUtil.ImprovedCosmoBaits.Contains(current);
            // 具体的エサ指定(支給エサ)のミッションは、装備中エサがその指定に含まれる場合のみ適合。
            if (CosmicHelper.SheetMissionDict.TryGetValue(missionId, out var mi) && mi.PresetBaitIds.Count > 0)
                return mi.PresetBaitIds.Contains(current);
            return true;
        }
        public static FisherSpotInfo? GetNextFishingSpot(uint zone, Vector2 flag, Vector3 playerPosition)
        {
            // Check if the zone and flag exist
            if (!MoonFishingLocations.TryGetValue(zone, out var zoneData) ||
                !zoneData.TryGetValue(flag, out var spots) ||
                spots.Count == 0)
            {
                return null;
            }

            // Find the index of the spot close to the player (within distance of 2)
            int currentIndex = -1;
            for (int i = 0; i < spots.Count; i++)
            {
                float distance = Vector3.Distance(playerPosition, spots[i].FacePosition);
                if (distance < 2f)
                {
                    currentIndex = i;
                    break;
                }
            }

            // If a close spot was found, return the next one (cycling back to 0 if at the end)
            if (currentIndex != -1)
            {
                int nextIndex = (currentIndex + 1) % spots.Count;
                return spots[nextIndex];
            }

            // If no close spot found, return the first entry
            return spots[0];
        }
        public static FisherSpotInfo? GetCurrentFishingSpot()
        {
            var zone = Player.Territory.RowId;
            var mission = CosmicHelper.CurrentMissionInfo;
            var flag = mission.MapPosition;

            // Check if the zone and flag exist
            if (!MoonFishingLocations.TryGetValue(zone, out var zoneData) || !zoneData.TryGetValue(flag, out var spots) || spots.Count == 0)
            {
                return null;
            }

            var closestSpot = spots.MinBy(x => Player.DistanceTo(x.FishingSpot));
            return closestSpot;
        }
        public static bool? InitiateMoving(Vector3 fishingPos)
        {
            if (!P.Navmesh.IsReady())
            {
                Utils.VnavBuildInfo();
                return false;
            }
            else if (P.Navmesh.IsRunning())
            {
                P.TaskManager.Enqueue(() => !P.Navmesh.IsRunning());
                return true;
            }
            else
            {
                if (EzThrottler.Throttle("Navmesh movement"))
                {
                    IceLogging.DestinationLogs.Log(fishingPos);
                    P.Navmesh.PathfindAndMoveTo(fishingPos, false);
                }
                return false;
            }
        }
    }
}
