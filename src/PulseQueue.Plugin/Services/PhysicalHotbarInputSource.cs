using System.Diagnostics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using PulseQueue.Core;

namespace PulseQueue.Plugin.Services;

internal readonly record struct StandardHotbarBinding(
    InputId InputId,
    uint HotbarId,
    uint SlotId)
{
    private const int FirstInputId = (int)InputId.HOTBAR_1_1;
    private const int LastInputId = (int)InputId.HOTBAR_10_B;
    private const int SlotsPerHotbar = 12;

    public static int BindingCount => LastInputId - FirstInputId + 1;

    public static bool TryFromInputId(InputId inputId, out StandardHotbarBinding binding)
    {
        var raw = (int)inputId;
        if (raw < FirstInputId || raw > LastInputId)
        {
            binding = default;
            return false;
        }

        var offset = raw - FirstInputId;
        binding = new StandardHotbarBinding(
            inputId,
            (uint)(offset / SlotsPerHotbar),
            (uint)(offset % SlotsPerHotbar));
        return true;
    }

    public static bool TryFromSlot(uint hotbarId, uint slotId, out StandardHotbarBinding binding)
    {
        if (hotbarId >= 10 || slotId >= SlotsPerHotbar)
        {
            binding = default;
            return false;
        }

        var offset = checked((int)((hotbarId * SlotsPerHotbar) + slotId));
        binding = new StandardHotbarBinding(
            (InputId)(FirstInputId + offset),
            hotbarId,
            slotId);
        return true;
    }

    public int Index => (int)InputId - FirstInputId;
}

/// <summary>
/// Settings captured once for a native standard-hotbar binding scan. When an
/// external repeat owner is active PulseQueue observes/delegates its pulses and
/// never manufactures a second pulse.
/// </summary>
internal readonly record struct NativeHotbarRepeatSettings(
    bool RepeatEnabled,
    bool ExternalRepeatOwnerActive,
    int InitialDelayMilliseconds,
    int RepeatIntervalMilliseconds)
{
    public static NativeHotbarRepeatSettings Disabled => new(
        RepeatEnabled: false,
        ExternalRepeatOwnerActive: false,
        InitialDelayMilliseconds: 0,
        RepeatIntervalMilliseconds: LogicalHotbarRepeatOptions.MinimumRepeatIntervalMilliseconds);

    public NativeHotbarRepeatSettings Normalize()
    {
        var options = new LogicalHotbarRepeatOptions(
            InitialDelayMilliseconds,
            RepeatIntervalMilliseconds).Normalize();
        return this with
        {
            InitialDelayMilliseconds = options.InitialDelayMilliseconds,
            RepeatIntervalMilliseconds = options.RepeatIntervalMilliseconds,
        };
    }

    public LogicalHotbarRepeatOptions ToCoreOptions() => new LogicalHotbarRepeatOptions(
        InitialDelayMilliseconds,
        RepeatIntervalMilliseconds).Normalize();
}

internal enum HotbarActivationKind
{
    PhysicalPress = 0,
    InjectedRepeat,
    DelegatedRepeat,
}

internal readonly record struct CertifiedHotbarPress(
    long PressId,
    long LifecycleGeneration,
    StandardHotbarBinding Binding,
    SeVirtualKey PhysicalKey,
    KeyModifierFlag RequiredModifiers,
    KeyModifierFlag ActiveModifiers,
    byte KeySettingIndex,
    nint InputDataAddress,
    long ObservedAtMilliseconds);

internal readonly record struct HotbarActivation(
    HotbarActivationKind Kind,
    CertifiedHotbarPress Press,
    long ObservedAtMilliseconds,
    bool SuppressedByNewerInput = false)
{
    public StandardHotbarBinding Binding => Press.Binding;
}

internal readonly record struct NativeHotbarRepeatTelemetry(
    long Observations,
    long PhysicalPresses,
    long InjectedRepeats,
    long DelegatedRepeats,
    long Releases,
    long HoldsPreempted,
    long SuppressedOlderHolds,
    long FailedOpenEvents,
    long OwnerLogicalInputId,
    uint OwnerHotbarId,
    uint OwnerSlotId,
    NativeHotbarRepeatSettings Settings);

