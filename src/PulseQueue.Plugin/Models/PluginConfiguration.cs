using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PulseQueue.Plugin.Models;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public const int CurrentVersion = 3;
    public const int DefaultTurboInitialDelayMilliseconds = 180;
    public const int DefaultTurboRepeatIntervalMilliseconds = 80;
    public const int MinimumTurboInitialDelayMilliseconds = 0;
    public const int MaximumTurboInitialDelayMilliseconds = 1_000;
    public const int MinimumTurboRepeatIntervalMilliseconds = 60;
    public const int MaximumTurboRepeatIntervalMilliseconds = 1_000;

    private bool turboEnabled;
    private bool turboMacrosEnabled;

    public int Version { get; set; } = CurrentVersion;
    public bool Enabled { get; set; } = true;
    public bool DryRun { get; set; }
    public bool DetailedLogging { get; set; }
    public bool TurboEnabled
    {
        get => Version <= CurrentVersion && turboEnabled;
        set => turboEnabled = value;
    }

    public bool TurboMacrosEnabled
    {
        get => Version <= CurrentVersion && turboMacrosEnabled;
        set => turboMacrosEnabled = value;
    }

    public int TurboInitialDelayMs { get; set; } = DefaultTurboInitialDelayMilliseconds;
    public int TurboRepeatIntervalMs { get; set; } = DefaultTurboRepeatIntervalMilliseconds;
    public bool TurboOutOfCombat { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface value)
    {
        pluginInterface = value;

        // A configuration written by a newer plugin may contain fields this build
        // cannot preserve. Keep the file untouched and make the optional repeat
        // source inert until a compatible build or an explicit reset is used.
        if (Version > CurrentVersion)
        {
            return;
        }

        var changed = false;
        if (Version <= 1)
        {
            TurboEnabled = false;
            TurboInitialDelayMs = DefaultTurboInitialDelayMilliseconds;
            TurboRepeatIntervalMs = DefaultTurboRepeatIntervalMilliseconds;
            TurboOutOfCombat = false;
            changed = true;
        }

        // Macro Turbo is a separate high-impact permission. Never infer consent
        // from an older schema where the setting did not exist.
        if (Version <= 2)
        {
            TurboMacrosEnabled = false;
            changed = true;
        }

        if (Version != CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        changed |= NormalizeTurboTiming();
        if (changed) Save();
    }

    public void Save()
    {
        // Do not serialize an unknown future schema through this older model:
        // doing so would erase fields that this build did not deserialize.
        if (Version > CurrentVersion) return;

        NormalizeTurboTiming();
        pluginInterface?.SavePluginConfig(this);
    }

    public void ResetToDefaults()
    {
        Version = CurrentVersion;
        Enabled = true;
        DryRun = false;
        DetailedLogging = false;
        TurboEnabled = false;
        TurboMacrosEnabled = false;
        TurboInitialDelayMs = DefaultTurboInitialDelayMilliseconds;
        TurboRepeatIntervalMs = DefaultTurboRepeatIntervalMilliseconds;
        TurboOutOfCombat = false;
    }

    private bool NormalizeTurboTiming()
    {
        var initialDelay = Math.Clamp(
            TurboInitialDelayMs,
            MinimumTurboInitialDelayMilliseconds,
            MaximumTurboInitialDelayMilliseconds);
        var repeatInterval = Math.Clamp(
            TurboRepeatIntervalMs,
            MinimumTurboRepeatIntervalMilliseconds,
            MaximumTurboRepeatIntervalMilliseconds);
        var changed = initialDelay != TurboInitialDelayMs
            || repeatInterval != TurboRepeatIntervalMs;
        TurboInitialDelayMs = initialDelay;
        TurboRepeatIntervalMs = repeatInterval;
        return changed;
    }
}
