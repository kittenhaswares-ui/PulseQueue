using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin;

namespace PulseQueue.Plugin.Services;

internal sealed record PluginCompatibilityAssessment(
    ImmutableArray<string> Conflicts,
    ImmutableArray<string> Integrations,
    ImmutableHashSet<uint> ExcludedActionIds,
    string Signature)
{
    public bool IsCompatible => Conflicts.IsEmpty;
}

/// <summary>
/// Produces a fail-closed compatibility snapshot for plugins that can alter action timing,
/// action selection, repeated input, or targeting. Foreign plugin objects are never strongly
/// retained; the per-input ReAction guard uses only a weak configuration reference.
/// </summary>
internal sealed class PluginCompatibilityService
{
    private const string MOActionRetargetedActionsIpc = "MOAction.RetargetedActions";
    private const string AssessmentFormat = "pulsequeue-compat-v1";

    private static readonly Version SupportedNoClippyVersion = new(0, 5, 0, 24);
    private static readonly Version SupportedReActionVersion = new(1, 3, 5, 1);
    private static readonly Version SupportedMOActionVersion = new(4, 10, 1, 0);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly object liveGuardGate = new();
    private WeakReference<object>? liveReActionConfiguration;
    private ReActionConfigurationSnapshot? auditedReActionConfiguration;
    private int auditedMOActionLoaded;