/// <summary>
/// Converts the player's held logical standard-hotbar binding into periodic
/// native "pressed" results while the game is resolving those bindings. FFXIV
/// therefore remains the sole authority for the selected slot, action, target,
/// macro and queue semantics.
/// </summary>
internal sealed unsafe class PhysicalHotbarInputSource : IDisposable
{
    private const string CheckHotbarBindingsSignature = "89 54 24 10 53 41 55 41 57";
    private const long MaximumCorrelationAgeMilliseconds = 50;

    [ThreadStatic]
    private static PhysicalHotbarInputSource? activeScanSource;

    [ThreadStatic]
    private static NativeHotbarRepeatSettings activeScanSettings;

    private readonly Hook<InputData.Delegates.IsInputIdPressed> pressedHook;
    private readonly Hook<CheckHotbarBindingsDelegate> checkHotbarBindingsHook;
    private readonly Func<NativeHotbarRepeatSettings> getSettings;
    private readonly Action<CertifiedHotbarPress> onPhysicalPress;
    private readonly object gate = new();
    private readonly PendingActivation?[] pendingActivations = new PendingActivation?[StandardHotbarBinding.BindingCount];
    private readonly CertifiedHotbarPress?[] currentPresses = new CertifiedHotbarPress?[StandardHotbarBinding.BindingCount];

    private LogicalHotbarRepeatEngine repeatEngine = new();
    private LogicalHotbarRepeatOptions repeatOptions = LogicalHotbarRepeatOptions.Default;
    private NativeHotbarRepeatSettings lastSettings = NativeHotbarRepeatSettings.Disabled;
    private nint currentInputDataAddress;
    private long nextPressId;
    private long lifecycleGeneration = 1;
    private long observations;
    private long physicalPresses;
    private long injectedRepeats;
    private long delegatedRepeats;
    private long releases;
    private long holdsPreempted;
    private long suppressedOlderHolds;
    private long failedOpenEvents;
    private bool started;
    private bool disposed;

    private delegate void CheckHotbarBindingsDelegate(nint context, byte mode);

    public PhysicalHotbarInputSource(
        IGameInteropProvider interop,
        Func<NativeHotbarRepeatSettings> getSettings,
        Action<CertifiedHotbarPress> onPhysicalPress)
    {
        ArgumentNullException.ThrowIfNull(interop);
        this.getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        this.onPhysicalPress = onPhysicalPress ?? throw new ArgumentNullException(nameof(onPhysicalPress));

        pressedHook = interop.HookFromAddress<InputData.Delegates.IsInputIdPressed>(
            InputData.MemberFunctionPointers.IsInputIdPressed,
            IsInputIdPressedDetour);
        try
        {
            checkHotbarBindingsHook = interop.HookFromSignature<CheckHotbarBindingsDelegate>(
                CheckHotbarBindingsSignature,
                CheckHotbarBindingsDetour);
        }
        catch
        {
            pressedHook.Dispose();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) return;

        pressedHook.Enable();
        try
        {
            checkHotbarBindingsHook.Enable();
            started = true;
        }
        catch
        {
            pressedHook.Disable();
            throw;
        }
    }

    public long SuppressedHeldRepeatCount => Interlocked.Read(ref suppressedOlderHolds);

    public LogicalHotbarRepeatSnapshot RepeatSnapshot
    {
        get
        {
            lock (gate) return repeatEngine.Snapshot;
        }
    }

    public NativeHotbarRepeatTelemetry Telemetry
    {
        get
        {
            lock (gate)
            {
                var ownerInputId = repeatEngine.Snapshot.OwnerLogicalInputId;
                var ownerBinding = StandardHotbarBinding.TryFromInputId(
                    (InputId)ownerInputId,
                    out var binding)
                    ? binding
                    : default;
                return new NativeHotbarRepeatTelemetry(
                    Interlocked.Read(ref observations),
                    Interlocked.Read(ref physicalPresses),
                    Interlocked.Read(ref injectedRepeats),
                    Interlocked.Read(ref delegatedRepeats),
                    Interlocked.Read(ref releases),
                    Interlocked.Read(ref holdsPreempted),
                    Interlocked.Read(ref suppressedOlderHolds),
                    Interlocked.Read(ref failedOpenEvents),
                    ownerInputId,
                    ownerBinding.HotbarId,
                    ownerBinding.SlotId,
                    lastSettings);
            }
        }
    }

