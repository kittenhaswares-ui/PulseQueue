using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PulseQueue.Core;
using PulseQueue.Plugin.Models;
using PulseQueue.Plugin.Services;
using PulseQueue.Plugin.UI;

namespace PulseQueue.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/pulsequeue";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly PluginConfiguration configuration;
    private readonly WindowSystem windowSystem = new("PulseQueue");
    private readonly ActionBufferService actionBuffer;
    private readonly SettingsWindow settingsWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IDataManager dataManager,
        IFramework framework,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.chatGui = chatGui;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        configuration.Initialize(pluginInterface);

        actionBuffer = new ActionBufferService(
            pluginInterface,
            clientState,
            objectTable,
            targetManager,
            condition,
            dataManager,
            framework,
            interop,
            log,
            configuration);
        settingsWindow = new SettingsWindow(configuration, actionBuffer, ApplyConfiguration, ResetConfiguration);
        windowSystem.AddWindow(settingsWindow);

        commandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PulseQueue settings. Subcommands: on, off, status, turbo on|off, dry on|off, log on|off, cancel, reset, help.",
        });

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenSettings;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        actionBuffer.Start();
    }

    public void Dispose()
    {
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenSettings;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        commandManager.RemoveHandler(Command);
        actionBuffer.Dispose();
        windowSystem.RemoveAllWindows();
    }

    private void Draw() => windowSystem.Draw();

    private void OpenSettings() => settingsWindow.IsOpen = true;

    private void OnCommand(string _, string arguments)
    {
        try
        {
            HandleCommand(arguments.Trim());
        }
        catch (Exception exception)
        {
            log.Error(exception, "PulseQueue command failed.");
            chatGui.PrintError("[PulseQueue] The command failed. See the Dalamud log for details.");
        }
    }

    private void HandleCommand(string arguments)
    {
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || words[0].Equals("open", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            settingsWindow.Toggle();
            return;
        }

        switch (words[0].ToLowerInvariant())
        {
            case "on":
                configuration.Enabled = true;
                ApplyConfiguration("Enabled from command");
                break;
            case "off":
                configuration.Enabled = false;
                ApplyConfiguration("Disabled from command");
                break;
            case "status":
            case "debug":
                PrintStatus();
                break;
            case "dry" when TryReadToggle(words, out var dryRun):
                configuration.DryRun = dryRun;
                ApplyConfiguration($"Dry run {(dryRun ? "enabled" : "disabled")}");
                break;
            case "turbo" when TryReadToggle(words, out var turbo):
                if (configuration.Version > PluginConfiguration.CurrentVersion)
                {
                    chatGui.PrintError(
                        "[PulseQueue] Native Turbo remains off because this configuration was written by a newer plugin. Update PulseQueue or use Reset only if you intentionally want to replace that newer configuration.");
                    break;
                }

                configuration.TurboEnabled = turbo;
                ApplyConfiguration($"Native Turbo {(turbo ? "enabled" : "disabled")}");
                break;
            case "log" when TryReadToggle(words, out var detailedLogging):
                configuration.DetailedLogging = detailedLogging;
                ApplyConfiguration($"Detailed logging {(detailedLogging ? "enabled" : "disabled")}");
                break;
            case "cancel":
                actionBuffer.Cancel(CancelReason.Explicit, "Cancelled by command");
                chatGui.Print("[PulseQueue] Pending input cleared and Turbo stopped.");
                break;
            case "reset":
                ResetConfiguration();
                chatGui.Print("[PulseQueue] Settings and fault latch reset.");
                break;
            case "help":
                PrintHelp();
                break;
            default:
                PrintHelp(error: true);
                break;
        }
    }

    private void ApplyConfiguration(string reason = "Settings changed")
    {
        actionBuffer.Cancel(configuration.Enabled ? CancelReason.Explicit : CancelReason.Disabled, reason);
        configuration.Save();
    }

    private void ResetConfiguration()
    {
        configuration.ResetToDefaults();
        actionBuffer.ClearFaultForReload();
        configuration.Save();
    }

    private void PrintStatus()
    {
        var value = actionBuffer.Diagnostics;
        var conflicts = value.Conflicts.Count == 0 ? "none" : string.Join(", ", value.Conflicts);
        var integrations = value.Integrations.Count == 0 ? "none" : string.Join(", ", value.Integrations);
        chatGui.Print(
            $"[PulseQueue] {value.Status}; window={value.HoldWindowMilliseconds} ms; "
            + $"RTT={value.EstimatedResponseMilliseconds:0} ms/{value.AcceptedTimingSamples} samples; "
            + $"captured={value.Captured}, dispatched={value.Dispatched}, rejected={value.ReplayRejected}; "
            + $"inputs={value.ObservedHotbarInputs}, replaced={value.ReplacedPendingInputs}; "
            + $"nativeQ={value.NativeQueueAccepted}/{value.NativeQueueBlocked}/{value.OwnedNativeQueueReplacements} owned-replaced; "
            + $"turbo={value.TurboState} {value.TurboPulses}/{value.TurboAccepted}/{value.TurboRejected}, {value.TurboStatus}; "
            + $"integrations={integrations}; conflicts={conflicts}; last={value.LastEvent}");
    }

    private void PrintHelp(bool error = false)
    {
        const string text = "Usage: /pulsequeue [on|off|status|turbo on|off|dry on|off|log on|off|cancel|reset|help]. /pulsequeue opens settings.";
        if (error) chatGui.PrintError($"[PulseQueue] {text}");
        else chatGui.Print($"[PulseQueue] {text}");
    }

    private static bool TryReadToggle(IReadOnlyList<string> words, out bool value)
    {
        value = false;
        if (words.Count != 2) return false;
        if (words[1].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        return words[1].Equals("off", StringComparison.OrdinalIgnoreCase);
    }
}