    public PluginCompatibilityService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
    }

    public PluginCompatibilityAssessment Assess()
    {
        var conflicts = new List<string>();
        var integrations = new List<string>();
        var excludedActionIds = new HashSet<uint>();

        IExposedPlugin[] loadedPlugins;
        try
        {
            loadedPlugins = pluginInterface.InstalledPlugins
                .Where(plugin => plugin.IsLoaded)
                .ToArray();
            if (!loadedPlugins.Any(plugin =>
                    string.Equals(plugin.InternalName, "ReAction", StringComparison.OrdinalIgnoreCase)))
            {
                ClearLiveReActionGuard();
            }

            if (!loadedPlugins.Any(plugin =>
                    string.Equals(plugin.InternalName, "MOAction", StringComparison.OrdinalIgnoreCase)))
            {
                Volatile.Write(ref auditedMOActionLoaded, 0);
            }
        }
        catch
        {
            conflicts.Add("The active plugin list could not be read; reload PulseQueue before enabling buffering.");
            return CreateAssessment(conflicts, integrations, excludedActionIds);
        }

        try
        {
            AssessSingletonPlugin(
                loadedPlugins,
                "NoClippy",
                conflicts,
                plugin => AssessNoClippy(plugin, conflicts, integrations));
            AssessHardConflict(loadedPlugins, "NoClippyUnchained", conflicts,
                "NoClippyUnchained is incompatible; disable it and use NoClippy 0.5.0.24 instead.");
            AssessSingletonPlugin(
                loadedPlugins,
                "ReAction",
                conflicts,
                plugin => AssessReAction(plugin, conflicts, integrations));
            AssessHardConflict(loadedPlugins, "ReActionEx", conflicts,
                "ReActionEx is incompatible; disable it and use the guarded ReAction 1.3.5.1 profile shown in PulseQueue settings.");
            AssessSingletonPlugin(
                loadedPlugins,
                "MOAction",
                conflicts,
                plugin => AssessMOAction(plugin, conflicts, integrations, excludedActionIds));
        }
        catch
        {
            conflicts.Add("Plugin compatibility data could not be verified; reload PulseQueue before enabling buffering.");
        }

        return CreateAssessment(conflicts, integrations, excludedActionIds);
    }

    /// <summary>
    /// Cheap per-input verification of the already-audited ReAction safety fields.
    /// The weak reference never keeps the foreign plugin alive across a reload.
    /// </summary>
    public bool IsLiveReActionProfileCurrent()
    {
        WeakReference<object>? weak;
        ReActionConfigurationSnapshot? expected;
        lock (liveGuardGate)
        {
            weak = liveReActionConfiguration;
            expected = auditedReActionConfiguration;
        }

        if (expected is null) return true;
        if (weak is null || !weak.TryGetTarget(out var configuration)) return false;
        return TryReadReActionConfigurationObject(configuration, out var current)
            && current == expected.Value;
    }

    /// <summary>
    /// Re-reads MOAction ownership only for an action that is about to be armed
    /// or dispatched. Adding a retarget stack therefore fails closed without
    /// invoking IPC on every ordinary hotbar press.
    /// </summary>
    public bool IsLiveMOActionUnowned(uint requestedActionId, uint resolvedActionId)
    {
        if (Volatile.Read(ref auditedMOActionLoaded) == 0) return true;

        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<uint[]>(MOActionRetargetedActionsIpc);
            if (!subscriber.HasFunction) return false;
            var actionIds = subscriber.InvokeFunc();
            return actionIds is not null
                && !actionIds.Contains(requestedActionId)
                && !actionIds.Contains(resolvedActionId);
        }
        catch
        {
            return false;
        }
    }

    private static void AssessSingletonPlugin(
        IEnumerable<IExposedPlugin> loadedPlugins,
        string internalName,
        ICollection<string> conflicts,
        Action<IExposedPlugin> assess)
    {
        var matches = loadedPlugins
            .Where(plugin => string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0) return;
        if (matches.Length > 1)
        {
            conflicts.Add($"Multiple loaded {internalName} instances were detected; keep only one installed copy and reload Dalamud.");
            return;
        }

        assess(matches[0]);
    }

    private static void AssessHardConflict(
        IEnumerable<IExposedPlugin> loadedPlugins,
        string internalName,
        ICollection<string> conflicts,
        string conflict)
    {
        if (loadedPlugins.Any(plugin =>
                string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)))
        {
            conflicts.Add(conflict);
        }
    }

    private static void AssessNoClippy(
        IExposedPlugin plugin,
        ICollection<string> conflicts,
        ICollection<string> integrations)
    {
        if (plugin.Version != SupportedNoClippyVersion)
        {
            conflicts.Add(
                $"NoClippy {plugin.Version} is not audited; install NoClippy {SupportedNoClippyVersion} or disable it.");
            return;
        }

        integrations.Add($"NoClippy {SupportedNoClippyVersion} (animation-lock timing)");
    }

    private void AssessReAction(
        IExposedPlugin plugin,
        ICollection<string> conflicts,
        ICollection<string> integrations)
    {
        if (plugin.Version != SupportedReActionVersion)
        {
            ClearLiveReActionGuard();
            conflicts.Add(
                $"ReAction {plugin.Version} is not audited; install ReAction {SupportedReActionVersion} or disable it.");
            return;
        }

        if (!TryReadReActionConfiguration(plugin, out var configuration, out var liveConfiguration))
        {
            ClearLiveReActionGuard();
            conflicts.Add(
                $"ReAction {SupportedReActionVersion} settings could not be verified; reload ReAction or disable it.");
            return;
        }

        SetLiveReActionGuard(liveConfiguration, configuration);

        var safe = true;
        if (configuration.ActionStackCount != 0)
        {
            conflicts.Add("ReAction Action Stacks must be empty while PulseQueue is enabled.");
            safe = false;
        }

        if (configuration.AutoTargetEnabled)
        {
            conflicts.Add("Disable ReAction's Auto Target while PulseQueue is enabled.");
            safe = false;
        }

        if (configuration.TurboHotbarsEnabled)
        {
            conflicts.Add("Disable ReAction's Turbo Hotbars; PulseQueue native Turbo can replace it without competing repeat owners.");
            safe = false;
        }

        if (configuration.MacroQueueEnabled)
        {
            conflicts.Add("Disable ReAction's Macro Queue; PulseQueue cannot prove exact macro ownership while ReAction rewrites macro action queue mode.");
            safe = false;
        }

        // These capabilities operate only on inputs PulseQueue already refuses
        // to own. Mounted presses fail the complete safety snapshot, and every
        // movement/position-changing action (including ReAction's directionals
        // exception) is excluded from buffering and Turbo. Keep the fields in
        // the live snapshot so a settings change still invalidates active work,
        // but do not globally suspend unrelated actions.
        if (configuration.AutoDismountEnabled)
        {
            integrations.Add("ReAction Auto Dismount (mounted inputs passed through, never owned)");
        }

        if (configuration.CameraRelativeDirectionalsEnabled)
        {
            integrations.Add("ReAction Camera Relative Directionals (movement actions excluded)");
        }

        if (safe)
        {
            integrations.Add($"ReAction {SupportedReActionVersion} (audited guarded mode)");
        }
    }

    private bool TryReadReActionConfiguration(
        IExposedPlugin expectedPlugin,
        out ReActionConfigurationSnapshot configuration,
        out object liveConfiguration)
    {
        configuration = default;
        liveConfiguration = null!;

        try
        {
            Type? pluginType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                IExposedPlugin? owner;
                try
                {
                    owner = pluginInterface.GetPlugin(assembly);
                }
                catch
                {
                    continue;
                }

                if (owner is null
                    || !owner.IsLoaded
                    || !string.Equals(owner.InternalName, expectedPlugin.InternalName, StringComparison.OrdinalIgnoreCase)
                    || owner.Version != expectedPlugin.Version)
                {
                    continue;
                }

                var candidate = assembly.GetType("ReAction.ReAction", throwOnError: false, ignoreCase: false);
                if (candidate is null) continue;
                if (pluginType is not null) return false;
                pluginType = candidate;
            }

            if (pluginType is null) return false;

            var configProperty = pluginType.GetProperty(
                "Config",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (configProperty?.GetMethod is null || configProperty.GetIndexParameters().Length != 0) return false;

            var config = configProperty.GetValue(null);
            if (config is null || !string.Equals(config.GetType().FullName, "ReAction.Configuration", StringComparison.Ordinal))
            {
                return false;
            }

            if (!TryReadReActionConfigurationObject(config, out configuration)) return false;
            liveConfiguration = config;
            return true;
        }
        catch
        {
            configuration = default;
            liveConfiguration = null!;
            return false;
        }
    }

    private static bool TryReadReActionConfigurationObject(
        object config,
        out ReActionConfigurationSnapshot configuration)
    {
        configuration = default;
        if (!string.Equals(config.GetType().FullName, "ReAction.Configuration", StringComparison.Ordinal))
        {
            return false;
        }

        var configType = config.GetType();
        if (!TryReadCollectionCount(configType, config, "ActionStacks", out var actionStackCount)
            || !TryReadBoolean(configType, config, "EnableAutoTarget", out var autoTargetEnabled)
            || !TryReadBoolean(configType, config, "EnableTurboHotbars", out var turboHotbarsEnabled)
            || !TryReadBoolean(configType, config, "EnableMacroQueue", out var macroQueueEnabled)
            || !TryReadBoolean(configType, config, "EnableAutoDismount", out var autoDismountEnabled)
            || !TryReadBoolean(
                configType,
                config,
                "EnableCameraRelativeDirectionals",
                out var cameraRelativeDirectionalsEnabled))
        {
            return false;
        }

        configuration = new ReActionConfigurationSnapshot(
            actionStackCount,
            autoTargetEnabled,
            turboHotbarsEnabled,
            macroQueueEnabled,
            autoDismountEnabled,
            cameraRelativeDirectionalsEnabled);
        return true;
    }

    private void SetLiveReActionGuard(
        object configuration,
        ReActionConfigurationSnapshot snapshot)
    {
        lock (liveGuardGate)
        {
            liveReActionConfiguration = new WeakReference<object>(configuration);
            auditedReActionConfiguration = snapshot;
        }
    }

    private void ClearLiveReActionGuard()
    {
        lock (liveGuardGate)
        {
            liveReActionConfiguration = null;
            auditedReActionConfiguration = null;
        }
    }

    private static bool TryReadCollectionCount(
        Type configType,
        object config,
        string fieldName,
        out int count)
    {
        count = 0;
        var field = configType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field is null) return false;

        var value = field.GetValue(config);
        if (value is ICollection collection)
        {
            count = collection.Count;
            return count >= 0;
        }

        if (value is null) return false;
        var countProperty = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProperty?.PropertyType != typeof(int)
            || countProperty.GetMethod is null
            || countProperty.GetIndexParameters().Length != 0)
        {
            return false;
        }

        var rawCount = countProperty.GetValue(value);
        if (rawCount is not int typedCount || typedCount < 0) return false;
        count = typedCount;
        return true;
    }

    private static bool TryReadBoolean(
        Type configType,
        object config,
        string fieldName,
        out bool value)
    {
        value = false;
        var field = configType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field?.FieldType != typeof(bool)) return false;
        var rawValue = field.GetValue(config);
        if (rawValue is not bool typedValue) return false;
        value = typedValue;
        return true;
    }

    private void AssessMOAction(
        IExposedPlugin plugin,
        ICollection<string> conflicts,
        ICollection<string> integrations,
        ISet<uint> excludedActionIds)
    {
        if (plugin.Version != SupportedMOActionVersion)
        {
            Volatile.Write(ref auditedMOActionLoaded, 0);
            conflicts.Add(
                $"MOAction {plugin.Version} is not audited; install MOAction {SupportedMOActionVersion} or disable it.");
            return;
        }

        try
        {
            var subscriber = pluginInterface.GetIpcSubscriber<uint[]>(MOActionRetargetedActionsIpc);
            if (!subscriber.HasFunction)
            {
                Volatile.Write(ref auditedMOActionLoaded, 0);
                conflicts.Add(
                    $"MOAction {SupportedMOActionVersion} retargeted-action data is unavailable; reload MOAction or disable it.");
                return;
            }

            var actionIds = subscriber.InvokeFunc();
            if (actionIds is null)
            {
                Volatile.Write(ref auditedMOActionLoaded, 0);
                conflicts.Add(
                    $"MOAction {SupportedMOActionVersion} returned no retargeted-action data; reload MOAction or disable it.");
                return;
            }

            foreach (var actionId in actionIds)
            {
                if (actionId != 0) excludedActionIds.Add(actionId);
            }

            integrations.Add(
                $"MOAction {SupportedMOActionVersion} ({excludedActionIds.Count} retargeted actions excluded)");
            Volatile.Write(ref auditedMOActionLoaded, 1);
        }
        catch
        {
            Volatile.Write(ref auditedMOActionLoaded, 0);
            conflicts.Add(
                $"MOAction {SupportedMOActionVersion} retargeted-action data could not be read; reload MOAction or disable it.");
        }
    }

    private static PluginCompatibilityAssessment CreateAssessment(
        IEnumerable<string> conflicts,
        IEnumerable<string> integrations,
        IEnumerable<uint> excludedActionIds)
    {
        var immutableConflicts = conflicts
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var immutableIntegrations = integrations
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        var immutableExcludedActionIds = excludedActionIds.ToImmutableHashSet();

        var canonical = new StringBuilder(AssessmentFormat);
        foreach (var conflict in immutableConflicts) canonical.Append("\nconflict:").Append(conflict);
        foreach (var integration in immutableIntegrations) canonical.Append("\nintegration:").Append(integration);
        foreach (var actionId in immutableExcludedActionIds.Order()) canonical.Append("\nexcluded:").Append(actionId);

        var signatureBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        var signature = $"{AssessmentFormat}:{Convert.ToHexString(signatureBytes)}";
        return new PluginCompatibilityAssessment(
            immutableConflicts,
            immutableIntegrations,
            immutableExcludedActionIds,
            signature);
    }

    private readonly record struct ReActionConfigurationSnapshot(
        int ActionStackCount,
        bool AutoTargetEnabled,
        bool TurboHotbarsEnabled,
        bool MacroQueueEnabled,
        bool AutoDismountEnabled,
        bool CameraRelativeDirectionalsEnabled);
}