    public bool TryConsumeActivation(
        RaptureHotbarModule* hotbarModule,
        RaptureHotbarModule.HotbarSlot* slot,
        long nowMilliseconds,
        out HotbarActivation activation)
    {
        activation = default;
        if (hotbarModule == null || slot == null) return false;

        lock (gate)
        {
            for (var index = 0; index < pendingActivations.Length; index++)
            {
                var pending = pendingActivations[index];
                if (pending is null) continue;
                if (!IsFresh(pending.Value, nowMilliseconds))
                {
                    pendingActivations[index] = null;
                    continue;
                }

                var candidate = pending.Value.Activation;
                var expected = hotbarModule->GetSlotById(
                    candidate.Binding.HotbarId,
                    candidate.Binding.SlotId);
                if (expected != slot) continue;
                if (pending.Value.RequiresActiveScan && activeScanSource != this) return false;

                pendingActivations[index] = null;
                activation = candidate;
                if (pending.Value.CountDelegationOnConsume)
                {
                    Interlocked.Increment(ref delegatedRepeats);
                }

                return true;
            }
        }

        return false;
    }

    public bool TryConsumeActivation(
        uint hotbarId,
        uint slotId,
        long nowMilliseconds,
        out HotbarActivation activation)
    {
        activation = default;
        if (!StandardHotbarBinding.TryFromSlot(hotbarId, slotId, out var binding)) return false;

        lock (gate)
        {
            var pending = pendingActivations[binding.Index];
            if (pending is null || !IsFresh(pending.Value, nowMilliseconds))
            {
                pendingActivations[binding.Index] = null;
                return false;
            }

            if (pending.Value.RequiresActiveScan && activeScanSource != this) return false;

            pendingActivations[binding.Index] = null;
            activation = pending.Value.Activation;
            if (pending.Value.CountDelegationOnConsume)
            {
                Interlocked.Increment(ref delegatedRepeats);
            }

            return true;
        }
    }

    public bool IsStillHeld(CertifiedHotbarPress press)
    {
        if (disposed || press.InputDataAddress == nint.Zero) return false;
        lock (gate)
        {
            if (press.LifecycleGeneration != lifecycleGeneration
                || currentInputDataAddress != press.InputDataAddress)
            {
                return false;
            }
        }

        try
        {
            return ((InputData*)press.InputDataAddress)->IsInputIdHeld(press.Binding.InputId);
        }
        catch
        {
            Interlocked.Increment(ref failedOpenEvents);
            return false;
        }
    }

    public void DiscardPending()
    {
        lock (gate) Array.Clear(pendingActivations);
    }

