using ECommons.GameHelpers;
using ICE.Utilities.Cosmic_Helper;

namespace ICE.Scheduler.Tasks
{
    internal static class Task_ExecuteMission
    {
        public static void Enqueue()
        {
            P.TaskManager.Enqueue(() => ExecuteMission(), "Finding proper mission state");
        }

        private static bool? ExecuteMission()
        {
            if (CosmicHelper.CurrentLunarMission != 0)
            {
                var missionId = CosmicHelper.CurrentLunarMission;
                P.MissionTimer.StartMission(missionId);
                Task_Craft.OnMissionStarted(missionId); // 同じミッション ID を続けて受けても、製作の停滞回数を持ち越さない

                var mission = CosmicHelper.SheetMissionDict[missionId];
                bool fishingMission = mission.Jobs.Contains(18);
                bool gatherMission = mission.Jobs.Contains(16) || mission.Jobs.Contains(17);
                bool craftMission = mission.Jobs.Any(x => CosmicHelper.CrafterJobList.Contains(x));

                C.MissionConfig.TryGetValue(missionId, out var config);
                bool dualClass = (gatherMission && craftMission) || (fishingMission && craftMission);

                bool notUpdatedFisher = !P.AutoHook.UpdatedPlugin() && CosmicMoonRegistry.Auxesia.TerritoryId == Player.Territory.RowId && mission.Jobs.Contains(18);

                if (C.OnlyGrabMission_Debug || UnsupportedMissions.Ids.Contains(missionId) || notUpdatedFisher)
                {
                    if (notUpdatedFisher && P.AutoHook.Installed)
                    {
                        string message = $"[I.C.E.] You didn't read the little warning in the mission setup\n" +
                            "You need to update autohook for you to be able to fish here on Auxesia.\n" +
                            "Please swap to testing version";
                        IceLogging.Error($"{message}", "Execute Mission");
                        Svc.Chat.Print(new()
                        {
                            Type = Dalamud.Game.Text.XivChatType.ErrorMessage,
                            Message = message,
                        });
                        Svc.Toasts.ShowError($"{message}");
                    }
                    SchedulerMain.State = IceState.ManualMode;
                }
                else if (dualClass)
                {
                    IceLogging.Info("We've found a dual class mission! Kicking it off with that.", "[Task: Execute Mission]");
                    SchedulerMain.State = IceState.DualClass;
                    if (fishingMission)
                    {
                        if (config.Use_BuildinPreset)
                        {
							IceLogging.Debug("Use Built-In Presets Checked. Resetting/Importing presets.");
                            P.AutoHook.DeleteAllAnonymousPresets();
                            FishingTask(missionId);
                        }
						else
						{
							IceLogging.Debug("Use Built-In Presets Unchecked. Setting configured preset.");
							string presetName = C.MissionConfig[CosmicHelper.CurrentLunarMission].AutoHookPresetName;
							P.AutoHook.SetPreset(presetName);
						}
                    }
                }
                else if (fishingMission)
                {
                    // Check exist twice, one here is to actually enable the fishing profile that is selected.
                    var missionConfig = C.MissionConfig[missionId];
                    if (missionConfig.Use_BuildinPreset)
                    {
                        // Using the build in presets that are included in the plugin.
						IceLogging.Debug("Use Built-In Presets Checked. Resetting/Importing presets.");
                        P.AutoHook.DeleteAllAnonymousPresets();
                        FishingTask(missionId);
                    }
                    else
                    {
						IceLogging.Debug("Use Built-In Presets Unchecked. Setting configured preset.");
                        string presetName = missionConfig.AutoHookPresetName;
                        P.AutoHook.SetPreset(presetName);
                    }

                    SchedulerMain.State = IceState.Fish;
                    IceLogging.Debug("Mission is a fishing mission, so going to the fishing task");
                }
                else if (gatherMission)
                {
                    SchedulerMain.State = IceState.Gather;
                    IceLogging.Info("Mission is a gathering mission. Need to gather inial resources. But first going to do a check to make sure where we're at.", "[Task_ExecuteMission]");
                }
                else if (craftMission)
                {
                    IceLogging.Debug("Mission is purely a crafting mission (yay), checking current state next", "[Task_ExecuteMission]");
                    SchedulerMain.State = IceState.Craft;
                }
            }
            else if (CosmicHelper.CurrentLunarMission == 0)
            {
                IceLogging.Debug("Hmm... somehow we got in this state. And we shouldn't be? Returning back to the grab mission state");
                SchedulerMain.State = IceState.GrabMission;
            }

            return true;
        }

        public static void FishingTask(uint missionId)
        {
            P.TaskManager.Enqueue(() => ClearFishingPreset(), "Clearing All Fishing Presets");
            P.TaskManager.EnqueueDelay(150);
            P.TaskManager.Enqueue(() => ImportPresetsSequentially(missionId));
            // 投入したプリセットが本当に「選択中」になったかを確かめる(なっていなければ再投入)。
            // AutoHook は選択中プリセットで釣るが、画面中央の編集ビューは起動時の表示(Global Preset)が残るため、
            // 何が使われているかをログ/チャットで示して混乱を防ぐ(2026-09-30 ユーザー報告)。
            P.TaskManager.EnqueueDelay(300);
            P.TaskManager.Enqueue(() => VerifyFishingPreset(missionId), "Verifying AutoHook preset");
        }

        public enum PresetCheck { Ok, Mismatch, Unknown }

        private static uint _presetNotifiedMission = 0;
        private static uint _presetRetryMission = 0;
        private static int _presetRetry = 0;
        private const int PresetMaxRetry = 2;

