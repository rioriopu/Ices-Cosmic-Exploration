using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using ICE.Utilities.Cosmic_Helper;
using Callback = ECommons.Automation.Callback;
using Time = (int start, int end);

namespace ICE.Scheduler.Handlers;

internal static unsafe class PlayerHandlers
{
    private static readonly uint stellarSprintID = 4398;
    private static long _wksRewardSeenTick = 0; // WKSReward報酬ポップアップが開いた時刻(強制クローズ用)

    public static unsafe bool IsMoving()
    {
        return AgentMap.Instance()->IsPlayerMoving;
    }
    public static bool PlayerFirstCosmicZone = false;

    internal static unsafe void Tick()
    {
        Tasks.Task_TurninMission.RedAlertDiagTick(); // 一時診断: 緊急ミッションの座標/ワープ/納品ノード追跡

        if (!P.overlayWindow.IsOpen && PlayerHelper.IsInCosmicZone() && C.ShowOverlay)
            P.overlayWindow.IsOpen = true;

        if (C.MoonSprint && PlayerHelper.IsInCosmicZone()
         && !PlayerHelper.HasStatusId(stellarSprintID) && Svc.Condition[ConditionFlag.NormalConditions]
         && IsMoving() && PlayerHelper.UsingSupportedJob())
            UseSprint();

        if ((!PlayerHelper.IsInCosmicZone()) && SchedulerMain.State != IceState.Idle)
        {
            DisablePlugin();
        }

        if (PlayerHelper.HasStatusId(4409) && C.RemoveStellarStatus)
        {
            if (EzThrottler.Throttle("Turning off Stellar Buff"))
                StatusManager.ExecuteStatusOff(4409);
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("WKSReward", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            if (EzThrottler.Throttle("Closing the reward popup"))
            {
                GenericHandlers.FireCallback("WKSReward", true, -1);
            }

            // 7.51でコールバック(-1)では閉じずWKSRewardが開きっぱなしになりループする事例に対するフォールバック。
            // 報酬は完了時に確定済みのサマリー表示なので、一定時間閉じなければアドオンを直接クローズして進行不能を防ぐ。
            if (_wksRewardSeenTick == 0)
                _wksRewardSeenTick = Environment.TickCount64;
            else if (Environment.TickCount64 - _wksRewardSeenTick > 3000 && EzThrottler.Throttle("Force closing reward popup", 1000))
            {
                IceLogging.Warning("WKSReward報酬画面がコールバックで閉じないため、強制クローズします (7.51フォールバック)");
                addon->Close(true);
            }
        }
        else
        {
            _wksRewardSeenTick = 0;
        }

        // === 一時診断: WKSMission窓が開いている間、各ミッションリストの中身とタブをファイルへ出力 ===
        // マスターシップミッションがどのリスト(basic/special/visible)・どのSelectedTabに現れるか実機特定するため。
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("WKSMission", out var wmAddon) && GenericHelpers.IsAddonReady(wmAddon))
        {
            if (EzThrottler.Throttle("MasterDiagFile", 2000))
            {
                try
                {
                    int tab = -99; try { tab = AgentWKSMissionEx.selectedTab(); } catch { }
                    var basic = CosmicHandler.Basic_AvailableMissions();
                    var special = CosmicHandler.Provisional_AvailableMissions();
                    var visible = CosmicHandler.VisibleMissions();
                    var line = $"[{System.DateTime.Now:HH:mm:ss}] SelectedTab={tab}\n" +
                               $"  basic({basic.Count})=[{string.Join(",", basic)}]\n" +
                               $"  special({special.Count})=[{string.Join(",", special)}]\n" +
                               $"  visible({visible.Count})=[{string.Join(",", visible)}]\n";
                    System.IO.File.AppendAllText(@"\\rio-pc\DevPlugins\master_diag.log", line);
                }
                catch { }
            }
        }

        if (C.StartUponEnterMoon)
        {
            if (PlayerHelper.IsInCosmicZone() && !PlayerFirstCosmicZone)
            {
                PlayerFirstCosmicZone = true;
                P.TaskManager.EnqueueDelay(1000);
                P.TaskManager.Enqueue(() => InitiateFirstCosmic(), "Waiting for player to be available");
            }
            if (PlayerFirstCosmicZone && !PlayerHelper.IsInCosmicZone())
                PlayerFirstCosmicZone = false;
        }

    }

    private static bool? InitiateFirstCosmic()
    {
        if (Player.Interactable && Player.Available)
        {
            SchedulerMain.EnablePlugin();
            return true;
        }

        return false;
    }

    internal static void DisablePlugin()
    {
        if (SchedulerMain.State != IceState.Idle)
        {
            P.TaskManager.Abort();
            SchedulerMain.DisablePlugin();
        }
        PlayerFirstCosmicZone = false;
    }

    private static void UseSprint()
    {
        var am = ActionManager.Instance();
        var isSprintReady = am->GetActionStatus(ActionType.GeneralAction, 4) == 0;

        if (isSprintReady) am->UseAction(ActionType.GeneralAction, 4);
    }

    /// <summary>
    ///
    /// </summary>
    /// <returns>Hours[long], Minutes[long]</returns>
    private static (long, long) GetEorzeaTime()
    {
        var eorzeaTime = Framework.Instance()->ClientTime.EorzeaTime;
        long hours = eorzeaTime / 3600 % 24;
        long minutes = eorzeaTime / 60 % 60;
        return (hours, minutes);
    }
}
