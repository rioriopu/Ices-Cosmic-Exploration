using ECommons.Automation.NeoTaskManager;
using ECommons.Configuration;
using ECommons.GameHelpers;
using ICE.ConfigFiles;
using ICE.IPC;
using ICE.Ui;
using ICE.Utilities.Cosmic_Helper;
using Pictomancy;
using System.Collections.Generic;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using ICE.Scheduler.Handlers.PictoStuff;
using ICE.Utilities.GatheringHelper.RouteLoader;

namespace ICE;

public sealed partial class ICE : IDalamudPlugin
{
    public static string Name => "ICE";

    internal static ICE P = null!;
    private Config config;
    public static Config C => P.config;
    public static PctContext PictoService;

    // Missing ECommons PluginService. Update to Svc when ECommons get updated
    [PluginService] public static IUnlockState UnlockState { get; set; } = null!;
    
    public MissionTimer MissionTimer { get; private set; }

    // Window's that I use, base window to the settings... need these to actually show shit 
    internal WindowSystem windowSystem;
    internal MainWindow mainWindow;
    internal OverlayWindow overlayWindow;
    internal DebugWindow debugWindow;
    internal InfoWindow infoWindow;
    internal Window_ExternalDetails externalDetails;

    // Taskmanager from Ecommons
    internal TaskManager TaskManager;

    // Internal IPC's that I use for... well plugins. 
    internal LifestreamIPC Lifestream;
    internal NavmeshIPC Navmesh;
    internal PandoraIPC Pandora;
    internal ArtisanIPC Artisan;
    internal VislandIPC Visland;
    internal AutoHookIPC AutoHook;
    internal IceCosmicExplorationIPC IceIpc;
    internal GlamourerIPC GlamourIpc;

    public ICE(IDalamudPluginInterface pi)
    {
        P = this;
        ECommonsMain.Init(pi, P, Module.DalamudReflector, ECommons.Module.ObjectFunctions);
        new ECommons.Schedulers.TickScheduler(Load);
        PictoService = PctService.Initialize(pi);
    }
    public void Load()
    {
        EzConfig.Migrate<Config>();
        config = EzConfig.Init<Config>();

        // プラグイン内蔵の日本語化(CSV 辞書)。ウィンドウを作る前に読み込む
        GenericHelpers.Safe(() => global::ICE.Localization.Loc.Initialize());

        //IPC's that are used
        Lifestream = new();
        Navmesh = new();
        Pandora = new();
        Artisan = new();
        Visland = new();
        AutoHook = new();
        IceIpc = new();
        GlamourIpc = new(Svc.PluginInterface);

        // all the windows
        windowSystem = new();
        mainWindow = new();
        overlayWindow = new();
        debugWindow = new();
        infoWindow = new();
        externalDetails = new();

        EzCmd.Add("/icecosmic", OnCommand, """
            Open plugin interface
            /ice help - shows all commands
            /ice clear - removes all missions
            /ice stop - stops ICE (also aborts a leveling gear purchase)
            /ice start - Starts ICE
            /ice add | remove | toggle | only 
            /ice flag [id] - Opens the map and marks where the area of gathering is.
            """);
        EzCmd.Add("/ice", OnCommand);
        EzCmd.Add("/IceCosmic", OnCommand);
        Init();
        Svc.Framework.Update += Tick;
        Svc.Chat.ChatMessage += OnChatMessage;

        // ガンブルの輪(WKSLottery)が開いたら、その時点の景品を自動で設定へ取り込む(カテゴリ推定付き)。
        // 景品プールはシートに無くバイナリ内のため、輪を開いた時にしか取得できない。
        Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "WKSLottery", OnWheelOpened);
        Svc.PluginInterface.UiBuilder.Draw += OnDraw;

        TaskManager = new(new(showDebug: false, timeLimitMS: 10 * 60 * 3000));
        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += () =>
        {
            mainWindow.IsOpen = true;
        };
        Svc.PluginInterface.UiBuilder.OpenConfigUi += () =>
        {
            mainWindow.IsOpen = true;
            C.SelectedTab = WindowSelection.MiscSettings;
        };

        Svc.ClientState.TerritoryChanged += OnTerritoryChange;

        // timer stuff
        MissionTimer = new MissionTimer();

        DictionaryCreation();
        Task_Gamba.EnsureGambaWeightsInitialized();
        CosmicHelper.UpdateCriticalWeather();
        TestLoadRoutes();
        CosmicHelper.Task_UpdateRelicMissionInfo();
        GatheringRouteLoader.LoadAllRoutes();

