using Dalamud.Plugin;
using PulseQueue.Plugin.Models;

internal static class PluginConfigurationTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("Turbo configuration defaults are opt-in", DefaultsAreOptIn);
        yield return ("schema 1 migrates to safe Turbo defaults", SchemaOneMigratesSafely);
        yield return ("Turbo timing is normalized before persistence", TimingNormalizesBeforePersistence);
        yield return ("future configuration fails closed without rewrite", FutureSchemaFailsClosedWithoutRewrite);
        yield return ("explicit reset replaces a future configuration", ResetReplacesFutureSchema);
    }

    private static void DefaultsAreOptIn()
    {
        var configuration = new PluginConfiguration();
        Equal(PluginConfiguration.CurrentVersion, configuration.Version);
        False(configuration.TurboEnabled);
        Equal(PluginConfiguration.DefaultTurboInitialDelayMilliseconds, configuration.TurboInitialDelayMs);
        Equal(PluginConfiguration.DefaultTurboRepeatIntervalMilliseconds, configuration.TurboRepeatIntervalMs);
        False(configuration.TurboOutOfCombat);
    }

    private static void SchemaOneMigratesSafely()
    {
        var persistence = new FakePluginInterface();
        var configuration = new PluginConfiguration
        {
            Version = 1,
            TurboEnabled = true,
            TurboInitialDelayMs = 999,
            TurboRepeatIntervalMs = 999,
            TurboOutOfCombat = true,
        };

        configuration.Initialize(persistence);

        Equal(PluginConfiguration.CurrentVersion, configuration.Version);
        False(configuration.TurboEnabled);
        Equal(PluginConfiguration.DefaultTurboInitialDelayMilliseconds, configuration.TurboInitialDelayMs);
        Equal(PluginConfiguration.DefaultTurboRepeatIntervalMilliseconds, configuration.TurboRepeatIntervalMs);
        False(configuration.TurboOutOfCombat);
        Equal(1, persistence.SaveCount);
        Same(configuration, persistence.LastSaved);
    }

    private static void TimingNormalizesBeforePersistence()
    {
        var persistence = new FakePluginInterface();
        var configuration = new PluginConfiguration
        {
            TurboInitialDelayMs = int.MinValue,
            TurboRepeatIntervalMs = int.MaxValue,
        };

        configuration.Initialize(persistence);
        Equal(PluginConfiguration.MinimumTurboInitialDelayMilliseconds, configuration.TurboInitialDelayMs);
        Equal(PluginConfiguration.MaximumTurboRepeatIntervalMilliseconds, configuration.TurboRepeatIntervalMs);
        Equal(1, persistence.SaveCount);

        configuration.TurboInitialDelayMs = int.MaxValue;
        configuration.TurboRepeatIntervalMs = int.MinValue;
        configuration.Save();
        Equal(PluginConfiguration.MaximumTurboInitialDelayMilliseconds, configuration.TurboInitialDelayMs);
        Equal(PluginConfiguration.MinimumTurboRepeatIntervalMilliseconds, configuration.TurboRepeatIntervalMs);
        Equal(2, persistence.SaveCount);
    }

    private static void FutureSchemaFailsClosedWithoutRewrite()
    {
        var persistence = new FakePluginInterface();
        var configuration = new PluginConfiguration
        {
            Version = PluginConfiguration.CurrentVersion + 1,
            TurboEnabled = true,
            TurboInitialDelayMs = -50,
            TurboRepeatIntervalMs = 1,
            TurboOutOfCombat = true,
        };

        configuration.Initialize(persistence);
        False(configuration.TurboEnabled);
        Equal(-50, configuration.TurboInitialDelayMs);
        Equal(1, configuration.TurboRepeatIntervalMs);
        True(configuration.TurboOutOfCombat);
        Equal(0, persistence.SaveCount);

        configuration.Save();
        Equal(0, persistence.SaveCount);
        Equal(-50, configuration.TurboInitialDelayMs);
        Equal(1, configuration.TurboRepeatIntervalMs);
    }

    private static void ResetReplacesFutureSchema()
    {
        var configuration = new PluginConfiguration
        {
            Version = PluginConfiguration.CurrentVersion + 5,
            Enabled = false,
            DryRun = true,
            DetailedLogging = true,
            TurboEnabled = true,
            TurboInitialDelayMs = 1_000,
            TurboRepeatIntervalMs = 1_000,
            TurboOutOfCombat = true,
        };

        configuration.ResetToDefaults();

        Equal(PluginConfiguration.CurrentVersion, configuration.Version);
        True(configuration.Enabled);
        False(configuration.DryRun);
        False(configuration.DetailedLogging);
        False(configuration.TurboEnabled);
        Equal(PluginConfiguration.DefaultTurboInitialDelayMilliseconds, configuration.TurboInitialDelayMs);
        Equal(PluginConfiguration.DefaultTurboRepeatIntervalMilliseconds, configuration.TurboRepeatIntervalMs);
        False(configuration.TurboOutOfCombat);
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void Same(object expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("Expected the same saved configuration instance.");
        }
    }

    private sealed class FakePluginInterface : IDalamudPluginInterface
    {
        public int SaveCount { get; private set; }

        public object? LastSaved { get; private set; }

        public void SavePluginConfig(object configuration)
        {
            SaveCount++;
            LastSaved = configuration;
        }
    }
}
