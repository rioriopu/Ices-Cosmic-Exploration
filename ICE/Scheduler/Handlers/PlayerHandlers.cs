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
    public static uint lastTerritory = 0;

    // 「最優秀貢献者の証」(ステラーの光る見た目)。Status シートには同名・同アイコン(216255)の ID が 2 つある(4409 と 4415)。
    // 従来は 4409 だけを解除していたため、レリック強化でジョブを切り替えて戻った直後などに 4415 で再付与されると外れなかった
    // (2026-09-30 ユーザー報告)。シートから同アイコン/同名の ID を全て集めて解除する。
    private const uint StellarContributorStatusId = 4409;
    private static HashSet<uint>? _stellarStatusIds;
    private static HashSet<uint> StellarStatusIds
    {
        get
        {
            if (_stellarStatusIds != null) return _stellarStatusIds;
            var ids = new HashSet<uint> { StellarContributorStatusId, 4415 };
            try
            {
                var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>();
                if (sheet != null && sheet.TryGetRow(StellarContributorStatusId, out var baseRow))
                {
                    string baseName = baseRow.Name.ExtractText();
                    foreach (var row in sheet)
                    {
                        if (row.RowId == 0) continue;
                        if (row.Icon == baseRow.Icon || (!string.IsNullOrEmpty(baseName) && row.Name.ExtractText() == baseName))
                            ids.Add(row.RowId);
                    }
                }
            }
            catch (Exception ex)
            {
                IceLogging.Warning($"ステラーステータスの ID 一覧を Status シートから作れませんでした(既定の 4409/4415 を使います): {ex.Message}", "[Stellar Status]");
            }
            IceLogging.Debug($"ステラーステータスとして解除する ID: {string.Join(",", ids.OrderBy(x => x))}", "[Stellar Status]");
            _stellarStatusIds = ids;
            return ids;
        }
    }

    private static void RemoveStellarStatuses()
    {
        if (Player.Object is not Dalamud.Game.ClientState.Objects.Types.IBattleChara chara)
            return;
        foreach (var status in chara.StatusList)
        {
            uint id = status.StatusId;
            if (id == 0 || !StellarStatusIds.Contains(id))
                continue;
            if (!EzThrottler.Throttle($"Turning off Stellar Buff {id}", 1000))
                continue;
            bool ok = StatusManager.ExecuteStatusOff(id);
            IceLogging.Info($"ステラーステータス(ID {id})の解除を要求しました: {(ok ? "受理" : "拒否(後で再試行)")}", "[Stellar Status]");
        }
    }

    internal static unsafe void Tick()
    {
        var playerTerritory = Player.Territory.RowId;
        /*
        if (lastTerritory != playerTerritory)
        {
            lastTerritory = playerTerritory;
            if (PlayerHelper.IsInCosmicZone())
            {
                if (C.ShowOverlay && !P.overlayWindow.IsOpen)
                    P.overlayWindow.IsOpen = true;
            }
        }
        */

        if (C.MoonSprint 
            && PlayerHelper.IsInCosmicZone()
            && !PlayerHelper.HasStatusId(stellarSprintID) 
            && Svc.Condition[ConditionFlag.NormalConditions]
            && IsMoving() 
            && PlayerHelper.UsingSupportedJob())
        {
            UseSprint();
        }

        if ((!PlayerHelper.IsInCosmicZone()) && SchedulerMain.State != IceState.Idle)
        {
            DisablePlugin();
        }

        if (C.RemoveStellarStatus)
            RemoveStellarStatuses();

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("WKSReward", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            if (C.HideRewardWindow)
            {
                if (EzThrottler.Throttle("Closing the reward popup"))
                {
                    GenericHandlers.FireCallback("WKSReward", true, -1);
                }

                // コールバック(-1)では閉じず WKSReward が開いたままになりループする事例へのフォールバック。
                // 報酬は完了時に確定済みのサマリー表示なので、一定時間閉じなければ直接クローズして進行不能を防ぐ。
                if (_wksRewardSeenTick == 0)
                    _wksRewardSeenTick = Environment.TickCount64;
                else if (Environment.TickCount64 - _wksRewardSeenTick > 3000 && EzThrottler.Throttle("Force closing reward popup", 1000))
                {
                    IceLogging.Warning("WKSReward報酬画面がコールバックで閉じないため、強制クローズします");
                    addon->Close(true);
                }
            }
        }

        else
        {
            _wksRewardSeenTick = 0;
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