        MigrateConfigSettings();
        _ = Sounds.SoundPlayer.InitializeAsync();

        UpdateMissingGathering();
    }

    // ガンブルの輪が開いたら景品を自動取込する。PostSetup 直後はデータが未充填のことがあるため、少し遅らせて走査する。
    private void OnWheelOpened(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
    {
        GenericHelpers.Safe(() => Svc.Framework.RunOnTick(() =>
        {
            try
            {
                var (open, added) = Scheduler.Tasks.Task_Gamba.ScanOpenWheel();
                if (added > 0)
                    IceLogging.Info($"[Gamba] 輪を開いたので新景品 {added} 件を自動登録しました");
            }
            catch { }
        }, TimeSpan.FromMilliseconds(500)));
    }

    // 「魚たちに警戒されてしまったようだ…少し場所を変えたほうがいいだろう」のログを検知し、釣り場の移動を要求する。
    // 同じ釣り場で粘り続けて釣果が落ちるのを避けるため、次サイクルで別スポットへ移動させる。
    private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
    {
        try
        {
            var text = msg.Message?.TextValue;
            if (!string.IsNullOrEmpty(text) && text.Contains("警戒") && text.Contains("場所を変え"))
                Scheduler.Tasks.Task_Fishing.WaryMoveRequested = true;
        }
        catch { }
    }
    private static void Init()
    {
        ExcelHelper.Init();
        ConsumableInfo.Init();
    }

    private void Tick(object _)
    {
        if (Player.Available && PlayerHelper.IsInCosmicZone())
        {
            if (EzThrottler.Throttle("Update Character Stats"))
            {
                CosmicHelper.Task_UpdateRelicMissionInfo();
            }
            PlayerHandlers.Tick();
            if (SchedulerMain.State != IceState.Idle)
                SchedulerMain.Tick();
            WeatherForecastHandler.Tick();

            if (C.FakeIncreaseFisher)
            {
                GlamourIpc.SetClownHead();
            }
            else
            {
                GlamourIpc.ResetClownHead();
            }
        }
        else if (!Player.Available)
        {
            if (SchedulerMain.State != IceState.Idle)
                PlayerHandlers.DisablePlugin();
            if (PlayerHandlers.PlayerFirstCosmicZone)
                PlayerHandlers.PlayerFirstCosmicZone = false;
        }

        if (PlayerHelper.IsInCosmicZone())
        {
            GenericManager.Tick();
            TextAdvancedManager.Tick();
            YesAlreadyManager.Tick();
        }
        else
        {
            if (SchedulerMain.State != IceState.Idle)
                SchedulerMain.DisablePlugin();
        }

        if (SchedulerMain.State == IceState.Idle && !GenericManager.Pandora_WasRestored)
        {
            if (EzThrottler.Throttle("Turning on pandora features"))
                GenericManager.RestorePandoraStates();
        }
    }

    private void OnTerritoryChange(uint TerritoryId)
    {
        bool isInCosmic = PlayerHelper.IsInCosmicZone();
        if (isInCosmic && !overlayWindow.IsOpen && C.ShowOverlay)
            P.overlayWindow.IsOpen = true;
        else if (!isInCosmic && overlayWindow.IsOpen)
            P.overlayWindow.IsOpen = false;
    }

    private void OnDraw()
    {
        if (PlayerHelper.IsInCosmicZone() && Player.Available)
        {
            PictoManager.DrawPicto();
        }
    }

    public void Dispose()
    {
        GenericHelpers.Safe(() => Svc.AddonLifecycle.UnregisterListener(OnWheelOpened));
        GenericHelpers.Safe(() => Svc.Framework.Update -= Tick);
        GenericHelpers.Safe(() => Svc.PluginInterface.UiBuilder.Draw -= OnDraw);
        GenericHelpers.Safe(() => Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
        GenericHelpers.Safe(() => Svc.ClientState.TerritoryChanged -= OnTerritoryChange);
        GenericHelpers.Safe(TextAdvancedManager.UnlockTA);
        GenericHelpers.Safe(YesAlreadyManager.Unlock);
        GenericHelpers.Safe(PictoService.Dispose);
        ECommonsMain.Dispose();
        PictoService.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var subcommands = args.Split(' ');

        if (subcommands.Length == 0 || args == "")
        {
            mainWindow.IsOpen = !mainWindow.IsOpen;
            return;
        }

        var firstArg = subcommands[0];

        if (firstArg.ToLower() == "d" || firstArg.ToLower() == "debug")
        {
            debugWindow.IsOpen = true;
            return;
        }
        else if (firstArg.ToLower() == "i")
        {
            infoWindow.IsOpen = true;
            return;
        }
        else if (firstArg.ToLower() == "s" || firstArg.ToLower() == "settings")
        {
            mainWindow.IsOpen = true;
            C.SelectedTab = WindowSelection.MiscSettings;
            return;
        }
        else if (firstArg.ToLower() == "clear")
        {
            foreach (var mission in C.MissionConfig)
            {
                mission.Value.Enabled = false;
            }
            C.Save();
        }
        else if (firstArg.ToLower() == "stop")
        {
            SchedulerMain.DisablePlugin();
        }
        else if (firstArg.ToLower() == "start")
        {
            SchedulerMain.EnablePlugin();
        }
        else if (firstArg.ToLower() == "add")
        {
            uint[] ids = [.. subcommands.Skip(1).Select(uint.Parse)];
            var idSet = new HashSet<uint>(ids);
            if (ids.Length == 0) return;

            foreach (var id in idSet)
            {
                if (C.MissionConfig.TryGetValue(id, out var mission))
                {
                    mission.Enabled = true;
                }
            }
            C.Save();
        }
        else if (firstArg.ToLower() == "remove")
        {
            uint[] ids = [.. subcommands.Skip(1).Select(uint.Parse)];
            var idSet = new HashSet<uint>(ids);
            if (ids.Length == 0) return;

            foreach (var id in idSet)
            {
                if (C.MissionConfig.TryGetValue(id, out var mission))
                {
                    mission.Enabled = false;
                }
            }
            C.Save();
        }
        else if (firstArg.ToLower() == "toggle")
        {
            uint[] ids = [.. subcommands.Skip(1).Select(uint.Parse)];
            var idSet = new HashSet<uint>(ids);
            if (ids.Length == 0) return;

            foreach (var id in idSet)
            {
                if (C.MissionConfig.TryGetValue(id, out var mission))
                {
                    mission.Enabled = !mission.Enabled;
                }
            }
            C.Save();
        }
        else if (firstArg.ToLower() == "only")
        {
            uint[] ids = [.. subcommands.Skip(1).Select(uint.Parse)];
            var idSet = new HashSet<uint>(ids);
            if (ids.Length == 0) return;

            foreach (var mission in C.MissionConfig.Where(x => x.Value.Enabled))
            {
                mission.Value.Enabled = false;
            }
            foreach (var id in idSet)
            {
                if (C.MissionConfig.TryGetValue(id, out var mission))
                {
                    mission.Enabled = true;
                }
            }
        }
        else if (firstArg.ToLower() == "flag")
        {
            if (subcommands.Length != 2) return;
            if (!PlayerHelper.IsInCosmicZone()) return;

            int missionId = int.Parse(subcommands[1]);
            var info = CosmicHelper.SheetMissionDict.FirstOrDefault(mission => mission.Key == missionId);
            if (info.Value == default) return;
            if (info.Value.MarkerId == 0) return;

            Utils.SetGatheringRing(info.Value.TerritoryId, (int)info.Value.MapPosition.X, (int)info.Value.MapPosition.Y, info.Value.Radius, info.Value.Name);
        }
        else if (firstArg.ToLower() == "help")
        {
            string helpMessage = $"- - ICE Commands Help - - \n" +
                                 $"/ice help - show all available commands\n" +
                                 $"/ice -> opens the main settings\n" +
                                 $"/ice s -> opens the settings menu\n" +
                                 $" - - - Mission specific - - - \n" +
                                 $"/ice stop - Stops ICE\n" +
                                 $"/ice start - starts ICE \n" +
                                 $"The rest of the commands work by doing a single id/multiple in a row \n" +
                                 $"EX. /ice add 10 155 185\n" +
                                 $"/ice add (ids) - enables select missions\n" +
                                 $"/ice remove (ids) - removes/disables select missions\n" +
                                 $"/ice toggle (ids) - toggles select mission ids" +
                                 $"/ice only (ids) - makes only select missions enabled" +
                                 $"/ice flag (id) - opens the map and flags the mission (if it has one).\n";
            Svc.Chat.Print(helpMessage);
        }
        else if (firstArg.ToLower() == "overlay")
        {
            overlayWindow.IsOpen = false;
            overlayWindow.IsOpen = true;
        }
    }

    public void TestLoadRoutes()
    {

    }
}
