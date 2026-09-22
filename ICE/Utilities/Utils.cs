using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices.Legacy;
using ECommons.GameHelpers;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ICE.Utilities.Cosmic_Helper;
using Pictomancy;
using SharpDX.Direct3D11;
using System;

namespace ICE.Utilities;

/// <summary>
/// Misc unused (yet) or weird functions i didn't know where to put - Chika
/// </summary>
public static unsafe class Utils
{
    public static uint DarkMatter_8Id = 33916;

    public static bool HasPlugin(string name) => DalamudReflector.TryGetDalamudPlugin(name, out _, false, true);
    public static TaskManagerConfiguration TaskConfig => new(timeLimitMS: 10 * 60 * 3000, abortOnTimeout: false);

    public static unsafe void SetFlagForNPC(uint territoryId, float x, float y)
    {
        var map = ExcelHelper.TerritorySheet.GetRow(territoryId).Map.Value;

        var agent = AgentMap.Instance();

        Vector2 pos = MapToWorld(new Vector2(x, y), map.SizeFactor, map.OffsetX, map.OffsetY);

        agent->FlagMarkerCount = 0;
        agent->SetFlagMapMarker(territoryId, map.RowId, pos.X, pos.Y);
        agent->OpenMapByMapId(map.RowId, territoryId);
    }

    public static void SetRawFlagforNpc(uint territoryId, float x, float y)
    {
        var map = ExcelHelper.TerritorySheet.GetRow(territoryId).Map.Value;

        var agent = AgentMap.Instance();

        agent->FlagMarkerCount = 0;
        agent->SetFlagMapMarker(territoryId, map.RowId, x, y);
        agent->OpenMapByMapId(map.RowId, territoryId);
    }

    /// <summary>
    /// ミッションのフラグ座標(MapPosition)をワールド座標へ変換し、歩ける地面へ投影する。
    /// MapPosition は「マップマーカー生値 - 1024」で保持されており、コスミックマップ(SizeFactor=100)では
    /// ワールド座標(X,Z)とほぼ1:1で対応する。MapToWorld(1〜42のマップ座標前提)を通すと座標系が違い巨大値になる。
    /// Yはプレイヤーの現在高度を基準にする(0にすると下層のメッシュへ誤って吸着し、的外れな場所へ向かう)。
    /// </summary>
    public static Vector3? FlagToWorld(uint territoryId, Vector2 flag)
    {
        try
        {
            var map = ExcelHelper.TerritorySheet.GetRow(territoryId).Map.Value;
            float scalar = map.SizeFactor / 100f;
            if (scalar <= 0f) scalar = 1f;
            float baseY = Player.Available ? Player.Position.Y : 0f;
            Vector3 world = new(flag.X / scalar, baseY, flag.Y / scalar);

            if (P.Navmesh.Installed)
            {
                var floor = P.Navmesh.PointOnFloor(world, false, 50f);
                if (floor.HasValue)
                    return floor.Value;

                // フラグがメッシュ外の場合は、XZを広め・Yを狭めに取って最寄りのメッシュへ寄せる
                // (Yを広げるとプレイヤーと違う層のメッシュを拾ってしまう)。
                var nearest = P.Navmesh.NearestPoint(world, 200f, 60f);
                if (nearest.HasValue)
                    return nearest.Value;
            }
            return world;
        }
        catch
        {
            return null;
        }
    }

    public static float MapToWorld(float value, uint scale, int offset) => -offset * (scale / 100.0f) + 50.0f * (value - 1) * (scale / 100.0f);

    public static Vector2 MapToWorld(Vector2 coordinates, ushort sizeFactor, short offsetX, short offsetY)
    {
        var scalar = sizeFactor / 100.0f;

        var xWorldCoord = MapToWorld(coordinates.X, sizeFactor, offsetX);
        var yWorldCoord = MapToWorld(coordinates.Y, sizeFactor, offsetY);

        var objectPosition = new Vector2(xWorldCoord, yWorldCoord);
        var center = new Vector2(1024.0f, 1024.0f);

        return objectPosition / scalar - center / scalar;
    }

