using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using autoLeve.Windows;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;

namespace autoLeve;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAddonEventManager AddonEventManager { get; private set; } = null!;

    private const string CommandName = "/alevetest";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("autoLeve");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    internal SemiAutoLeveAssistant SemiAutoAssistant { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        EnsureDefaultNpcRouteConfiguration(Configuration);
        SemiAutoAssistant = new SemiAutoLeveAssistant(
            Configuration,
            ChatGui,
            Log,
            GameGui,
            ClientState,
            GameInteropProvider,
            AddonLifecycle,
            AddonEventManager);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [autoLeve] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    private static void EnsureDefaultNpcRouteConfiguration(Configuration config)
    {
        var changed = false;

        if (!config.NpcAConfigured)
        {
            config.NpcAConfigured = true;
            config.NpcAName = "格里格";
            config.NpcATerritory = 962;
            config.NpcAX = 46.83f;
            config.NpcAY = -15.65f;
            config.NpcAZ = 107.87f;
            changed = true;
        }

        if (changed)
        {
            config.Save();
        }

    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Framework.Update -= OnFrameworkUpdate;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        SemiAutoAssistant.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim();
        if (!string.IsNullOrEmpty(trimmedArgs))
        {
            HandleAssistantCommand(trimmedArgs);
            return;
        }

        var player = ClientState.LocalPlayer;
        if (player == null)
        {
            ChatGui.Print("尚未登入角色。");
            return;
        }

        Vector3 pos = player.Position;
        ChatGui.Print($"座標: X={pos.X:F1}, Y={pos.Y:F1}, Z={pos.Z:F1}");
    }

    private void HandleAssistantCommand(string args)
    {
        switch (args.ToLowerInvariant())
        {
            case "semi on":
                Configuration.SemiAutoLeveEnabled = true;
                Configuration.Save();
                ChatGui.Print("[autoLeve] 半自動模式已開啟。");
                break;
            case "semi off":
                Configuration.SemiAutoLeveEnabled = false;
                Configuration.Save();
                SemiAutoAssistant.Stop("使用者關閉");
                ChatGui.Print("[autoLeve] 半自動模式已關閉。");
                break;
            case "semi start":
                SemiAutoAssistant.Start();
                break;
            case "semi stop":
                SemiAutoAssistant.Stop("使用者停止");
                break;
            case "semi status":
                ChatGui.Print($"[autoLeve] {SemiAutoAssistant.StatusSummary}");
                break;
            case "semi dump":
                SemiAutoAssistant.DumpVisibleMenuEntries();
                break;
            default:
                ChatGui.Print(
                    "[autoLeve] 指令: /alevetest semi on|off|start|stop|status|dump (無參數則顯示座標)"
                );
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        SemiAutoAssistant.Update();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