    /// <summary>
    /// Terminates native repeat ownership without allowing a key that remains
    /// physically held across the boundary to resume. Each such logical input
    /// must be observed released before it can own or delegate repeats again.
    /// Previously certified press handles are invalidated as part of the same
    /// atomic lifecycle transition.
    /// </summary>
    /// <returns>The number of currently held logical inputs release-gated.</returns>
    public int CancelAndRequireRelease()
    {
        lock (gate)
        {
            var gatedInputs = repeatEngine.CancelAndRequireRelease();
            Array.Clear(pendingActivations);
            Array.Clear(currentPresses);
            currentInputDataAddress = nint.Zero;
            lifecycleGeneration = lifecycleGeneration == long.MaxValue
                ? 1
                : lifecycleGeneration + 1;
            return gatedInputs;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        started = false;

        try
        {
            checkHotbarBindingsHook.Dispose();
        }
        finally
        {
            pressedHook.Dispose();
        }

        lock (gate)
        {
            Array.Clear(pendingActivations);
            Array.Clear(currentPresses);
            currentInputDataAddress = nint.Zero;
        }
    }

    private void CheckHotbarBindingsDetour(nint context, byte mode)
    {
        var previousSource = activeScanSource;
        var previousSettings = activeScanSettings;
        activeScanSource = this;
        activeScanSettings = ReadSettingsFailOpen();
        try
        {
            checkHotbarBindingsHook.Original(context, mode);
        }
        finally
        {
            activeScanSource = previousSource;
            activeScanSettings = previousSettings;
        }
    }

    private bool IsInputIdPressedDetour(InputData* inputData, InputId inputId)
    {
        // Native input is queried first and remains the fallback for every local
        // failure. This hook never substitutes a different logical binding.
        var nativePressed = pressedHook.Original(inputData, inputId);
        if (disposed
            || activeScanSource != this
            || inputData == null
            || !StandardHotbarBinding.TryFromInputId(inputId, out var binding))
        {
            return nativePressed;
        }

        try
        {
            var held = inputData->IsInputIdHeld(inputId);
            var now = NowMilliseconds;
            var settings = activeScanSettings;
            CertifiedHotbarPress? physicalPress = null;
            bool reportPressed;

            lock (gate)
            {
                currentInputDataAddress = (nint)inputData;
                EnsureEngine(settings);
                var before = repeatEngine.Snapshot;
                var decision = repeatEngine.Observe(new LogicalHotbarRepeatObservation(
                    (long)inputId,
                    nativePressed,
                    held,
                    now,
                    settings.RepeatEnabled,
                    settings.ExternalRepeatOwnerActive));
                var after = repeatEngine.Snapshot;

                Interlocked.Increment(ref observations);
                AddPositiveDelta(ref holdsPreempted, before.Counters.HoldsPreempted, after.Counters.HoldsPreempted);
                AddPositiveDelta(ref releases, before.Counters.Releases, after.Counters.Releases);
                AddPositiveDelta(
                    ref suppressedOlderHolds,
                    before.Counters.SuppressedOlderHolds,
                    after.Counters.SuppressedOlderHolds);

                reportPressed = settings.RepeatEnabled || settings.ExternalRepeatOwnerActive
                    ? decision.ShouldReportPressed
                    : nativePressed;

                switch (decision.Kind)
                {
                    case LogicalHotbarRepeatDecisionKind.PhysicalPress:
                        {
                            var releaseProvenFreshEdge = decision.IsFreshPhysicalEdge;
                            var press = releaseProvenFreshEdge
                                ? CreatePress(binding, inputData, now)
                                : GetOrCreateCurrentPress(binding, inputData, now);
                            if (held) currentPresses[binding.Index] = press;
                            else currentPresses[binding.Index] = null;
                            if (!releaseProvenFreshEdge && settings.ExternalRepeatOwnerActive)
                            {
                                // A native-pressed signal without a release-proven
                                // ownership claim may be an outer ReAction pulse at
                                // startup or after a timing reset. It is never allowed
                                // to masquerade as the player's newer physical intent.
                                pendingActivations[binding.Index] = new PendingActivation(
                                    new HotbarActivation(HotbarActivationKind.DelegatedRepeat, press, now),
                                    RequiresActiveScan: false,
                                    CountDelegationOnConsume: false);
                                Interlocked.Increment(ref delegatedRepeats);
                            }
                            else if (releaseProvenFreshEdge)
                            {
                                physicalPress = press;
                                pendingActivations[binding.Index] = new PendingActivation(
                                    new HotbarActivation(HotbarActivationKind.PhysicalPress, press, now),
                                    RequiresActiveScan: false,
                                    CountDelegationOnConsume: false);
                                Interlocked.Increment(ref physicalPresses);
                            }
                            else
                            {
                                // Preserve the vanilla result but publish no certified
                                // edge until this logical input has actually released.
                                pendingActivations[binding.Index] = null;
                            }
                            break;
                        }

                    case LogicalHotbarRepeatDecisionKind.InjectedRepeat:
                        {
                            var press = GetOrCreateCurrentPress(binding, inputData, now);
                            pendingActivations[binding.Index] = new PendingActivation(
                                new HotbarActivation(HotbarActivationKind.InjectedRepeat, press, now),
                                RequiresActiveScan: false,
                                CountDelegationOnConsume: false);
                            Interlocked.Increment(ref injectedRepeats);
                            break;
                        }

                    case LogicalHotbarRepeatDecisionKind.DelegatedRepeat:
                        {
                            var press = GetOrCreateCurrentPress(binding, inputData, now);
                            pendingActivations[binding.Index] = new PendingActivation(
                                new HotbarActivation(HotbarActivationKind.DelegatedRepeat, press, now),
                                RequiresActiveScan: false,
                                CountDelegationOnConsume: false);
                            Interlocked.Increment(ref delegatedRepeats);
                            break;
                        }

                    case LogicalHotbarRepeatDecisionKind.Released:
                        currentPresses[binding.Index] = null;
                        pendingActivations[binding.Index] = null;
                        break;

                    case LogicalHotbarRepeatDecisionKind.SuppressedOlderHold:
                        // When our pressed hook is inside ReAction's hook, the
                        // external hook can still turn our false return into a
                        // held-key pulse. Preserve that exact-slot provenance so
                        // ExecuteSlot can reject only this superseded repeat.
                        if (settings.ExternalRepeatOwnerActive && held)
                        {
                            var press = GetOrCreateCurrentPress(binding, inputData, now);
                            pendingActivations[binding.Index] = new PendingActivation(
                                new HotbarActivation(
                                    HotbarActivationKind.DelegatedRepeat,
                                    press,
                                    now,
                                    SuppressedByNewerInput: true),
                                RequiresActiveScan: true,
                                CountDelegationOnConsume: true);
                        }
                        else
                        {
                            pendingActivations[binding.Index] = null;
                        }
                        break;

                    case LogicalHotbarRepeatDecisionKind.None:
                        // If ReAction wraps this hook, it may turn our false return
                        // into a held-key pulse after we return. Leave an exact-slot
                        // candidate that is valid only inside this binding scan so
                        // ExecuteSlot can still classify that pulse as delegated.
                        if (settings.ExternalRepeatOwnerActive
                            && held)
                        {
                            var press = GetOrCreateCurrentPress(binding, inputData, now);
                            pendingActivations[binding.Index] = new PendingActivation(
                                new HotbarActivation(HotbarActivationKind.DelegatedRepeat, press, now),
                                RequiresActiveScan: true,
                                CountDelegationOnConsume: true);
                        }
                        break;
                }
            }

            if (physicalPress is { } observed)
            {
                try
                {
                    onPhysicalPress(observed);
                }
                catch
                {
                    // Certification is atomic with the consumer callback: if
                    // the consumer cannot invalidate older ownership, this exact
                    // press must not remain consumable as certified provenance.
                    // A newer press for the same slot may already have replaced
                    // it, so clear only the matching press identity.
                    lock (gate)
                    {
                        var index = observed.Binding.Index;
                        if (pendingActivations[index] is { } pending
                            && pending.Activation.Kind == HotbarActivationKind.PhysicalPress
                            && pending.Activation.Press.PressId == observed.PressId)
                        {
                            pendingActivations[index] = null;
                        }
                    }

                    Interlocked.Increment(ref failedOpenEvents);
                    return nativePressed;
                }
            }

            return reportPressed;
        }
        catch
        {
            Interlocked.Increment(ref failedOpenEvents);
            return nativePressed;
        }
    }

    private NativeHotbarRepeatSettings ReadSettingsFailOpen()
    {
        try
        {
            return getSettings().Normalize();
        }
        catch
        {
            Interlocked.Increment(ref failedOpenEvents);
            return NativeHotbarRepeatSettings.Disabled;
        }
    }

    private void EnsureEngine(NativeHotbarRepeatSettings settings)
    {
        var options = settings.ToCoreOptions();
        lastSettings = settings;
        if (options == repeatOptions) return;

        repeatOptions = options;
        repeatEngine.ReconfigureAndRequireRelease(options);
        Array.Clear(currentPresses);
        Array.Clear(pendingActivations);
        lifecycleGeneration = lifecycleGeneration == long.MaxValue
            ? 1
            : lifecycleGeneration + 1;
    }

    private CertifiedHotbarPress GetOrCreateCurrentPress(
        StandardHotbarBinding binding,
        InputData* inputData,
        long nowMilliseconds)
    {
        if (currentPresses[binding.Index] is { } current) return current;
        var created = CreatePress(binding, inputData, nowMilliseconds);
        currentPresses[binding.Index] = created;
        return created;
    }

    private CertifiedHotbarPress CreatePress(
        StandardHotbarBinding binding,
        InputData* inputData,
        long nowMilliseconds) =>
        new(
            Interlocked.Increment(ref nextPressId),
            lifecycleGeneration,
            binding,
            SeVirtualKey.NO_KEY,
            KeyModifierFlag.None,
            KeyModifierFlag.None,
            byte.MaxValue,
            (nint)inputData,
            nowMilliseconds);

    private static bool IsFresh(PendingActivation pending, long nowMilliseconds)
    {
        var age = nowMilliseconds - pending.Activation.ObservedAtMilliseconds;
        return age is >= 0 and <= MaximumCorrelationAgeMilliseconds;
    }

    private static void AddPositiveDelta(ref long target, long before, long after)
    {
        var delta = after - before;
        if (delta > 0) Interlocked.Add(ref target, delta);
    }

    private static long NowMilliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private readonly record struct PendingActivation(
        HotbarActivation Activation,
        bool RequiresActiveScan,
        bool CountDelegationOnConsume);
}