        /// <summary>
        /// AutoHook の選択中プリセットが、このミッションに期待するもの(内蔵=anon_[ミッションID]…、任意指定=設定名)か。
        /// Unknown = 反射で読めない(AutoHook 未導入や内部構造の変更)。その場合は従来どおり信じて進める。
        /// </summary>
        public static PresetCheck CheckFishingPreset(uint missionId, out string selected, out string expected)
        {
            selected = null;
            expected = "";
            if (!P.AutoHook.Installed || !P.AutoHook.TryGetSelectedPresetName(out selected))
                return PresetCheck.Unknown;

            C.MissionConfig.TryGetValue(missionId, out var cfg);
            bool builtin = cfg == null || cfg.Use_BuildinPreset;
            if (builtin)
            {
                expected = $"anon_[{missionId}]";
                // 内蔵プリセット名は "[ID] ミッション名" で、匿名化で anon_ が付く。フォルダ型は先頭プリセットが選ばれるので anon_ なら良しとする
                if (selected != null && (selected.Contains($"[{missionId}]") || selected.StartsWith("anon_", StringComparison.Ordinal)))
                    return PresetCheck.Ok;
                return PresetCheck.Mismatch;
            }
            expected = cfg.AutoHookPresetName ?? "";
            if (string.IsNullOrEmpty(expected))
                return PresetCheck.Unknown; // 指定無し → 判定しない
            return selected == expected ? PresetCheck.Ok : PresetCheck.Mismatch;
        }

        private static bool? VerifyFishingPreset(uint missionId)
        {
            if (CosmicHelper.CurrentLunarMission == 0)
                return true;
            var result = CheckFishingPreset(missionId, out var selected, out var expected);
            string shown = selected ?? "Global Preset";
            switch (result)
            {
                case PresetCheck.Ok:
                    if (_presetRetryMission == missionId) _presetRetry = 0;
                    IceLogging.Info($"AutoHook の選択中プリセット: '{shown}'(このミッション用)", "[AH Import]");
                    if (_presetNotifiedMission != missionId)
                    {
                        _presetNotifiedMission = missionId;
                        IceLogging.ChatInfo($"{Loc.T("AutoHook preset in use:")} {shown}", "[I.C.E.]");
                    }
                    return true;
                case PresetCheck.Unknown:
                    IceLogging.Debug("AutoHook の選択中プリセットを確認できないため、投入結果を信じて進めます", "[AH Import]");
                    return true;
                default:
                    if (!TryScheduleFishingPresetRetry(missionId, shown, expected))
                        IceLogging.ChatError($"{Loc.T("AutoHook did not select the fishing preset for this mission, so fishing will use:")} {shown}", "[I.C.E.]");
                    return true;
            }
        }

        // 期待するプリセットが選ばれていないとき、上限回数まで投入をやり直す。戻り値 false = 上限に達した(諦める)。
        private static bool TryScheduleFishingPresetRetry(uint missionId, string shown, string expected)
        {
            if (_presetRetryMission != missionId)
            {
                _presetRetryMission = missionId;
                _presetRetry = 0;
            }
            if (_presetRetry >= PresetMaxRetry)
                return false;
            _presetRetry++;
            IceLogging.Warning($"AutoHook の選択中プリセットが '{shown}' で、期待する '{expected}…' ではありません。プリセットを再投入します({_presetRetry}/{PresetMaxRetry})", "[AH Import]");
            FishingTask(missionId);
            return true;
        }

        /// <summary>
        /// 釣り開始前の確認。期待するプリセットが選ばれていなければ再投入を積んで false を返す(呼び元は今回の開始を見送る)。
        /// 上限まで試しても直らなければ true(Global Preset のまま釣る)。
        /// </summary>
        public static bool EnsureFishingPresetSelected(uint missionId)
        {
            var result = CheckFishingPreset(missionId, out var selected, out var expected);
            if (result != PresetCheck.Mismatch)
                return true;
            if (_presetRetryMission == missionId && _presetRetry >= PresetMaxRetry)
                return true;
            return !TryScheduleFishingPresetRetry(missionId, selected ?? "Global Preset", expected);
        }

        private static bool? ClearFishingPreset()
        {
            if (P.AutoHook.Installed)
            {
                P.AutoHook.DeleteAllAnonymousPresets();
                // AutoHook の PresetList キャッシュは件数でしか更新されないので、削除→投入で件数が戻ると
                // 新しいプリセットが「選択中」として見つからず Global Preset で釣ってしまう。削除の直後にも捨てさせる
                P.AutoHook.InvalidatePresetCache();
            }
            return true;
        }
        private static void ImportPresetsSequentially(uint missionId)
        {
            var presetList = CosmicHelper.SheetMissionDict[missionId].Fish_Presets;

            if (presetList.Count == 0)
                return;

            var preset = presetList[0];
            if (preset.StartsWith("AHFOLDER"))
            {
                IceLogging.Verbose("We found a folder! We're going to import that", "AH Import");
                P.AutoHook.CreateAndSelectAnonymousFolder(preset);
            }
            else
            {
                IceLogging.Verbose("Basic Fishing preset (bless) single import it is", "AH Import");
                P.AutoHook.CreateAndSelectAnonymousPreset(preset);
            }
            // 投入直後にキャッシュを捨てさせ、AutoHook 内部の SelectedPreset が新しいプリセットを指すようにする
            bool invalidated = P.AutoHook.InvalidatePresetCache();
            IceLogging.Debug($"AutoHook のプリセットキャッシュを無効化: {(invalidated ? "成功" : "対象なし/失敗")}", "AH Import");
        }
    }
}