    public static bool? TargetgameObjectTask(IGameObject? gameObject)
    {
        var x = gameObject;
        if (Svc.Targets.Target != null && Svc.Targets.Target.BaseId == x.BaseId)
            return true;

        if (!GenericHelpers.IsOccupied())
        {
            if (x != null)
            {
                if (EzThrottler.Throttle($"Throttle Targeting {x.BaseId}"))
                {
                    Svc.Targets.SetTarget(x);
                    IceLogging.Info($"Setting the target to {x.BaseId}");
                }
            }
        }
        return false;
    }
    internal static bool TryGetObjectByDataId(ulong dataId, out IGameObject? gameObject) => (gameObject = Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.BaseId == dataId)) != null;
    public static IGameObject? TryGetObjectNearestEventObject()
    {
        return Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj);
    }
    public static IGameObject? TryGetObjectCollectionPoint()
    {
        return Svc.Objects.OrderBy(Player.DistanceTo).FirstOrDefault(x => x.BaseId == 2014616 || x.BaseId == 2014618);
    }
    public static void TargetgameObject(IGameObject? gameObject)
    {
        var x = gameObject;
        var currentTarget = Svc.Targets.Target;
        if (currentTarget != null && currentTarget.BaseId == x.BaseId)
            return;

        if (!GenericHelpers.IsOccupied())
        {
            if (x != null)
            {
                if (EzThrottler.Throttle($"Throttle targeting: {x.BaseId}"))
                {
                    IceLogging.Info($"Attempting to set the target to: {x.BaseId} | {x.Name}", "[Target Game Object]");
                    Svc.Targets.SetTarget(x);
                }
            }
        }
    }
    public static unsafe void InteractWithObject(IGameObject? gameObject)
    {
        try
        {
            if (gameObject == null || !gameObject.IsTargetable)
                return;
            var gameObjectPointer = (GameObject*)gameObject.Address;
            TargetSystem.Instance()->InteractWithObject(gameObjectPointer, false);
        }
        catch (Exception ex)
        {
            IceLogging.Error($"InteractWithObject: Exception: {ex}");
        }
    }
    public static unsafe void SetGatheringRing(uint territoryId, int x, int y, int radius, string? tooltip = "Node Location", uint iconId = 60561)
    {
        var map = ExcelHelper.TerritorySheet.GetRow(territoryId).Map.Value;
        var agent = AgentMap.Instance();

        Vector2 pos = MapToWorld(new Vector2(x, y), map.SizeFactor, map.OffsetX, map.OffsetY);
        IceLogging.Debug($"Current map: {map.RowId} {territoryId} | {map.PlaceName.Value.Name} | {pos.X} {pos.Y} | {x} {y} | {radius} | {tooltip}");

        agent->FlagMarkerCount = 0;
        // agent->IsFlagMarkerSet = false;
        agent->SetFlagMapMarker(territoryId, map.RowId, x, y, iconId);
        agent->TempMapMarkerCount = 0;
        agent->AddGatheringTempMarker(x, y, radius, tooltip: tooltip);
        agent->OpenMap(map.RowId, territoryId, tooltip, MapType.GatheringLog);
    }
    public static unsafe void MountAction()
    {
        IceLogging.Verbose("We were told to use a mount action");
        bool useMount = Char_Info.MountId != 0 && PlayerState.Instance()->IsMountUnlocked(Char_Info.MountId);

        if (!Player.IsCasting && !Player.Mounting)
        {
            if (useMount)
            {
                ActionManager.Instance()->UseAction(ActionType.Mount, Char_Info.MountId);
                IceLogging.Info($"Attempting to mount: {Char_Info.MountName}");
            }
            else
            {
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
                IceLogging.Info($"Resorting to using the mount roulette");
            }
        }
    }
    public static unsafe void Dismount()
    {
        if (Player.Mounted)
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 9);
        }
    }

    public static uint ToUintABGR(Vector4 col)
    {
        byte a = (byte)(col.W * 255);
        byte b = (byte)(col.Z * 255);
        byte g = (byte)(col.Y * 255);
        byte r = (byte)(col.X * 255);
        return (uint)((a << 24) | (b << 16) | (g << 8) | r);
    }

    public static Vector4 FromUintABGR(uint color)
    {
        float a = ((color >> 24) & 0xFF) / 255f;
        float b = ((color >> 16) & 0xFF) / 255f;
        float g = ((color >> 8) & 0xFF) / 255f;
        float r = (color & 0xFF) / 255f;
        return new Vector4(r, g, b, a);
    }

    public static void VnavBuildInfo()
    {
        if (EzThrottler.Throttle("Vnavmesh throttle message", 1000))
            IceLogging.Debug($"Navmesh isn't ready. % built is at: {P.Navmesh.BuildProgress}");
    }
}
