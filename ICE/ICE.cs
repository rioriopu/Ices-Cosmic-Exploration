using ECommons.Automation.NeoTaskManager;
using ECommons.Configuration;
using ECommons.GameHelpers;
using ICE.ConfigFiles;
using ICE.IPC;
using ICE.Ui;
using ICE.Utilities.Cosmic_Helper;
using ICE.Utilities.GatheringHelper;
using Pictomancy;
using System.Collections.Generic;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using ICE.Scheduler.Handlers.PictoStuff;

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

        //IPC's that are used
        Lifestream = new();
        Navmesh = new();
        Pandora = new();
        Artisan = new();
        Visland = new();
        AutoHook = new();
        IceIpc = new();

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
            /ice stop - stops ICE
            /ice start - Starts ICE
            /ice add | remove | toggle | only 
            /ice flag [id] - Opens the map and marks where the area of gathering is.
            """);
        EzCmd.Add("/ice", OnCommand);
        EzCmd.Add("/IceCosmic", OnCommand);
        Init();
        Svc.Framework.Update += Tick;
        Svc.Chat.ChatMessage += OnChatMessage;
        Svc.PluginInterface.UiBuilder.Draw += OnDraw;

        // ガンバの輪(WKSLottery)が開いたら、その時点の景品を自動で設定へ取り込む(カテゴリ推定付き)。
        // 景品プールはゲームデータシートに無くバイナリ内のため、輪を開いた時にしか取得できない。これでボタンを押さなくても
        // 輪を開くだけで新景品(7.51のAuxesia景品等)が設定に反映される。
        Svc.AddonLifecycle.RegisterListener(Dalamud.Game.Addon.Lifecycle.AddonEvent.PostSetup, "WKSLottery", OnWheelOpened);

        TaskManager = new(new(showDebug: false, timeLimitMS: 10 * 60 * 3000));
        Svc.PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += () =>
        {
            mainWindow.IsOpen = true;
        };
        Svc.PluginInterface.UiBuilder.OpenConfigUi += () =>
        {
            mainWindow.IsOpen = true;
            C.MainUi_SelectedWindow = "modeSelect_MissionSetup";
        };

        // timer stuff
        MissionTimer = new MissionTimer();

        DictionaryCreation();
        Task_Gamba.EnsureGambaWeightsInitialized();
        CosmicHelper.UpdateCriticalWeather();
        TestLoadRoutes();
        CosmicHelper.Task_UpdateRelicMissionInfo();

        MigrateConfigSettings();
        _ = Sounds.SoundPlayer.InitializeAsync();
    }

    private static void Init()
    {
        ExcelHelper.Init();
        ConsumableInfo.Init();
        
    }

    // 「魚たちに警戒されてしまったようだ…少し場所を変えたほうがいいだろう」のログを検知し、釣り場移動を要求する。
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

    private void Tick(object _)
    {
        if (PlayerHelper.IsInCosmicZone())
        {
            if (Player.Available)
            {
                if (EzThrottler.Throttle("Update Character Stats"))
                {
                    CosmicHelper.Task_UpdateRelicMissionInfo();
                }


                PlayerHandlers.Tick();
                if (SchedulerMain.State != IceState.Idle)
                    SchedulerMain.Tick();
                WeatherForecastHandler.Tick();
            }
            else
            {
                if (SchedulerMain.State != IceState.Idle)
                    PlayerHandlers.DisablePlugin();
                if (PlayerHandlers.PlayerFirstCosmicZone)
                    PlayerHandlers.PlayerFirstCosmicZone = false;
            }
            GenericManager.Tick();
            TextAdvancedManager.Tick();
            YesAlreadyManager.Tick();
        }
        else
        {
            if (SchedulerMain.State != IceState.Idle)
                SchedulerMain.DisablePlugin();
        }
    }

    private void OnDraw()
    {
        if (PlayerHelper.IsInCosmicZone())
        {
            PictoManager.DrawPicto();
        }
    }

    // ガンバの輪(WKSLottery)が開いたら景品を自動取込。PostSetupは早くてデータ未充填のことがあるため、少し遅延して走査する。
    private void OnWheelOpened(Dalamud.Game.Addon.Lifecycle.AddonEvent ev, Dalamud.Game.Addon.Lifecycle.AddonArgTypes.AddonArgs args)
    {
        GenericHelpers.Safe(() => Svc.Framework.RunOnTick(() =>
        {
            try
            {
                var (open, added) = Scheduler.Tasks.Task_Gamba.ScanOpenWheel();
                if (added > 0)
                    IceLogging.Info($"[Gamba] 輪を開いたので新景品 {added}件 を自動登録しました");
            }
            catch { }
        }, TimeSpan.FromMilliseconds(500)));
    }

    public void Dispose()
    {
        GenericHelpers.Safe(() => Svc.AddonLifecycle.UnregisterListener(OnWheelOpened));
        GenericHelpers.Safe(() => Svc.Chat.ChatMessage -= OnChatMessage);
        GenericHelpers.Safe(() => Svc.Framework.Update -= Tick);
        GenericHelpers.Safe(() => Svc.PluginInterface.UiBuilder.Draw -= OnDraw);
        GenericHelpers.Safe(() => Svc.PluginInterface.UiBuilder.Draw -= windowSystem.Draw);
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
            C.MainUi_SelectedWindow = "modeSelect_MissionSetup";
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
        else if (firstArg.ToLower() == "capture" || firstArg.ToLower() == "cap")
        {
            // 現在地周辺の採取ノードを静的yamlルートとして保存(動的ルート不安定対策)。
            // 採取ミッションを受注し採取エリアに立った状態で実行する。
            var result = GatheringRouteLoader.CaptureCurrentRoute();
            Svc.Chat.Print($"[ICE] 採取ルートキャプチャ: {result}");
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
                                 $"/ice flag (id) - opens the map and flags the mission (if it has one).\n" +
                                 $"/ice capture (cap) - 現在の採取ミッション中に、周辺の採取ノードを静的yamlルートとして保存(Auxesia等の動的ルート不安定対策)\n";
            Svc.Chat.Print(helpMessage);
        }
    }

    public void TestLoadRoutes()
    {
        try
        {
            // Clear cache first to force reload
            GatheringRouteLoader.ClearCache();

            var routes = GatheringRouteLoader.LoadAllRoutes();

            IceLogging.Info($"Successfully loaded {routes.Count} zones with {routes.Sum(x => x.Value.Count)} total routes");

            // Test getting a specific route
            var testRoute = GatheringRouteLoader.GetRoute(1237, new Vector2(-690f, -752f));
            if (testRoute != null)
            {
                IceLogging.Info($"Test route loaded successfully with {testRoute.Count} nodes");
            }
        }
        catch (Exception ex)
        {
            IceLogging.Error($"Failed to load routes: {ex.Message}");
            IceLogging.Error(ex.StackTrace ?? "No stack trace");
        }
    }
}
