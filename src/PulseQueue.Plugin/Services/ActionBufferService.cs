using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using PulseQueue.Core;
using PulseQueue.Plugin.Models;
using GameAction = Lumina.Excel.Sheets.Action;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;

namespace PulseQueue.Plugin.Services;

internal enum RuntimeState
{
    Off,
    Ready,
    Pending,
    DryRun,
    Suspended,
    Faulted,
}

internal enum OwnedQueueCancelPolicy
{
    Preserve,
    ExactClear,
}

internal sealed record BufferDiagnostics(
    RuntimeState State,
    string Status,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Integrations,
    int ExcludedIntegrationActions,
    int HoldWindowMilliseconds,
    double EstimatedResponseMilliseconds,
    int AcceptedTimingSamples,
    long Captured,
    long Dispatched,
    long DryRunDispatches,
    long ReplayRejected,
    long ObservedHotbarInputs,
    long ReplacedPendingInputs,
    long NativeQueueAccepted,
    long NativeQueueBlocked,
    long OwnedNativeQueueReplacements,
    long OwnedNativeQueueSafetyClears,
    long RepeatNativeQueueClaims,
    long RepeatNativeQueueReplacements,
    long IntegrationExclusions,
    bool TurboConfigured,
    bool TurboInputAvailable,
    long TurboPhysicalPresses,
    long TurboInjectedRepeats,
    long TurboDelegatedRepeats,
    long TurboPreemptions,
    long TurboReleases,
    long TurboFailedOpenEvents,
    HoldRepeatState TurboState,
    long TurboStarts,
    long TurboPulses,
    long TurboSuppressedHeldRepeats,
    long TurboAccepted,
    long TurboRejected,
    HoldRepeatCancelReason TurboLastCancelReason,
    string TurboStatus,
    CancelReason LastCancelReason,
    string LastEvent);

/// <summary>
/// Bridges a certified native hotbar execution to the dependency-free one-shot engine.
/// The original player call always happens first and exactly once. This class never writes
/// animation lock, recast state, or targets. Native queue mutation is limited
/// to an atomically proven PulseQueue-owned exact entry during replacement or
/// terminal safety cancellation.
/// </summary>
internal sealed unsafe class ActionBufferService : IDisposable
{
    private const ulong InvalidObjectId = 0xE0000000;
    private const ushort StunStatusId = 2;
    private const double AnimationLockEpsilonSeconds = 0.0005;
    private const long MaximumFrameGapMilliseconds = 100;
    private const long MaximumTimingSampleAgeMilliseconds = 2_000;
    private const long MaximumRecentActionEffectAgeMilliseconds = 2_000;
    private const int MaximumActionEffectTargets = 32;
    private const byte KnockbackActionEffectType = 33;
    private const int CompatibilityPollIntervalMilliseconds = 500;
    private const long MaximumTurboAcknowledgementMilliseconds = 2_000;
    private const int MaximumMacroCaptureMilliseconds = 2_000;
    private const int NativeMacroRepeatStartGraceMilliseconds = 100;
    private const int LogicalRepeatQueueCorrelationMilliseconds = 250;
    private const uint ReActionCameraRelativeMovementException = 29494;
    private const uint DirectActionHotbarSlotType = 1;
    private const uint MacroHotbarSlotType = 7;

    [ThreadStatic]
    private static int hotbarExecutionDepth;

    [ThreadStatic]
    private static int logicalRepeatExecutionDepth;

    [ThreadStatic]
    private static NativeLogicalRepeatExecutionScope? activeLogicalRepeatExecution;

    [ThreadStatic]
    private static bool replaying;

    [ThreadStatic]
    private static bool turboDispatching;

    [ThreadStatic]
    private static HotbarInputScope? activeHotbarInput;

    [ThreadStatic]
    private static MacroPulseExecutionScope? activeMacroPulseExecution;

    [ThreadStatic]
    private static DirectPulseExecutionScope? activeDirectPulseExecution;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly PluginConfiguration configuration;
    private readonly PluginCompatibilityService compatibility;
    private readonly object dispatchGate = new();
    private readonly BufferEngine engine = new();
    private readonly InputGenerationGate inputGenerations = new();
    private readonly NativeQueueOwnership nativeQueueOwnership = new();
    private readonly RepeatNativeQueueOwnership logicalRepeatQueueOwnership = new();
    private readonly AdaptiveRttEstimator latency = new(new AdaptiveRttOptions
    {
        MinimumSuggestedHold = TimeSpan.FromMilliseconds(80),
        MaximumSuggestedHold = TimeSpan.FromMilliseconds(BufferEngine.AbsoluteHoldCapMilliseconds),
        MaximumAcceptedSample = TimeSpan.FromMilliseconds(MaximumTimingSampleAgeMilliseconds),
        SafetyMargin = TimeSpan.FromMilliseconds(8),
    });
    private readonly ConcurrentDictionary<ushort, long> sentSequences = new();
    private readonly ConcurrentQueue<long> observedResponseTimes = new();
    private readonly ConcurrentQueue<Exception> timingHookErrors = new();
    private readonly ConcurrentQueue<TimedTurboActionEffect> recentLocalActionEffects = new();
    private readonly Hook<ActionManager.Delegates.UseAction> useActionHook;
    private readonly Hook<RaptureHotbarModule.Delegates.ExecuteSlot> executeSlotHook;
    private readonly Hook<RaptureHotbarModule.Delegates.ExecuteSlotById> executeSlotByIdHook;
    private readonly Hook<ActionEffectHandler.Delegates.Receive> receiveActionEffectHook;

    private PhysicalHotbarInputSource? physicalHotbarInput;
    private HoldRepeatEngine turboEngine;

    private RuntimeAction? pendingRuntimeAction;
    private TurboRuntime? turboRuntime;
    private MacroTurboRuntime? macroTurboRuntime;
    private SyntheticMacroExecutorQuarantine? syntheticMacroExecutorQuarantine;
    private RetiredPhysicalMacroExecutor? retiredPhysicalMacroExecutor;
    private OwnedNativeQueueSafetyContext? ownedNativeQueueSafetyContext;
    private LogicalRepeatQueuePending? logicalRepeatQueuePending;
    private LogicalRepeatQueueAttempt? logicalRepeatQueueInFlight;
    private NativeMacroRepeatTail? nativeMacroRepeatTail;
    private IReadOnlyList<string> activeConflicts = Array.Empty<string>();
    private IReadOnlyList<string> activeIntegrations = Array.Empty<string>();
    private IReadOnlySet<uint> excludedIntegrationActionIds = new HashSet<uint>();
    private string compatibilitySignature = string.Empty;
    private bool reActionTurboHotbarsEnabled;
    private bool reActionTurboHotbarsOutOfCombatEnabled;
    private bool reActionMacroQueueEnabled;
    private bool reActionLoaded;
    private bool reActionAudited;
    private bool reActionSmartActionTransformActive;
    private long nextCompatibilityRefreshAt;
    private int compatibilityQuarantineFrames;
    private int pluginTopologyDirty;
    private long lastFrameworkAt;
    private long capturedCount;
    private long dispatchedCount;
    private long dryRunDispatchCount;
    private long replayRejectedCount;
    private long nativeQueueAcceptedCount;
    private long nativeQueueBlockedCount;
    private long ownedNativeQueueReplacementCount;
    private long ownedNativeQueueSafetyClearCount;
    private long observedHotbarInputCount;
    private long replacedPendingCount;
    private long integrationExclusionCount;
    private long logicalRepeatQueueClaimCount;
    private long logicalRepeatQueueReplacementCount;
    private long latestLogicalRepeatQueueReplacementGeneration;
    private long turboStartCount;
    private long turboPulseCount;
    private long turboAcceptedCount;
    private long turboRejectedCount;
    private long syntheticMacroSuppressedCallCount;
    private long latestCertifiedPressId;
    private TurboAcknowledgement? turboAcknowledgement;
    private MacroTurboAcknowledgement? macroTurboAcknowledgement;
    private uint localEntityId;
    private int forcedMovementObserved;
    private int timingHookErrorLogged;
    private bool ownedNativeQueueSafetyClearPending;
    private long ownedNativeQueueSafetyClearThroughGeneration;
    private long latestCertifiedQueueReplacementGeneration;
    private bool faulted;
    private bool faultLogged;
    private bool disposed;
    private NativeInputContext? lastNativeInputContext;
    private string turboInputUnavailableReason = string.Empty;
    private HoldRepeatCancelReason turboLastCancelReason;
    private string lastEvent = "Initialized";

    public ActionBufferService(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IDataManager dataManager,
        IFramework framework,
        IGameInteropProvider interop,
        IPluginLog log,
        PluginConfiguration configuration)
    {
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.dataManager = dataManager;
        this.framework = framework;
        this.log = log;
        this.configuration = configuration;
        compatibility = new PluginCompatibilityService(pluginInterface);
        turboEngine = new HoldRepeatEngine(CreateTurboOptions());

        try
        {
            physicalHotbarInput = new PhysicalHotbarInputSource(
                interop,
                GetNativeHotbarRepeatSettings,
                OnCertifiedPhysicalPress);
        }
        catch (Exception exception)
        {
            turboInputUnavailableReason = "native logical hotbar input hooks unavailable";
            log.Warning(exception, "PulseQueue native held-input Turbo is unavailable; the one-shot buffer will remain usable.");
        }

        useActionHook = interop.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.MemberFunctionPointers.UseAction,
            UseActionDetour);
        executeSlotHook = interop.HookFromAddress<RaptureHotbarModule.Delegates.ExecuteSlot>(
            RaptureHotbarModule.MemberFunctionPointers.ExecuteSlot,
            ExecuteSlotDetour);
        executeSlotByIdHook = interop.HookFromAddress<RaptureHotbarModule.Delegates.ExecuteSlotById>(
            RaptureHotbarModule.MemberFunctionPointers.ExecuteSlotById,
            ExecuteSlotByIdDetour);
        receiveActionEffectHook = interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            ActionEffectHandler.MemberFunctionPointers.Receive,
            ReceiveActionEffectDetour);
    }

    public BufferDiagnostics Diagnostics
    {
        get
        {
            var state = GetRuntimeState();
            var nativeRepeat = physicalHotbarInput?.Telemetry;
            var repeatSnapshot = physicalHotbarInput?.RepeatSnapshot;
            var repeatCounters = repeatSnapshot?.Counters ?? default;
            return new BufferDiagnostics(
                state,
                DescribeState(state),
                activeConflicts,
                activeIntegrations,
                excludedIntegrationActionIds.Count,
                CurrentHoldWindowMilliseconds,
                latency.EstimatedRtt.TotalMilliseconds,
                latency.AcceptedSampleCount,
                capturedCount,
                dispatchedCount,
                dryRunDispatchCount,
                replayRejectedCount,
                observedHotbarInputCount,
                replacedPendingCount,
                nativeQueueAcceptedCount,
                nativeQueueBlockedCount,
                ownedNativeQueueReplacementCount,
                ownedNativeQueueSafetyClearCount,
                logicalRepeatQueueClaimCount,
                logicalRepeatQueueReplacementCount,
                integrationExclusionCount,
                configuration.TurboEnabled,
                physicalHotbarInput is not null,
                nativeRepeat?.PhysicalPresses ?? 0,
                nativeRepeat?.InjectedRepeats ?? 0,
                nativeRepeat?.DelegatedRepeats ?? 0,
                nativeRepeat?.HoldsPreempted ?? 0,
                nativeRepeat?.Releases ?? 0,
                nativeRepeat?.FailedOpenEvents ?? 0,
                repeatSnapshot is { HasOwner: true }
                    ? HoldRepeatState.Active
                    : HoldRepeatState.Idle,
                repeatCounters.HoldsClaimed,
                (nativeRepeat?.InjectedRepeats ?? 0) + (nativeRepeat?.DelegatedRepeats ?? 0),
                physicalHotbarInput?.SuppressedHeldRepeatCount ?? 0,
                nativeRepeat?.InjectedRepeats ?? 0,
                nativeRepeat?.DelegatedRepeats ?? 0,
                HoldRepeatCancelReason.None,
                DescribeTurboState(),
                engine.LastCancelReason,
                lastEvent);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RefreshConflicts(force: true);
        lastFrameworkAt = NowMilliseconds;
        try
        {
            executeSlotHook.Enable();
            executeSlotByIdHook.Enable();
            receiveActionEffectHook.Enable();
            useActionHook.Enable();
            if (physicalHotbarInput is { } inputSource)
            {
                try
                {
                    inputSource.Start();
                }
                catch (Exception exception)
                {
                    DisposeSilently(inputSource);
                    physicalHotbarInput = null;
                    turboInputUnavailableReason = "native logical hotbar input hooks could not be enabled";
                    log.Warning(exception, "PulseQueue native held-input Turbo is unavailable; the one-shot buffer remains enabled.");
                }
            }
            framework.Update += OnFrameworkUpdate;
            pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
            if (activeConflicts.Count > 0)
            {
                log.Warning(
                    "PulseQueue loaded suspended. Resolve: {Conflicts}",
                    string.Join("; ", activeConflicts));
            }
            else
            {
                log.Information(
                    "PulseQueue ready. Smart-buffer cap={Cap} ms; native Turbo input={TurboAvailability}; Turbo configured={TurboConfigured}.",
                    BufferEngine.AbsoluteHoldCapMilliseconds,
                    physicalHotbarInput is null ? "unavailable" : "available",
                    configuration.TurboEnabled);
            }
        }
        catch
        {
            pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            framework.Update -= OnFrameworkUpdate;
            DisposeSilently(useActionHook);
            DisposeSilently(receiveActionEffectHook);
            DisposeSilently(executeSlotByIdHook);
            DisposeSilently(executeSlotHook);
            if (physicalHotbarInput is { } inputSource) DisposeSilently(inputSource);
            physicalHotbarInput = null;
            disposed = true;
            throw;
        }
    }

    public void Cancel(CancelReason reason, string detail)
    {
        if (reason == CancelReason.None) reason = CancelReason.Explicit;
        lock (dispatchGate)
        {
            var actionManager = RequiresNativeInputRelease(reason)
                ? ActionManager.Instance()
                : null;
            if (!configuration.DryRun && actionManager != null)
            {
                ResolveLogicalRepeatQueuePending(
                    actionManager,
                    NowMilliseconds,
                    $"before terminal cancellation {reason}");
            }
            if (RequiresNativeInputRelease(reason))
            {
                if (logicalRepeatQueueInFlight is { } inFlight)
                {
                    inFlight.SupersededByTerminalCancellation = true;
                }
                if (logicalRepeatQueuePending is { } pending)
                {
                    pending.SupersededByTerminalCancellation = true;
                }
            }

            inputGenerations.Invalidate();
            if (RequiresNativeInputRelease(reason))
            {
                var gatedInputs = physicalHotbarInput?.CancelAndRequireRelease() ?? 0;
                latestLogicalRepeatQueueReplacementGeneration = inputGenerations.Current;
                if (configuration.DryRun)
                {
                    AbandonOwnedQueueProvenanceForDetectOnly();
                }
                else if (actionManager != null)
                {
                    ResolveLogicalRepeatQueuePending(
                        actionManager,
                        NowMilliseconds,
                        $"during terminal cancellation {reason}");
                    TryReplaceLogicalRepeatNativeQueue(
                        actionManager,
                        latestLogicalRepeatQueueReplacementGeneration,
                        $"during terminal cancellation {reason}");
                }
                else
                {
                    logicalRepeatQueueOwnership.Clear();
                    logicalRepeatQueuePending = null;
                }

                if (gatedInputs > 0 && configuration.DetailedLogging)
                {
                    log.Information(
                        "Native repeat cancellation {Reason} requires release for {Count} held logical input(s).",
                        reason,
                        gatedInputs);
                }
            }

            engine.Cancel(reason);
            pendingRuntimeAction = null;
            recentLocalActionEffects.Clear();
            CancelTurboUnsafe(
                ToTurboCancelReason(reason),
                detail,
                ownedQueuePolicy: reason == CancelReason.Replaced
                    ? OwnedQueueCancelPolicy.Preserve
                    : OwnedQueueCancelPolicy.ExactClear);
            lastEvent = detail;
        }
    }

    private static bool RequiresNativeInputRelease(CancelReason reason) => reason is not (
        CancelReason.None or
        CancelReason.Replaced or
        CancelReason.Dispatched);

    public void ClearFaultForReload()
    {
        faulted = false;
        faultLogged = false;
        Cancel(CancelReason.Explicit, "Fault latch cleared manually");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel(CancelReason.Disabled, "Plugin disposed");
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        framework.Update -= OnFrameworkUpdate;
        lock (dispatchGate)
        {
            syntheticMacroExecutorQuarantine = null;
            retiredPhysicalMacroExecutor = null;
            activeHotbarInput = null;
        }
        useActionHook.Dispose();
        receiveActionEffectHook.Dispose();
        executeSlotByIdHook.Dispose();
        executeSlotHook.Dispose();
        physicalHotbarInput?.Dispose();
        physicalHotbarInput = null;
        sentSequences.Clear();
    }

    private byte ExecuteSlotDetour(RaptureHotbarModule* thisPtr, RaptureHotbarModule.HotbarSlot* slot)
    {
        var rootInput = hotbarExecutionDepth == 0 && !replaying && !turboDispatching;
        var repeatRoot = false;
        var managedRoot = false;
        var suppressPreemptedRepeat = false;
        NativeLogicalRepeatExecutionScope? repeatExecutionScope = null;
        NativeMacroRepeatRootAttempt? macroRepeatRootAttempt = null;
        if (rootInput)
        {
            try
            {
                HotbarActivation? activation = null;
                if (physicalHotbarInput?.TryConsumeActivation(
                        thisPtr,
                        slot,
                        NowMilliseconds,
                        out var observedActivation) == true)
                {
                    activation = observedActivation;
                    LogNativeRepeatActivation(observedActivation);
                }

                suppressPreemptedRepeat = activation is { SuppressedByNewerInput: true };
                repeatRoot = activation is
                {
                    Kind: HotbarActivationKind.InjectedRepeat or HotbarActivationKind.DelegatedRepeat,
                    SuppressedByNewerInput: false,
                };
                if (repeatRoot && activation is { } repeatedActivation)
                {
                    repeatExecutionScope = CreateNativeLogicalRepeatExecutionScope(repeatedActivation);
                    macroRepeatRootAttempt = TryPrepareNativeMacroRepeatRoot(
                        repeatExecutionScope,
                        slot);
                }
                if (!suppressPreemptedRepeat && !repeatRoot)
                {
                    var certifiedPress = activation is { Kind: HotbarActivationKind.PhysicalPress }
                        ? activation.Value.Press
                        : (CertifiedHotbarPress?)null;
                    BeginHotbarInput(certifiedPress, CaptureHotbarSlotIdentity(certifiedPress, slot));
                    PrepareCertifiedDirectQueueReplacement();
                    PrepareCertifiedMacroInput();
                    managedRoot = true;
                }
            }
            catch (Exception exception)
            {
                activeHotbarInput = null;
                Fault(exception, "Physical hotbar certification failed");
            }
        }

        // A still-held older input may be reasserted by an outer repeat hook.
        // It has no new physical edge and must not execute after a newer input
        // took ownership.
        if (suppressPreemptedRepeat) return 0;

        var originalCompleted = false;
        var previousRepeatExecution = activeLogicalRepeatExecution;
        if (repeatRoot)
        {
            logicalRepeatExecutionDepth++;
            activeLogicalRepeatExecution = repeatExecutionScope;
        }
        hotbarExecutionDepth++;
        try
        {
            var result = executeSlotHook.Original(thisPtr, slot);
            originalCompleted = true;
            return result;
        }
        finally
        {
            hotbarExecutionDepth--;
            if (repeatRoot)
            {
                try
                {
                    CompleteNativeMacroRepeatRoot(macroRepeatRootAttempt, originalCompleted);
                }
                finally
                {
                    activeLogicalRepeatExecution = previousRepeatExecution;
                    logicalRepeatExecutionDepth--;
                }
            }
            if (managedRoot)
            {
                if (originalCompleted) CompleteHotbarInput();
                else activeHotbarInput = null;
            }
        }
    }

    private byte ExecuteSlotByIdDetour(RaptureHotbarModule* thisPtr, uint hotbarId, uint slotId)
    {
        var rootInput = hotbarExecutionDepth == 0 && !replaying && !turboDispatching;
        var repeatRoot = false;
        var managedRoot = false;
        var suppressPreemptedRepeat = false;
        NativeLogicalRepeatExecutionScope? repeatExecutionScope = null;
        NativeMacroRepeatRootAttempt? macroRepeatRootAttempt = null;
        if (rootInput)
        {
            try
            {
                HotbarActivation? activation = null;
                if (physicalHotbarInput?.TryConsumeActivation(
                        hotbarId,
                        slotId,
                        NowMilliseconds,
                        out var observedActivation) == true)
                {
                    activation = observedActivation;
                    LogNativeRepeatActivation(observedActivation);
                }

                var slot = thisPtr == null ? null : thisPtr->GetSlotById(hotbarId, slotId);
                suppressPreemptedRepeat = activation is { SuppressedByNewerInput: true };
                repeatRoot = activation is
                {
                    Kind: HotbarActivationKind.InjectedRepeat or HotbarActivationKind.DelegatedRepeat,
                    SuppressedByNewerInput: false,
                };
                if (repeatRoot && activation is { } repeatedActivation)
                {
                    repeatExecutionScope = CreateNativeLogicalRepeatExecutionScope(repeatedActivation);
                    macroRepeatRootAttempt = TryPrepareNativeMacroRepeatRoot(
                        repeatExecutionScope,
                        slot);
                }
                if (!suppressPreemptedRepeat && !repeatRoot)
                {
                    var certifiedPress = activation is { Kind: HotbarActivationKind.PhysicalPress }
                        ? activation.Value.Press
                        : (CertifiedHotbarPress?)null;
                    BeginHotbarInput(certifiedPress, CaptureHotbarSlotIdentity(certifiedPress, slot));
                    PrepareCertifiedDirectQueueReplacement();
                    PrepareCertifiedMacroInput();
                    managedRoot = true;
                }
            }
            catch (Exception exception)
            {
                activeHotbarInput = null;
                Fault(exception, "Physical hotbar certification failed");
            }
        }

        if (suppressPreemptedRepeat) return 0;

        var originalCompleted = false;
        var previousRepeatExecution = activeLogicalRepeatExecution;
        if (repeatRoot)
        {
            logicalRepeatExecutionDepth++;
            activeLogicalRepeatExecution = repeatExecutionScope;
        }
        hotbarExecutionDepth++;
        try
        {
            var result = executeSlotByIdHook.Original(thisPtr, hotbarId, slotId);
            originalCompleted = true;
            return result;
        }
        finally
        {
            hotbarExecutionDepth--;
            if (repeatRoot)
            {
                try
                {
                    CompleteNativeMacroRepeatRoot(macroRepeatRootAttempt, originalCompleted);
                }
                finally
                {
                    activeLogicalRepeatExecution = previousRepeatExecution;
                    logicalRepeatExecutionDepth--;
                }
            }
            if (managedRoot)
            {
                if (originalCompleted) CompleteHotbarInput();
                else activeHotbarInput = null;
            }
        }
    }

    private bool UseActionDetour(
        ActionManager* thisPtr,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        Candidate? candidate = null;
        MacroQueueAttempt? macroQueueAttempt = null;
        DirectPulseAttempt? directPulseAttempt = null;
        NativeQueueDrainAttempt? nativeQueueDrainAttempt = null;
        LogicalRepeatQueueAttempt? logicalRepeatQueueAttempt = null;
        var directLogicalRepeatInput = logicalRepeatExecutionDepth > 0;
        var asynchronousLogicalRepeatInput = false;
        var staleLogicalRepeatMacroTail = false;
        NativeLogicalRepeatExecutionScope? asynchronousRepeatExecutionScope = null;
        if (!directLogicalRepeatInput
            && hotbarExecutionDepth == 0
            && !replaying
            && !turboDispatching
            && IsPotentialNativeMacroRepeatTailMode(mode))
        {
            lock (dispatchGate)
            {
                ClassifyNativeMacroRepeatTail(
                    NowMilliseconds,
                    out asynchronousLogicalRepeatInput,
                    out staleLogicalRepeatMacroTail,
                    out asynchronousRepeatExecutionScope);
            }
        }

        var logicalRepeatInput = directLogicalRepeatInput || asynchronousLogicalRepeatInput;
        var logicalRepeatExecution = directLogicalRepeatInput
            ? activeLogicalRepeatExecution
            : asynchronousRepeatExecutionScope;
        var suppressSyntheticMacroCall = staleLogicalRepeatMacroTail;
        var nativeHotbarInput = hotbarExecutionDepth > 0
            && !logicalRepeatInput
            && !replaying
            && !turboDispatching;
        var sequenceBefore = thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence;
        var nativeMode = mode == ActionManager.UseActionMode.Macro
            && CanOwnNativeMacroQueue()
                ? ActionManager.UseActionMode.None
                : mode;

        if (!suppressSyntheticMacroCall
            && !logicalRepeatInput
            && mode == ActionManager.UseActionMode.Macro)
        {
            lock (dispatchGate)
            {
                var now = NowMilliseconds;
                ReconcileSyntheticMacroExecutorQuarantine(now);
                ReconcileRetiredPhysicalMacroExecutor(now);
                suppressSyntheticMacroCall = ShouldSuppressQuarantinedSyntheticMacroCall();
            }
        }

        if (!suppressSyntheticMacroCall && logicalRepeatInput)
        {
            try
            {
                lock (dispatchGate)
                {
                    var createdAttempt = TryCreateLogicalRepeatQueueAttempt(
                        logicalRepeatExecution,
                        directLogicalRepeatInput,
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        nativeMode,
                        comboRouteId,
                        sequenceBefore);
                    if (createdAttempt is not null && logicalRepeatQueueInFlight is null)
                    {
                        logicalRepeatQueueAttempt = createdAttempt;
                        logicalRepeatQueueInFlight = createdAttempt;
                    }
                }
            }
            catch (Exception exception)
            {
                // Provenance is optional bookkeeping. Losing it must never drop
                // or replace the authoritative native/ReAction repeat call.
                logicalRepeatQueueOwnership.Clear();
                logicalRepeatQueuePending = null;
                logicalRepeatQueueInFlight = null;
                try
                {
                    Fault(exception, "Logical repeat queue provenance setup failed open");
                }
                catch
                {
                    // Original below remains authoritative even during teardown.
                }
            }
        }

        if (!suppressSyntheticMacroCall
            && turboDispatching
            && activeDirectPulseExecution is { } directPulseExecution)
        {
            lock (dispatchGate)
            {
                if (TryAuthorizeDirectPulseInvocation(
                        directPulseExecution,
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        mode,
                        comboRouteId,
                        out var directTuple,
                        out var directSafetySeed))
                {
                    directPulseAttempt = new DirectPulseAttempt(
                        directPulseExecution,
                        directSafetySeed,
                        directTuple,
                        CaptureNativeQueue(thisPtr),
                        sequenceBefore);
                }
                else
                {
                    suppressSyntheticMacroCall = true;
                }
            }
        }

        if (!suppressSyntheticMacroCall
            && turboDispatching
            && activeMacroPulseExecution is { } pulseExecution)
        {
            lock (dispatchGate)
            {
                if (mode == ActionManager.UseActionMode.Queue)
                {
                    // Queue drains remain native. Consume PulseQueue ownership
                    // only when the complete exact tuple still authorizes it.
                    if (!IsOwnedMacroTurboQueueDrain(
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        mode,
                        comboRouteId,
                        out nativeQueueDrainAttempt))
                    {
                        QuarantineSyntheticMacroExecutor(
                            pulseExecution.Runtime,
                            "unowned queue-mode call entered the synthetic same-slot chain");
                        CancelTurboUnsafe(
                            HoldRepeatCancelReason.PluginChange,
                            "Macro Turbo blocked an unowned queue-mode action call",
                            ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                        suppressSyntheticMacroCall = true;
                    }
                }
                else if (TryAuthorizeMacroPulseInvocation(
                        pulseExecution,
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        mode,
                        comboRouteId,
                        out var pulseEntry))
                {
                    macroQueueAttempt = TryCreateAuthorizedMacroQueueAttempt(
                        pulseExecution.Runtime.Generation,
                        pulseExecution.Runtime,
                        null,
                        thisPtr,
                        pulseEntry,
                        mode,
                        pulseExecution.Token);
                }
                else
                {
                    // This call is inside the exact synthetic slot call-chain.
                    // Once provenance fails it must not escape to native action
                    // execution, but the already-running physical/original macro
                    // paths remain untouched.
                    suppressSyntheticMacroCall = true;
                }
            }
        }

        if (!suppressSyntheticMacroCall
            && !logicalRepeatInput
            && !replaying
            && !turboDispatching)
        {
            lock (dispatchGate)
            {
                if (!nativeHotbarInput)
                {
                    var ownedMacroQueueDrain = IsOwnedMacroTurboQueueDrain(
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        mode,
                        comboRouteId,
                        out var macroDrainAttempt);
                    if (macroDrainAttempt is not null)
                    {
                        nativeQueueDrainAttempt = macroDrainAttempt;
                    }
                    var continuationEntry = default(MacroActionInvocation);
                    var firstInitialEntry = false;
                    var suppressSyntheticContinuation = false;
                    var observedOwnedMacroContinuation = false;
                    var ownedMacroExecution = !ownedMacroQueueDrain
                        && IsOwnedMacroTurboExecutionContinuation(
                            thisPtr,
                            actionType,
                            actionId,
                            targetId,
                            extraParam,
                            mode,
                            comboRouteId,
                            out continuationEntry,
                            out firstInitialEntry,
                            out suppressSyntheticContinuation,
                            out observedOwnedMacroContinuation);
                    suppressSyntheticMacroCall |= suppressSyntheticContinuation;
                    if (ownedMacroExecution && macroTurboRuntime is { } ownedRuntime)
                    {
                        if (firstInitialEntry)
                        {
                            TryReplaceOwnedNativeQueue(
                                thisPtr,
                                ownedRuntime.Generation,
                                "before the first certified asynchronous macro action call");
                        }

                        macroQueueAttempt = TryCreateAuthorizedMacroQueueAttempt(
                            ownedRuntime.Generation,
                            ownedRuntime,
                            null,
                            thisPtr,
                            continuationEntry,
                            mode,
                            pulseToken: null);
                    }

                    MacroQueueAttempt? retiredMacroAttempt = null;
                    var retiredMacroObserved = !ownedMacroQueueDrain
                        && !ownedMacroExecution
                        && !observedOwnedMacroContinuation
                        && !suppressSyntheticContinuation
                        && TryObserveRetiredPhysicalMacroQueueAttempt(
                            thisPtr,
                            actionType,
                            actionId,
                            targetId,
                            extraParam,
                            mode,
                            comboRouteId,
                            out retiredMacroAttempt);
                    if (retiredMacroAttempt is not null)
                    {
                        macroQueueAttempt = retiredMacroAttempt;
                    }

                    var ownedTurboQueueDrain = false;
                    if (!ownedMacroQueueDrain
                        && !ownedMacroExecution
                        && !observedOwnedMacroContinuation
                        && !retiredMacroObserved
                        && !suppressSyntheticContinuation)
                    {
                        ownedTurboQueueDrain = IsOwnedTurboActionContinuation(
                            thisPtr,
                            actionType,
                            actionId,
                            targetId,
                            extraParam,
                            mode,
                            comboRouteId,
                            out var directDrainAttempt);
                        if (directDrainAttempt is not null)
                        {
                            nativeQueueDrainAttempt = directDrainAttempt;
                        }
                    }

                    if (!ownedMacroQueueDrain
                        && !ownedMacroExecution
                        && !observedOwnedMacroContinuation
                        && !retiredMacroObserved
                        && !suppressSyntheticContinuation
                        && !ownedTurboQueueDrain)
                    {
                        Cancel(CancelReason.Replaced, "Cleared by another native action invocation");
                    }
                }

                if (nativeHotbarInput)
                {
                    try
                    {
                        var macroScope = activeHotbarInput is { SlotIdentity.CommandType: MacroHotbarSlotType }
                            ? activeHotbarInput
                            : null;
                        if (macroScope is not null)
                        {
                            macroScope.MacroLockObservedDuringExecution |= IsMacroExecutionActive();
                            macroScope.ActionInvocationCount++;
                            if (TryAuthorizeCertifiedMacroInvocation(
                                    macroScope,
                                    thisPtr,
                                    actionType,
                                    actionId,
                                    targetId,
                                    extraParam,
                                    mode,
                                    comboRouteId,
                                    out var originalEntry,
                                    out var firstOriginalEntry))
                            {
                                if (firstOriginalEntry)
                                {
                                    // This is the closest boundary before the first
                                    // native /ac call. Queue replacement is permitted
                                    // only after that exact action has passed static
                                    // eligibility, resolver, and live MOAction checks.
                                    TryReplaceOwnedNativeQueue(
                                        thisPtr,
                                        macroScope.Generation,
                                        "before the first certified macro action call");
                                }

                                macroQueueAttempt = TryCreateAuthorizedMacroQueueAttempt(
                                    macroScope.Generation,
                                    null,
                                    macroScope,
                                    thisPtr,
                                    originalEntry,
                                    mode,
                                    pulseToken: null);
                            }

                            // The physical macro execution stays completely vanilla.
                            // Its action calls never enter one-shot buffering and
                            // PulseQueue never selects a macro line or target.
                            goto CandidateCaptureComplete;
                        }

                        if (activeHotbarInput is { } inputScope)
                        {
                            inputScope.ActionInvocationCount++;
                            if (inputScope.ActionInvocationCount > 1)
                            {
                                inputScope.TurboCandidate = null;
                                inputScope.TurboDisqualified = true;
                            }
                        }

                        candidate = TryCreateCandidate(
                            thisPtr,
                            actionType,
                            actionId,
                            targetId,
                            extraParam,
                            mode,
                            comboRouteId);
                        if (candidate is null && activeHotbarInput is { } rejectedScope)
                        {
                            rejectedScope.TurboDisqualified = true;
                        }
                        if (candidate is { } ownershipCandidate
                            && nativeQueueOwnership.HasOwnership
                            && !compatibility.IsLiveMOActionUnowned(
                                ownershipCandidate.RequestedActionId,
                                ownershipCandidate.ResolvedActionId))
                        {
                            MarkCompatibilityProfileDirty("MOAction ownership changed");
                            candidate = null;
                        }

                        if (activeHotbarInput is { MaySupersedeOwnedQueue: true } supersedingScope)
                        {
                            if (TryReplaceOwnedNativeQueue(
                                    thisPtr,
                                    supersedingScope.Generation,
                                    "before the newest native action call"))
                            {
                                candidate = candidate is { } captured
                                    ? captured with
                                    {
                                        QueueAtCapture = CaptureNativeQueue(thisPtr),
                                    }
                                    : null;
                            }
                        }

                        if (candidate is { } turboCandidate
                            && activeHotbarInput is { ActionInvocationCount: 1, TurboDisqualified: false } turboScope)
                        {
                            turboScope.TurboCandidate = turboCandidate;
                        }

                    CandidateCaptureComplete:;
                    }
                    catch (Exception exception)
                    {
                        Fault(exception, "Candidate capture failed");
                    }
                }
            }
        }

        bool result;
        if (suppressSyntheticMacroCall)
        {
            if (outOptAreaTargeted != null) *outOptAreaTargeted = false;
            result = false;
            if (configuration.DetailedLogging)
            {
                log.Warning(
                    "Suppressed unauthorized synthetic Turbo call source={Source}, type={Type}, action={Action}, mode={Mode}; native physical input was not affected.",
                    activeDirectPulseExecution is null ? "macro-slot" : "direct-slot",
                    actionType,
                    actionId,
                    mode);
            }
        }
        else
        {
            // This is deliberately outside plugin-side exception recovery: every
            // authorized or physical native call invokes the original once and its
            // result/exception remains authoritative.
            var originalCompleted = false;
            try
            {
                result = useActionHook.Original(
                    thisPtr,
                    actionType,
                    actionId,
                    targetId,
                    extraParam,
                    nativeMode,
                    comboRouteId,
                    outOptAreaTargeted);
                originalCompleted = true;
            }
            finally
            {
                if (!originalCompleted && logicalRepeatQueueAttempt is { } interruptedRepeat)
                {
                    ClearLogicalRepeatQueueInFlight(interruptedRepeat);
                }

                if (!originalCompleted && nativeQueueDrainAttempt is { } interruptedDrain)
                {
                    // Never strand a non-reentrant lease if the authoritative
                    // native/outer hook throws. Preserve that original exception;
                    // best-effort finalization uses only the state visible while
                    // unwinding and never writes native queue fields.
                    try
                    {
                        lock (dispatchGate)
                        {
                            ProcessOwnedNativeQueueDrainOutcome(
                                thisPtr,
                                interruptedDrain,
                                thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence,
                                "while unwinding an exceptional native drain");
                        }
                    }
                    catch (Exception finalizeException)
                    {
                        try
                        {
                            Fault(finalizeException, "Exact native queue drain lease finalization failed while unwinding");
                        }
                        catch
                        {
                            // The original native exception remains authoritative.
                        }
                    }
                }
            }
        }

        try
        {
            var currentSequence = thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence;
            if (nativeQueueDrainAttempt is { } completedDrain)
            {
                lock (dispatchGate)
                {
                    ProcessOwnedNativeQueueDrainOutcome(
                        thisPtr,
                        completedDrain,
                        currentSequence,
                        "after the authoritative native drain call");
                }
            }

            if (macroQueueAttempt is { } attemptedMacroQueue)
            {
                lock (dispatchGate)
                {
                    ProcessMacroQueueAttempt(
                        thisPtr,
                        attemptedMacroQueue,
                        currentSequence);
                }
            }

            if (directPulseAttempt is { } attemptedDirectPulse)
            {
                lock (dispatchGate)
                {
                    ProcessDirectPulseAttempt(
                        thisPtr,
                        attemptedDirectPulse,
                        result,
                        currentSequence);
                }
            }

            if (logicalRepeatQueueAttempt is { } repeatedAttempt)
            {
                lock (dispatchGate)
                {
                    ProcessLogicalRepeatQueueAttempt(
                        thisPtr,
                        repeatedAttempt,
                        currentSequence);
                }
            }

            if (candidate is { } captured)
            {
                lock (dispatchGate)
                {
                    ProcessOriginalOutcome(thisPtr, captured, result, currentSequence, outOptAreaTargeted);
                }
            }
            else if (currentSequence != 0 && currentSequence != sequenceBefore)
            {
                // Only a synchronous sequence transition is a send-side marker. Merely
                // returning true may mean vanilla queued the action for later.
                RecordSentSequence(currentSequence, NowMilliseconds);
            }
        }
        catch (Exception exception)
        {
            Fault(exception, "Original action outcome processing failed");
        }
        finally
        {
            if (logicalRepeatQueueAttempt is { } completedRepeat)
            {
                ClearLogicalRepeatQueueInFlight(completedRepeat);
            }
        }

        return result;
    }

    private void BeginHotbarInput(
        CertifiedHotbarPress? certifiedPress,
        HotbarSlotIdentity? slotIdentity)
    {
        var replacedPending = engine.Pending is not null;
        observedHotbarInputCount++;
        if (replacedPending) replacedPendingCount++;
        Cancel(CancelReason.Replaced, "Replaced by the newest hotbar input");
        activeHotbarInput = new HotbarInputScope(
            inputGenerations.Current,
            certifiedPress,
            slotIdentity)
        {
            MacroWasLockedBeforeExecution = IsMacroExecutionActive(),
            MacroSnapshotAtPress = slotIdentity is { CommandType: MacroHotbarSlotType }
                ? CaptureSnapshot(0, 0, includeResolverTargets: true)
                : null,
            DirectSnapshotAtPress = slotIdentity is { CommandType: DirectActionHotbarSlotType } directSlot
                ? CaptureDirectSnapshotAtPress(directSlot)
                : null,
        };
        if (!activeHotbarInput.MacroWasLockedBeforeExecution)
        {
            // Observe the unlocked boundary before any newer root can start a
            // different native Macro and reuse MacroLocked (ABA).
            ReconcileRetiredPhysicalMacroExecutor(NowMilliseconds);
            ReconcileNativeMacroRepeatTail(NowMilliseconds, observedUnlockedBoundary: true);
        }

        TryClearSyntheticMacroQuarantineForCertifiedRoot(activeHotbarInput);
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Observed hotbar input generation={Generation}, replacedPending={ReplacedPending}.",
                inputGenerations.Current,
                replacedPending);
        }
    }

    private void PrepareCertifiedDirectQueueReplacement()
    {
        lock (dispatchGate)
        {
            if (activeHotbarInput is not { } scope
                || scope.CertifiedPress is not { } press
                || scope.SlotIdentity is not { CommandType: DirectActionHotbarSlotType } slotIdentity
                || scope.DirectSnapshotAtPress is not { } snapshot
                || !configuration.Enabled
                || configuration.DryRun
                || activeConflicts.Count > 0
                || compatibilityQuarantineFrames > 0
                || !inputGenerations.IsCurrent(scope.Generation)
                || Volatile.Read(ref latestCertifiedPressId) != press.PressId
                || !TryReadCurrentSlotIdentity(press, out var currentIdentity)
                || currentIdentity != slotIdentity
                || !compatibility.IsLiveReActionProfileCurrent())
            {
                return;
            }

            // Queue takeover is input priority, not permission to buffer or
            // repeat. In particular, a physical Purify/Guard press must be able
            // to remove an older exact PulseQueue-owned Viper queue even when
            // Stunned/BeingMoved is already visible before the next framework
            // safety tick. TryStartTurbo and every dispatch path still require
            // this snapshot to be safe; the new vanilla call is not claimed in
            // an unsafe context.
            ArmCertifiedOwnedQueueReplacement(
                scope,
                "before the newest certified direct hotbar root");
        }
    }

    private void PrepareCertifiedMacroInput()
    {
        lock (dispatchGate)
        {
            if (activeHotbarInput is not { } scope
                || scope.CertifiedPress is null
                || scope.SlotIdentity is not { CommandType: MacroHotbarSlotType }
                || !configuration.Enabled
                || configuration.DryRun
                || !inputGenerations.IsCurrent(scope.Generation))
            {
                return;
            }

            // Input priority is independent of macro contents. The player pressed
            // this exact native hotbar control, so it may supersede an older exact
            // PulseQueue-owned queue before FFXIV evaluates the authored macro.
            // PulseQueue deliberately does not parse, select, budget, or suppress
            // macro lines here; the native macro engine remains the sole executor.
            ArmCertifiedOwnedQueueReplacement(
                scope,
                "before the newest certified native Macro hotbar root");
        }
    }

    private void ArmCertifiedOwnedQueueReplacement(
        HotbarInputScope scope,
        string phase)
    {
        scope.MaySupersedeOwnedQueue = true;
        // Keep the certified generation as a tombstone in case an outer hook
        // or an asynchronous older vanilla Macro creates/restores its exact
        // queue after the current ExecuteSlot chain returns.
        latestCertifiedQueueReplacementGeneration = scope.Generation;
        var actionManager = ActionManager.Instance();
        if (actionManager != null)
        {
            TryReplaceOwnedNativeQueue(actionManager, scope.Generation, phase);
        }
    }

    private void OnCertifiedPhysicalPress(CertifiedHotbarPress press)
    {
        try
        {
            lock (dispatchGate)
            {
                if (disposed) return;
                Volatile.Write(ref latestCertifiedPressId, press.PressId);
                if (engine.Pending is not null) replacedPendingCount++;
                MarkLogicalRepeatInFlightSupersededBy(press);
                var actionManager = ActionManager.Instance();
                if (!configuration.DryRun && actionManager != null)
                {
                    ResolveLogicalRepeatQueuePending(
                        actionManager,
                        NowMilliseconds,
                        "immediately before the newest physical logical hotbar edge");
                }

                Cancel(
                    CancelReason.Replaced,
                    $"Physical hotbar press {press.PressId} preempted every older buffered or Turbo owner");

                // Input priority is decided at the real logical press edge, not
                // after action/slot classification. This lets Guard, Purify,
                // Recuperate, items, macros, combo slots, and other hotbar
                // commands immediately replace one exact older PulseQueue-owned
                // native queue. Foreign queues are never touched.
                latestCertifiedQueueReplacementGeneration = inputGenerations.Current;
                latestLogicalRepeatQueueReplacementGeneration = inputGenerations.Current;
                if (configuration.DryRun)
                {
                    AbandonOwnedQueueProvenanceForDetectOnly();
                }
                else if (CanReplaceOwnedQueuesForNewestInput() && actionManager != null)
                {
                    TryReplaceLogicalRepeatNativeQueue(
                        actionManager,
                        latestLogicalRepeatQueueReplacementGeneration,
                        "at the newest physical logical hotbar edge");
                    TryReplaceOwnedNativeQueue(
                        actionManager,
                        latestCertifiedQueueReplacementGeneration,
                        "at the newest physical logical hotbar edge");
                }

                if (!IsNativeRepeatOwnershipAllowedNow())
                {
                    // The physical tap still preempted older exact ownership and
                    // remains fully vanilla, but a hold begun during a persistent
                    // terminal state may not resume Turbo when that state clears.
                    physicalHotbarInput?.CancelAndRequireRelease();
                    lastEvent = $"Physical hotbar press {press.PressId} passed through but must release before Turbo can resume";
                }

                if (configuration.DetailedLogging)
                {
                    log.Information(
                        "Native hotbar edge press={PressId}, input={InputId}, hotbar={Hotbar}, slot={Slot}; every older PulseQueue intent was preempted.",
                        press.PressId,
                        press.Binding.InputId,
                        press.Binding.HotbarId + 1,
                        press.Binding.SlotId + 1);
                }
            }
        }
        catch (Exception exception)
        {
            try
            {
                Fault(exception, "Physical press preemption failed");
            }
            catch
            {
                // Preserve the original preemption failure below.
            }

            // The input source catches this and discards the uncertified pending
            // edge, so the native pressed result still remains authoritative.
            throw;
        }
    }

    private NativeHotbarRepeatSettings GetNativeHotbarRepeatSettings()
    {
        // This callback runs at the beginning of the native binding scan, before
        // any logical hotbar edge is certified. Processing terminal transitions
        // here prevents a stun/target/zone change between framework ticks from
        // being erased by the next press.
        ObserveNativeInputContextTransitions();

        var liveReActionProfileCurrent = !reActionLoaded
            || reActionAudited && compatibility.IsLiveReActionProfileCurrent();
        var pluginOperational = configuration.Enabled
            && !faulted
            && !disposed;
        var featureEnabled = pluginOperational
            && configuration.TurboEnabled
            && !configuration.DryRun
            && activeConflicts.Count == 0
            && compatibilityQuarantineFrames == 0
            && IsNativeRepeatOwnershipAllowedNow()
            && liveReActionProfileCurrent
            && (!reActionLoaded || reActionAudited)
            && !reActionSmartActionTransformActive;
        var repeatEnabled = featureEnabled
            && (configuration.TurboOutOfCombat || condition[ConditionFlag.InCombat]);
        var reActionRepeatActive = pluginOperational
            && !configuration.DryRun
            && reActionLoaded
            && (!reActionAudited
                || !liveReActionProfileCurrent
                || Volatile.Read(ref reActionTurboHotbarsEnabled)
                    && (condition[ConditionFlag.InCombat]
                        || Volatile.Read(ref reActionTurboHotbarsOutOfCombatEnabled)));

        return new NativeHotbarRepeatSettings(
            RepeatEnabled: repeatEnabled,
            ExternalRepeatOwnerActive: reActionRepeatActive,
            InitialDelayMilliseconds: configuration.TurboInitialDelayMs,
            RepeatIntervalMilliseconds: configuration.TurboRepeatIntervalMs);
    }

    private bool IsNativeRepeatOwnershipAllowedNow()
    {
        var local = objectTable.LocalPlayer;
        return configuration.Enabled
            && !configuration.DryRun
            && !faulted
            && !disposed
            && activeConflicts.Count == 0
            && compatibilityQuarantineFrames == 0
            && clientState.IsLoggedIn
            && local is { IsDead: false }
            && !condition[ConditionFlag.Unconscious]
            && !condition[ConditionFlag.Mounted]
            && !IsStunned(local)
            && !condition[ConditionFlag.BeingMoved]
            && !IsBetweenAreas;
    }

    private void LogNativeRepeatActivation(HotbarActivation activation)
    {
        if (!configuration.DetailedLogging
            || activation.Kind == HotbarActivationKind.PhysicalPress)
        {
            return;
        }

        log.Debug(
            "Native hotbar repeat kind={Kind}, input={InputId}, hotbar={Hotbar}, slot={Slot}, suppressedByNewer={Suppressed}.",
            activation.Kind,
            activation.Binding.InputId,
            activation.Binding.HotbarId + 1,
            activation.Binding.SlotId + 1,
            activation.SuppressedByNewerInput);
    }

    private NativeLogicalRepeatExecutionScope CreateNativeLogicalRepeatExecutionScope(
        HotbarActivation activation)
    {
        var actionManager = ActionManager.Instance();
        return new NativeLogicalRepeatExecutionScope(
            inputGenerations.Current,
            activation.Press.PressId,
            activation.Binding,
            activation.ObservedAtMilliseconds,
            CaptureNativeQueue(actionManager),
            actionManager == null ? (ushort)0 : actionManager->LastUsedActionSequence,
            reActionLoaded
                && reActionAudited
                && reActionMacroQueueEnabled
                && compatibility.IsLiveReActionProfileCurrent());
    }

    private NativeMacroRepeatRootAttempt? TryPrepareNativeMacroRepeatRoot(
        NativeLogicalRepeatExecutionScope? execution,
        RaptureHotbarModule.HotbarSlot* slot)
    {
        if (execution is null
            || slot == null
            || (uint)slot->CommandType != MacroHotbarSlotType
            || !configuration.Enabled
            || configuration.DryRun
            || faulted
            || disposed)
        {
            return null;
        }

        lock (dispatchGate)
        {
            if (IsMacroExecutionActive()) return null;

            var now = NowMilliseconds;
            // MacroLocked=false immediately before this exact root is an observed
            // unlocked boundary. It closes any older tail before a new executor
            // can reuse the global MacroLocked bit (ABA).
            ReconcileNativeMacroRepeatTail(now, observedUnlockedBoundary: true);
            return new NativeMacroRepeatRootAttempt(
                execution,
                now,
                SaturatingAdd(now, MaximumMacroCaptureMilliseconds));
        }
    }

    private void CompleteNativeMacroRepeatRoot(
        NativeMacroRepeatRootAttempt? attempt,
        bool originalCompleted)
    {
        if (attempt is null) return;

        try
        {
            lock (dispatchGate)
            {
                if (!originalCompleted
                    || !configuration.Enabled
                    || configuration.DryRun
                    || faulted
                    || disposed
                    || !IsMacroExecutionActive()
                    || nativeMacroRepeatTail is not null)
                {
                    return;
                }

                // The token is committed only after the exact repeated slot returned
                // with MacroLocked active. It remains generation-bound even if a
                // newer physical press arrived re-entrantly while Original ran.
                nativeMacroRepeatTail = new NativeMacroRepeatTail(
                    attempt.Execution,
                    attempt.StartedAtMilliseconds,
                    attempt.DiagnosticDeadlineMilliseconds)
                {
                    MacroLockObserved = true,
                };
            }
        }
        catch (Exception exception)
        {
            nativeMacroRepeatTail = null;
            try
            {
                Fault(exception, "Native Macro repeat tail completion failed open");
            }
            catch
            {
                // ExecuteSlot's already-completed native result remains authoritative.
            }
        }
    }

    private bool IsPotentialNativeMacroRepeatTailMode(
        ActionManager.UseActionMode mode) =>
        mode == ActionManager.UseActionMode.Macro
        || (uint)mode == 100
        || mode == ActionManager.UseActionMode.None
            && nativeMacroRepeatTail?.Execution.ReActionMacroQueueAtRoot == true;

    private void ClassifyNativeMacroRepeatTail(
        long now,
        out bool currentTail,
        out bool staleTail,
        out NativeLogicalRepeatExecutionScope? execution)
    {
        currentTail = false;
        staleTail = false;
        execution = null;
        ReconcileNativeMacroRepeatTail(now, observedUnlockedBoundary: false);
        if (nativeMacroRepeatTail is not { } tail) return;

        // None is a possible Macro tail only when audited ReAction Macro Queue
        // owns the conversion. Queue-mode drains and ordinary direct roots never
        // enter this classifier.
        execution = tail.Execution;
        var isCurrent = inputGenerations.IsCurrent(tail.Execution.Generation);
        if (isCurrent)
        {
            currentTail = true;
        }
        else
        {
            staleTail = true;
        }
    }

    private void ReconcileNativeMacroRepeatTail(
        long now,
        bool observedUnlockedBoundary)
    {
        if (nativeMacroRepeatTail is not { } tail) return;

        var macroLocked = IsMacroExecutionActive();
        if (macroLocked)
        {
            tail.MacroLockObserved = true;
            if (now >= tail.DiagnosticDeadlineMilliseconds && !tail.TimeoutReported)
            {
                tail.TimeoutReported = true;
                if (configuration.DetailedLogging)
                {
                    log.Warning(
                        "Native repeat Macro tail exceeded {Timeout} ms; provenance remains fail-closed until MacroLocked is observed false.",
                        MaximumMacroCaptureMilliseconds);
                }
            }

            return;
        }

        if (observedUnlockedBoundary
            || tail.MacroLockObserved
            || now - tail.StartedAtMilliseconds >= NativeMacroRepeatStartGraceMilliseconds)
        {
            nativeMacroRepeatTail = null;
        }
    }

    private bool CanOwnNativeMacroQueue() =>
        configuration.Enabled
        && configuration.TurboMacrosEnabled
        && !configuration.DryRun
        && !faulted
        && !disposed
        && activeConflicts.Count == 0
        && compatibilityQuarantineFrames == 0
        && (!reActionLoaded
            || reActionAudited
                && compatibility.IsLiveReActionProfileCurrent()
                && !reActionMacroQueueEnabled
                && !reActionSmartActionTransformActive);

    private LogicalRepeatQueueAttempt? TryCreateLogicalRepeatQueueAttempt(
        NativeLogicalRepeatExecutionScope? execution,
        bool allowDeferredOuterHookCorrelation,
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        ushort sequenceBefore)
    {
        if (execution is null
            || actionManager == null
            || actionId == 0
            || !configuration.Enabled
            || configuration.DryRun
            || faulted
            || disposed)
        {
            return null;
        }

        var resolvedActionId = actionManager->GetAdjustedActionId(actionId);
        if (resolvedActionId == 0) resolvedActionId = actionId;
        var expectedMode = ((uint)mode == 100
            || execution.ReActionMacroQueueAtRoot
                && mode is ActionManager.UseActionMode.Macro or ActionManager.UseActionMode.None)
                ? ActionManager.UseActionMode.None
                : mode;
        var expected = new ExactActionTuple(
            (uint)actionType,
            actionId,
            resolvedActionId,
            targetId,
            extraParam,
            (uint)expectedMode,
            comboRouteId);
        if (!expected.IsValid) return null;

        return new LogicalRepeatQueueAttempt(
            execution,
            CaptureNativeQueue(actionManager),
            expected,
            sequenceBefore,
            NowMilliseconds,
            allowDeferredOuterHookCorrelation);
    }

    private void ProcessLogicalRepeatQueueAttempt(
        ActionManager* actionManager,
        LogicalRepeatQueueAttempt attempt,
        ushort currentSequence)
    {
        if (actionManager == null
            || configuration.DryRun
            || !configuration.Enabled
            || faulted
            || disposed)
        {
            logicalRepeatQueueOwnership.Clear();
            logicalRepeatQueuePending = null;
            return;
        }

        var queueAfter = CaptureNativeQueue(actionManager);
        var sequenceUnchanged = currentSequence == attempt.SequenceBefore;
        var generationCurrent = inputGenerations.IsCurrent(attempt.Execution.Generation);
        var generationAttributable = generationCurrent
            || attempt.SupersededByTerminalCancellation
            || attempt.SupersededByProvablyDifferentPhysicalInput
                && !reActionSmartActionTransformActive;
        var observedNewQueue = generationAttributable
            && sequenceUnchanged
            && queueAfter.IsQueued
            && !queueAfter.Equals(attempt.QueueBefore)
            && queueAfter.Matches(attempt.Expected);
        if (observedNewQueue)
        {
            if (logicalRepeatQueueOwnership.TryClaimFromObservedDelta(
                    attempt.Execution.Generation,
                    currentSequence,
                    attempt.QueueBefore,
                    queueAfter,
                    attempt.Expected))
            {
                logicalRepeatQueueClaimCount++;
                logicalRepeatQueuePending = null;
            }
        }
        else if (generationAttributable
            && sequenceUnchanged
            && attempt.AllowDeferredOuterHookCorrelation
            && attempt.Execution.SequenceAtRoot == attempt.SequenceBefore
            && !attempt.Execution.QueueAtRoot.Matches(attempt.Expected)
            && !attempt.QueueBefore.Matches(attempt.Expected)
            && !attempt.QueueBefore.IsQueued
            && !queueAfter.IsQueued)
        {
            // One narrowly bounded compatibility window: an outer hook may create
            // or restore the exact queue only after this inner detour returns.
            // Resolution is allowed before the next input or at one stable frame.
            logicalRepeatQueuePending = new LogicalRepeatQueuePending(
                attempt.Execution,
                attempt.QueueBefore,
                attempt.Expected,
                currentSequence,
                SaturatingAdd(NowMilliseconds, LogicalRepeatQueueCorrelationMilliseconds))
            {
                SupersededByProvablyDifferentPhysicalInput =
                    attempt.SupersededByProvablyDifferentPhysicalInput,
                SupersededByTerminalCancellation =
                    attempt.SupersededByTerminalCancellation,
            };
        }
        else
        {
            logicalRepeatQueuePending = null;
        }

        if (latestLogicalRepeatQueueReplacementGeneration > attempt.Execution.Generation)
        {
            TryReplaceLogicalRepeatNativeQueue(
                actionManager,
                latestLogicalRepeatQueueReplacementGeneration,
                "after an older logical repeat returned beneath a newer input");
        }
    }

    private void MarkLogicalRepeatInFlightSupersededBy(CertifiedHotbarPress press)
    {
        if (logicalRepeatQueueInFlight is null
            && logicalRepeatQueuePending is null)
        {
            return;
        }

        if (!TryReadCurrentSlotIdentity(press, out var newerSlot)
            || newerSlot.CommandType != DirectActionHotbarSlotType
            || newerSlot.CommandId == 0)
        {
            return;
        }

        var actionManager = ActionManager.Instance();
        var newerResolvedId = actionManager == null
            ? newerSlot.CommandId
            : actionManager->GetAdjustedActionId(newerSlot.CommandId);
        if (newerResolvedId == 0) newerResolvedId = newerSlot.CommandId;

        if (logicalRepeatQueueInFlight is { } inFlight
            && inFlight.Execution.PressId != press.PressId)
        {
            inFlight.SupersededByProvablyDifferentPhysicalInput = IsProvablyDifferent(
                inFlight.Expected,
                newerSlot.CommandId,
                newerResolvedId);
        }

        if (logicalRepeatQueuePending is { } pending
            && pending.Execution.PressId != press.PressId)
        {
            pending.SupersededByProvablyDifferentPhysicalInput = IsProvablyDifferent(
                pending.Expected,
                newerSlot.CommandId,
                newerResolvedId);
        }
    }

    private static bool IsProvablyDifferent(
        ExactActionTuple older,
        uint newerRequestedId,
        uint newerResolvedId) =>
        newerRequestedId != older.RequestedActionId
        && newerRequestedId != older.ResolvedActionId
        && newerResolvedId != older.RequestedActionId
        && newerResolvedId != older.ResolvedActionId;

    private void ClearLogicalRepeatQueueInFlight(LogicalRepeatQueueAttempt attempt)
    {
        lock (dispatchGate)
        {
            if (ReferenceEquals(logicalRepeatQueueInFlight, attempt))
            {
                logicalRepeatQueueInFlight = null;
            }
        }
    }

    private void ResolveLogicalRepeatQueuePending(
        ActionManager* actionManager,
        long now,
        string phase,
        bool stableBoundary = false)
    {
        if (logicalRepeatQueuePending is not { } pending || actionManager == null) return;
        var generationCurrent = inputGenerations.IsCurrent(pending.Execution.Generation);
        var generationAttributable = generationCurrent
            || pending.SupersededByTerminalCancellation
            || pending.SupersededByProvablyDifferentPhysicalInput
                && !reActionSmartActionTransformActive;
        if (!generationAttributable)
        {
            logicalRepeatQueuePending = null;
            return;
        }

        var current = CaptureNativeQueue(actionManager);
        var currentSequence = actionManager->LastUsedActionSequence;
        if (currentSequence != pending.SequenceMarker
            || now > pending.ExpiresAtMilliseconds)
        {
            logicalRepeatQueuePending = null;
            return;
        }

        if (current.IsQueued)
        {
            if (current.Matches(pending.Expected)
                && !pending.QueueBefore.Matches(pending.Expected)
                && logicalRepeatQueueOwnership.TryClaimFromObservedDelta(
                    pending.Execution.Generation,
                    currentSequence,
                    pending.QueueBefore,
                    current,
                    pending.Expected))
            {
                logicalRepeatQueueClaimCount++;
            }

            logicalRepeatQueuePending = null;
        }
        else if (stableBoundary)
        {
            // The outer-hook correlation window is exactly one stable boundary;
            // an empty queue here is not causally attributable to the old call.
            logicalRepeatQueuePending = null;
        }

        if (latestLogicalRepeatQueueReplacementGeneration > pending.Execution.Generation)
        {
            TryReplaceLogicalRepeatNativeQueue(
                actionManager,
                latestLogicalRepeatQueueReplacementGeneration,
                phase);
        }
    }

    private bool TryReplaceLogicalRepeatNativeQueue(
        ActionManager* actionManager,
        long replacingGeneration,
        string phase)
    {
        if (actionManager == null || configuration.DryRun) return false;
        var current = CaptureNativeQueue(actionManager);
        if (!current.IsQueued) return false;
        if (!logicalRepeatQueueOwnership.TryTakeForNewerInput(
                replacingGeneration,
                actionManager->LastUsedActionSequence,
                current,
                out var replaced))
        {
            return false;
        }

        actionManager->ActionQueued = false;
        logicalRepeatQueueReplacementCount++;
        lastEvent = $"Newest input replaced repeat-owned native queue action {replaced.ActionId}";
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Replaced repeat-owned native queue action={Action}, generation={Generation}, phase={Phase}.",
                replaced.ActionId,
                replacingGeneration,
                phase);
        }

        return true;
    }

    private bool CanReplaceOwnedQueuesForNewestInput() =>
        configuration.Enabled
        && !configuration.DryRun
        && !faulted
        && !disposed;

    private void AbandonOwnedQueueProvenanceForDetectOnly()
    {
        nativeQueueOwnership.Clear();
        logicalRepeatQueueOwnership.Clear();
        ownedNativeQueueSafetyContext = null;
        logicalRepeatQueuePending = null;
        logicalRepeatQueueInFlight = null;
        ownedNativeQueueSafetyClearPending = false;
        ownedNativeQueueSafetyClearThroughGeneration = 0;
        latestCertifiedQueueReplacementGeneration = 0;
        latestLogicalRepeatQueueReplacementGeneration = 0;
    }

    private static bool IsMacroTargetProven(Candidate candidate) =>
        !candidate.IncludeResolverTargets
        || candidate.TargetId is not (0 or InvalidObjectId);

    private bool TryCreateMacroTranscriptEntry(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroActionInvocation entry)
    {
        entry = default;
        if (actionManager == null
            || mode != ActionManager.UseActionMode.Macro
            || actionType is not (ActionType.Action or ActionType.PvPAction)
            || actionId == 0)
        {
            return false;
        }

        var resolvedActionId = actionManager->GetAdjustedActionId(actionId);
        if (resolvedActionId == 0
            || excludedIntegrationActionIds.Contains(actionId)
            || excludedIntegrationActionIds.Contains(resolvedActionId)
            || !compatibility.IsLiveMOActionUnowned(actionId, resolvedActionId)
            || !TryGetEligibleActionProfile(
                actionType,
                resolvedActionId,
                targetId,
                out var includeResolverTargets))
        {
            return false;
        }

        var snapshot = CaptureSnapshot(targetId, resolvedActionId, includeResolverTargets);
        var explicitTargetAddress = targetId is 0 or InvalidObjectId
            || targetId == snapshot.LocalGameObjectId
            ? nint.Zero
            : FindTargetAddress(targetId);
        if (!IsSafeSnapshot(snapshot)
            || targetId is not (0 or InvalidObjectId)
                && targetId != snapshot.LocalGameObjectId
                && explicitTargetAddress == nint.Zero)
        {
            return false;
        }

        entry = new MacroActionInvocation(
            (uint)actionType,
            actionId,
            resolvedActionId,
            targetId,
            extraParam,
            comboRouteId,
            snapshot,
            includeResolverTargets,
            explicitTargetAddress,
            includeResolverTargets ? snapshot.TargetFingerprint : 0);
        return entry.IsValid;
    }

    private bool TryAuthorizeCertifiedMacroInvocation(
        HotbarInputScope scope,
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroActionInvocation entry,
        out bool firstEntry)
    {
        entry = default;
        firstEntry = false;
        if (scope.MacroProfileAtPress is null
            || scope.MacroExecutionBudget is not { } budget
            || scope.MacroProvenanceDisqualified)
        {
            return false;
        }

        if (!TryCreateMacroTranscriptEntry(
                actionManager,
                actionType,
                actionId,
                targetId,
                extraParam,
                mode,
                comboRouteId,
                out entry))
        {
            scope.MacroProvenanceDisqualified = true;
            scope.MacroProvenanceFailure = "original macro emitted an ineligible, non-Macro-mode, or MOAction-owned action";
            return false;
        }

        firstEntry = budget.ObservedActionCalls == 0;
        var observationResult = budget.ObserveAction();
        if (observationResult == MacroTurboActionObservationResult.Allowed) return true;

        scope.MacroProvenanceDisqualified = true;
        scope.MacroProvenanceFailure =
            $"original macro exceeded its certified action-call budget ({observationResult}, observed={budget.ObservedActionCalls}, max={budget.MaxActionCalls})";
        firstEntry = false;
        return false;
    }

    private bool TryAuthorizeRuntimeMacroInvocation(
        MacroTurboRuntime runtime,
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroActionInvocation entry,
        out bool firstInitialEntry)
    {
        entry = default;
        firstInitialEntry = false;
        if (!TryCreateMacroTranscriptEntry(
                actionManager,
                actionType,
                actionId,
                targetId,
                extraParam,
                mode,
                comboRouteId,
                out entry))
        {
            if (runtime.ActiveExecutionEpoch > 0)
            {
                QuarantineSyntheticMacroExecutor(
                    runtime,
                    "ineligible, non-Macro-mode, or MOAction-owned action call");
            }

            CancelTurboUnsafe(
                HoldRepeatCancelReason.PluginChange,
                "Macro Turbo observed an ineligible, non-Macro-mode, or MOAction-owned action call",
                ownedQueuePolicy: runtime.InitialMacroLockCompleted
                    ? OwnedQueueCancelPolicy.ExactClear
                    : OwnedQueueCancelPolicy.Preserve);
            return false;
        }

        MacroTurboExecutionBudget? budget;
        if (!runtime.InitialMacroLockCompleted)
        {
            if (runtime.InitialExecutionBudget is not { } initialBudget)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.Fault,
                    "Macro Turbo initial execution budget owner was missing");
                return false;
            }

            budget = initialBudget;
            firstInitialEntry = budget.ObservedActionCalls == 0;
        }
        else
        {
            if (runtime.ActiveExecutionBudget is not { } activeBudget
                || runtime.ActiveExecutionEpoch <= 0)
            {
                QuarantineSyntheticMacroExecutor(runtime, "missing bounded execution budget");
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.Fault,
                    "Macro Turbo action call had no active bounded execution epoch",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return false;
            }

            budget = activeBudget;

            // Once one exact native outcome has been accepted, the remaining
            // authored fallback lines are a normal macro tail. Contain them
            // before Original without turning that expected stop into a fault.
            if (budget.AcceptedOutcomeCount > 0) return false;
        }

        var observationResult = budget.ObserveAction();
        if (observationResult == MacroTurboActionObservationResult.Allowed) return true;

        if (runtime.InitialMacroLockCompleted)
        {
            QuarantineSyntheticMacroExecutor(
                runtime,
                $"bounded macro execution rejected action call ({observationResult})");
        }

        CancelTurboUnsafe(
            HoldRepeatCancelReason.ResolvedActionChange,
            $"Macro Turbo action-call budget rejected call ({observationResult}, observed={budget.ObservedActionCalls}, max={budget.MaxActionCalls})",
            ownedQueuePolicy: runtime.InitialMacroLockCompleted
                ? OwnedQueueCancelPolicy.ExactClear
                : OwnedQueueCancelPolicy.Preserve);
        return false;
    }

    private bool TryAuthorizeMacroPulseInvocation(
        MacroPulseExecutionScope pulseExecution,
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroActionInvocation entry)
    {
        entry = default;
        var runtime = pulseExecution.Runtime;
        if (!ReferenceEquals(macroTurboRuntime, runtime)
            || !turboEngine.IsTokenCurrent(pulseExecution.Token)
            || runtime.ActiveExecutionEpoch != pulseExecution.ExecutionEpoch
            || runtime.ActiveExecutionBudget is null)
        {
            QuarantineSyntheticMacroExecutor(
                runtime,
                "stale synchronous call-chain provenance");
            CancelTurboUnsafe(
                HoldRepeatCancelReason.Fault,
                "Macro Turbo synchronous call-chain provenance was stale",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        if (!IsTurboSafetySafe(ObserveMacroTurbo(runtime, checkMacroHash: true).Safety))
        {
            QuarantineSyntheticMacroExecutor(
                runtime,
                "live macro safety changed inside the same-slot call chain");
            CancelTurboUnsafe(
                HoldRepeatCancelReason.PluginChange,
                "Macro Turbo live slot, content, target/resolver, compatibility, or physical hold changed",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        return TryAuthorizeRuntimeMacroInvocation(
            runtime,
            actionManager,
            actionType,
            actionId,
            targetId,
            extraParam,
            mode,
            comboRouteId,
            out entry,
            out _);
    }

    private MacroQueueAttempt? TryCreateAuthorizedMacroQueueAttempt(
        long generation,
        MacroTurboRuntime? runtime,
        HotbarInputScope? inputScope,
        ActionManager* actionManager,
        MacroActionInvocation entry,
        ActionManager.UseActionMode mode,
        HoldRepeatPulseToken? pulseToken)
    {
        if (actionManager == null
            || generation <= 0
            || !inputGenerations.IsCurrent(generation)
            || mode != ActionManager.UseActionMode.Macro
            || !entry.IsValid)
        {
            return null;
        }

        if (runtime is not null)
        {
            if (!ReferenceEquals(macroTurboRuntime, runtime)
                || !turboEngine.Snapshot.HasActiveHold
                || Volatile.Read(ref latestCertifiedPressId) != runtime.Press.PressId
                || physicalHotbarInput?.IsStillHeld(runtime.Press) != true)
            {
                return null;
            }

            if (pulseToken is { } token)
            {
                if (!turboEngine.IsTokenCurrent(token)
                    || activeMacroPulseExecution is not { } pulseExecution
                    || !ReferenceEquals(pulseExecution.Runtime, runtime)
                    || pulseExecution.Token != token
                    || pulseExecution.ExecutionEpoch != runtime.ActiveExecutionEpoch)
                {
                    return null;
                }
            }
            else if (!runtime.OwnsMacroExecutor || !IsMacroExecutionActive())
            {
                return null;
            }
        }
        else if (inputScope is null
            || !ReferenceEquals(activeHotbarInput, inputScope)
            || inputScope.MacroProfileAtPress is null)
        {
            return null;
        }

        return new MacroQueueAttempt(
            generation,
            runtime,
            inputScope,
            null,
            new OwnedNativeQueueSafetySeed(
                runtime?.Snapshot ?? inputScope!.MacroSnapshotAtPress!,
                entry.ActionSnapshot,
                entry.IncludeResolverTargets,
                entry.ExplicitTargetAddress),
            new ExactActionTuple(
                entry.ActionType,
                entry.RequestedActionId,
                entry.ResolvedActionId,
                entry.TargetId,
                entry.ExtraParam,
                (uint)mode,
                entry.RouteId),
            CaptureNativeQueue(actionManager),
            actionManager->LastUsedActionSequence,
            pulseToken ?? runtime?.ActiveExecutionToken,
            runtime?.ActiveExecutionEpoch ?? 0,
            NowMilliseconds);
    }

    private bool TryObserveRetiredPhysicalMacroQueueAttempt(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroQueueAttempt? attempt)
    {
        attempt = null;
        ReconcileRetiredPhysicalMacroExecutor(NowMilliseconds);
        var retired = retiredPhysicalMacroExecutor;
        if (retired is null
            || actionManager == null
            || !IsMacroExecutionActive()
            || mode != ActionManager.UseActionMode.Macro)
        {
            return false;
        }

        // The call belongs to a still-locked executor candidate, but observation
        // must fail closed. Any structural, budget, content, or eligibility
        // mismatch retires the observer and the vanilla call remains untouched.
        if (actionType is not (ActionType.Action or ActionType.PvPAction)
            || actionId == 0
            || retired.ObservedActionCalls >= retired.MaximumActionCalls)
        {
            retiredPhysicalMacroExecutor = null;
            return false;
        }

        var observed = retired.ObservedActionCalls + 1;
        retired = retired with { ObservedActionCalls = observed };
        retiredPhysicalMacroExecutor = retired;
        if (!TryReadSafeMacroProfile(retired.SlotIdentity, out var currentProfile, out _)
            || currentProfile.ContentFingerprint != retired.ContentFingerprint
            || currentProfile.ActionCount != retired.MaximumActionCalls)
        {
            retiredPhysicalMacroExecutor = null;
            return false;
        }

        var resolvedActionId = actionManager->GetAdjustedActionId(actionId);
        if (!compatibility.IsLiveReActionProfileCurrent())
        {
            retiredPhysicalMacroExecutor = null;
            MarkCompatibilityProfileDirty("ReAction profile changed during a retired physical Macro tail");
            return true;
        }

        if (resolvedActionId == 0
            || excludedIntegrationActionIds.Contains(actionId)
            || excludedIntegrationActionIds.Contains(resolvedActionId))
        {
            return true;
        }

        if (!compatibility.IsLiveMOActionUnowned(actionId, resolvedActionId))
        {
            retiredPhysicalMacroExecutor = null;
            MarkCompatibilityProfileDirty("MOAction ownership changed during a retired physical Macro tail");
            return true;
        }

        if (!TryGetEligibleActionProfile(
                actionType,
                resolvedActionId,
                targetId,
                out var includeResolverTargets))
        {
            // MacroLocked proves this is still the old physical executor, but
            // the call is outside PulseQueue's queue-ownership eligibility.
            // Leave it entirely vanilla and, crucially, do not let the old tail
            // cancel a newer Guard/heal input as an unrelated invocation.
            return true;
        }

        var actionSnapshot = CaptureSnapshot(
            targetId,
            resolvedActionId,
            includeResolverTargets);
        var explicitTargetAddress = targetId is 0 or InvalidObjectId
            || targetId == actionSnapshot.LocalGameObjectId
            ? nint.Zero
            : FindTargetAddress(targetId);
        if (targetId is not (0 or InvalidObjectId)
            && targetId != actionSnapshot.LocalGameObjectId
            && explicitTargetAddress == nint.Zero)
        {
            // Still attributable to the old bounded MacroLocked executor, but
            // an unresolved target is not eligible for ownership capture.
            return true;
        }

        // This observer never authorizes, suppresses, or rewrites the player's
        // already-running vanilla Macro. It only captures the exact native
        // before/after tuple so a newer-input or terminal tombstone can clear a
        // queue produced after the original Turbo runtime was retired.
        attempt = new MacroQueueAttempt(
            retired.Generation,
            null,
            null,
            retired,
            new OwnedNativeQueueSafetySeed(
                retired.Snapshot,
                actionSnapshot,
                includeResolverTargets,
                explicitTargetAddress),
            new ExactActionTuple(
                (uint)actionType,
                actionId,
                resolvedActionId,
                targetId,
                extraParam,
                (uint)mode,
                comboRouteId),
            CaptureNativeQueue(actionManager),
            actionManager->LastUsedActionSequence,
            null,
            0,
            NowMilliseconds);
        return true;
    }

    private void ProcessMacroQueueAttempt(
        ActionManager* actionManager,
        MacroQueueAttempt attempt,
        ushort currentSequence)
    {
        if (actionManager == null) return;
        var queueAfter = CaptureNativeQueue(actionManager);
        ReconcileOwnedNativeQueue(currentSequence, queueAfter);
        var queueTuple = attempt.Attempted with
        {
            // QueueType is stored native state and is independent of the
            // UseAction mode that created it. Ownership always records the
            // exact value currently present in the queue.
            Mode = queueAfter.Mode,
        };
        var claimed = currentSequence == attempt.SequenceBefore
            && queueAfter.Matches(queueTuple)
            && !attempt.QueueBefore.Matches(queueTuple)
            && TryClaimOwnedNativeQueue(
                attempt.Generation,
                currentSequence,
                attempt.QueueBefore,
                queueAfter,
                queueTuple,
                attempt.SafetySeed,
                actionManager,
                "after a Macro queue outcome");
        var immediateAcceptance = currentSequence != 0
            && currentSequence != attempt.SequenceBefore;
        var exactAcceptance = immediateAcceptance || claimed;
        var outcomeStillOwned = inputGenerations.IsCurrent(attempt.Generation)
            && (attempt.Runtime is { } currentRuntime
                ? ReferenceEquals(macroTurboRuntime, currentRuntime)
                : attempt.InputScope is { } currentInputScope
                    && ReferenceEquals(activeHotbarInput, currentInputScope)
                    && currentInputScope.MacroProfileAtPress is not null);

        if (!outcomeStillOwned)
        {
            // Cancellation or replacement may race while Original runs outside
            // dispatchGate. Classify and claim only the exact new queue anyway,
            // then let the previously armed tombstone clear it. The stale
            // runtime itself is never revived or acknowledged.
            if (ownedNativeQueueSafetyClearPending)
            {
                RetryExactOwnedNativeQueueSafetyClear(
                    actionManager,
                    "after a stale in-flight Macro outcome");
            }

            if (latestCertifiedQueueReplacementGeneration > attempt.Generation)
            {
                TryReplaceOwnedNativeQueue(
                    actionManager,
                    latestCertifiedQueueReplacementGeneration,
                    "after a replaced asynchronous vanilla Macro outcome");
            }

            return;
        }

        if (attempt.Runtime is { } outcomeRuntime
            && outcomeRuntime.InitialMacroLockCompleted
            && outcomeRuntime.ActiveExecutionBudget is { } executionBudget
            && outcomeRuntime.ActiveExecutionEpoch > 0
            && exactAcceptance)
        {
            var markResult = executionBudget.MarkAcceptedOutcome();
            if (markResult != MacroTurboAcceptedOutcomeMarkResult.Marked)
            {
                QuarantineSyntheticMacroExecutor(
                    outcomeRuntime,
                    $"native accepted outcome could not be bounded ({markResult})");
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.ResolvedActionChange,
                    $"Macro Turbo could not mark its one accepted outcome ({markResult}, observed={executionBudget.ObservedActionCalls}, accepted={executionBudget.AcceptedOutcomeCount})",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return;
            }
        }

        if (exactAcceptance)
        {
            var seed = new MacroTurboAcknowledgementSeed(
                new TurboActionEffectExpectation(
                    attempt.Attempted.ActionType,
                    attempt.Attempted.RequestedActionId,
                    attempt.Attempted.ResolvedActionId,
                    immediateAcceptance
                        ? TurboAcknowledgementSequenceMode.ImmediateExact
                        : TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                    immediateAcceptance ? currentSequence : attempt.SequenceBefore),
                attempt.StartedAtMilliseconds);

            if (attempt.InputScope is { } physicalScope)
            {
                if (physicalScope.InitialMacroAcceptedOutcomeCount == int.MaxValue)
                {
                    physicalScope.MacroProvenanceDisqualified = true;
                    physicalScope.MacroProvenanceFailure =
                        "original macro accepted-outcome counter was exhausted";
                }
                else
                {
                    physicalScope.InitialMacroAcceptedOutcomeCount++;
                    if (physicalScope.InitialMacroAcceptedOutcomeCount > 1)
                    {
                        // The physical macro remains completely vanilla. This
                        // only refuses later Turbo ownership when that one
                        // player press proved more than one native outcome.
                        physicalScope.MacroProvenanceDisqualified = true;
                        physicalScope.MacroProvenanceFailure =
                            $"original macro produced {physicalScope.InitialMacroAcceptedOutcomeCount} accepted outcomes";
                    }
                }

                physicalScope.InitialMacroAcknowledgement ??= seed;
            }
            else if (attempt.Runtime is { } acknowledgementRuntime)
            {
                if (!acknowledgementRuntime.InitialMacroLockCompleted)
                {
                    if (acknowledgementRuntime.InitialAcceptedOutcomeCount == int.MaxValue)
                    {
                        CancelTurboUnsafe(
                            HoldRepeatCancelReason.Fault,
                            "Macro Turbo initial accepted-outcome counter was exhausted");
                        return;
                    }

                    acknowledgementRuntime.InitialAcceptedOutcomeCount++;
                    if (acknowledgementRuntime.InitialAcceptedOutcomeCount > 1)
                    {
                        // This is still the player's untouched initial native
                        // executor. Stop adopting it as Turbo provenance, but
                        // never suppress the original macro continuation.
                        CancelTurboUnsafe(
                            HoldRepeatCancelReason.PulseRejected,
                            $"Original macro produced {acknowledgementRuntime.InitialAcceptedOutcomeCount} accepted outcomes; Turbo was not armed");
                        return;
                    }
                }

                if (!BeginMacroTurboAcknowledgement(
                        acknowledgementRuntime,
                        acknowledgementRuntime.InitialMacroLockCompleted ? attempt.PulseToken : null,
                        acknowledgementRuntime.InitialMacroLockCompleted ? attempt.ExecutionEpoch : 0,
                        seed))
                {
                    if (acknowledgementRuntime.InitialMacroLockCompleted)
                    {
                        QuarantineSyntheticMacroExecutor(
                            acknowledgementRuntime,
                            "accepted macro action could not establish an acknowledgement barrier");
                    }

                    CancelTurboUnsafe(
                        HoldRepeatCancelReason.PulseRejected,
                        "Macro Turbo accepted an action without a provable acknowledgement barrier",
                        ownedQueuePolicy: acknowledgementRuntime.InitialMacroLockCompleted
                            ? OwnedQueueCancelPolicy.ExactClear
                            : OwnedQueueCancelPolicy.Preserve);
                    return;
                }
            }
        }

        if (immediateAcceptance)
        {
            if (attempt.Runtime is { } immediateRuntime) immediateRuntime.OwnedQueueTuple = null;
            if (attempt.InputScope is { } immediateScope) immediateScope.OwnedMacroQueueTuple = null;
            RecordSentSequence(currentSequence, NowMilliseconds);
        }

        if (claimed)
        {
            if (attempt.Runtime is { } owningRuntime)
            {
                owningRuntime.OwnedQueueTuple = queueTuple;
            }
            else if (attempt.InputScope is { } owningScope)
            {
                owningScope.OwnedMacroQueueTuple = queueTuple;
            }

            if (configuration.DetailedLogging)
            {
                log.Debug(
                    "Macro Turbo claimed exact native queue generation={Generation}, action={Action}, mode={Mode}.",
                    attempt.Generation,
                    queueAfter.ActionId,
                    queueAfter.Mode);
            }

            return;
        }

        if (attempt.Runtime is { OwnedQueueTuple: { } runtimeTuple }
            && !queueAfter.Matches(runtimeTuple))
        {
            attempt.Runtime.OwnedQueueTuple = null;
        }
        else if (attempt.InputScope is { OwnedMacroQueueTuple: { } scopeTuple }
            && !queueAfter.Matches(scopeTuple))
        {
            attempt.InputScope.OwnedMacroQueueTuple = null;
        }
    }

    private bool TryAuthorizeDirectPulseInvocation(
        DirectPulseExecutionScope execution,
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out ExactActionTuple exactTuple,
        out OwnedNativeQueueSafetySeed safetySeed)
    {
        exactTuple = default;
        safetySeed = null!;
        execution.InvocationCount++;
        var runtime = execution.Runtime;
        if (execution.InvocationCount != 1)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.ResolvedActionChange,
                $"Direct same-slot Turbo emitted more than one action call (calls={execution.InvocationCount})",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        if (actionManager == null
            || !ReferenceEquals(turboRuntime, runtime)
            || !turboEngine.IsTokenCurrent(execution.Token)
            || activeDirectPulseExecution != execution
            || mode != ActionManager.UseActionMode.None
            || actionType is not (ActionType.Action or ActionType.PvPAction)
            || actionId == 0
            || actionId != runtime.SlotIdentity.CommandId
            || !inputGenerations.IsCurrent(runtime.Candidate.InputGeneration)
            || Volatile.Read(ref latestCertifiedPressId) != runtime.Press.PressId
            || physicalHotbarInput?.IsStillHeld(runtime.Press) != true
            || !TryReadCurrentSlotIdentity(runtime.Press, out var currentIdentity)
            || currentIdentity != runtime.SlotIdentity
            || activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || !compatibility.IsLiveReActionProfileCurrent())
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.PluginChange,
                "Direct same-slot Turbo call-chain identity or compatibility changed",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        var resolvedActionId = actionManager->GetAdjustedActionId(actionId);
        if (resolvedActionId == 0
            || resolvedActionId != execution.ExpectedResolvedActionId
            || excludedIntegrationActionIds.Contains(actionId)
            || excludedIntegrationActionIds.Contains(resolvedActionId)
            || !compatibility.IsLiveMOActionUnowned(actionId, resolvedActionId)
            || !TryGetEligibleActionProfile(
                actionType,
                resolvedActionId,
                targetId,
                out var includeResolverTargets)
            || includeResolverTargets != runtime.Candidate.IncludeResolverTargets)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.ResolvedActionChange,
                $"Direct same-slot Turbo call was no longer eligible ({actionId}->{resolvedActionId})",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        var capturedTargetId = runtime.Candidate.TargetId;
        var targetMatches = runtime.HasCapturedInvocation
            ? targetId == capturedTargetId
            : targetId is 0 or InvalidObjectId;
        var snapshotTargetId = runtime.HasCapturedInvocation ? targetId : 0;
        var currentSnapshot = CaptureSnapshot(
            snapshotTargetId,
            resolvedActionId,
            includeResolverTargets);
        if (!targetMatches
            || !IsSafeSnapshot(currentSnapshot)
            || currentSnapshot.TargetFingerprint != runtime.Candidate.Snapshot.TargetFingerprint
            || currentSnapshot.TerritoryId != runtime.Candidate.Snapshot.TerritoryId
            || currentSnapshot.ContextFingerprint != runtime.Candidate.Snapshot.ContextFingerprint
            || currentSnapshot.LocalGameObjectId != runtime.Candidate.Snapshot.LocalGameObjectId
            || currentSnapshot.LocalAddress != runtime.Candidate.Snapshot.LocalAddress
            || runtime.HasCapturedInvocation
                && (extraParam != runtime.Candidate.ExtraParam
                    || comboRouteId != runtime.Candidate.ComboRouteId))
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.TargetChange,
                "Direct same-slot Turbo target, resolver, or native parameters changed",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        exactTuple = new ExactActionTuple(
            (uint)actionType,
            actionId,
            resolvedActionId,
            targetId,
            extraParam,
            (uint)mode,
            comboRouteId);
        safetySeed = new OwnedNativeQueueSafetySeed(
            runtime.Candidate.Snapshot,
            currentSnapshot,
            includeResolverTargets,
            runtime.Candidate.ExplicitTargetAddress);
        execution.ExactTuple = exactTuple;
        return true;
    }

    private void ProcessDirectPulseAttempt(
        ActionManager* actionManager,
        DirectPulseAttempt attempt,
        bool result,
        ushort currentSequence)
    {
        var execution = attempt.Execution;
        var runtime = execution.Runtime;
        if (actionManager == null) return;

        var queueAfter = CaptureNativeQueue(actionManager);
        var sequenceAdvanced = currentSequence != attempt.SequenceBefore;
        var nativeOutcome = NativeActionOutcomeClassifier.Classify(
            result || sequenceAdvanced,
            attempt.QueueBefore,
            queueAfter,
            attempt.ExactTuple);
        var claimed = nativeOutcome == NativeActionOutcome.MatchingNewQueue
            && !sequenceAdvanced
            && TryClaimOwnedNativeQueue(
                runtime.Candidate.InputGeneration,
                currentSequence,
                attempt.QueueBefore,
                queueAfter,
                attempt.ExactTuple,
                attempt.SafetySeed,
                actionManager,
                "after a direct Turbo queue outcome");

        var outcomeStillOwned = ReferenceEquals(turboRuntime, runtime)
            && turboEngine.IsTokenCurrent(execution.Token)
            && activeDirectPulseExecution == execution;
        if (!outcomeStillOwned)
        {
            // Original runs outside dispatchGate and can trigger a re-entrant
            // terminal cancellation. Claim only its exact post-call queue, then
            // apply the existing generation tombstone without reviving runtime
            // acknowledgement or ownership.
            if (ownedNativeQueueSafetyClearPending)
            {
                RetryExactOwnedNativeQueueSafetyClear(
                    actionManager,
                    "after a stale in-flight direct Turbo outcome");
            }
            if (latestCertifiedQueueReplacementGeneration > runtime.Candidate.InputGeneration)
            {
                TryReplaceOwnedNativeQueue(
                    actionManager,
                    latestCertifiedQueueReplacementGeneration,
                    "after a replaced direct Turbo outcome");
            }

            return;
        }

        execution.Completed = true;
        execution.ExactTuple = attempt.ExactTuple;
        execution.SequenceBefore = attempt.SequenceBefore;
        execution.SequenceAfter = currentSequence;
        execution.QueueAfter = queueAfter;

        if (nativeOutcome == NativeActionOutcome.ImmediateAcceptance && sequenceAdvanced)
        {
            runtime.OwnedQueueTuple = null;
            RecordSentSequence(currentSequence, NowMilliseconds);
            if (!BeginTurboAcknowledgement(
                    runtime,
                    execution.Token,
                    TurboAcknowledgementSequenceMode.ImmediateExact,
                    currentSequence,
                    attempt.ExactTuple))
            {
                RejectTurboPulseUnsafe(
                    $"Direct same-slot action {attempt.ExactTuple.ResolvedActionId} had no valid acknowledgement identity");
                return;
            }

            execution.Accepted = true;
            return;
        }

        if (nativeOutcome == NativeActionOutcome.MatchingNewQueue && !sequenceAdvanced)
        {
            if (claimed
                && BeginTurboAcknowledgement(
                    runtime,
                    execution.Token,
                    TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                    attempt.SequenceBefore,
                    attempt.ExactTuple))
            {
                runtime.OwnedQueueTuple = attempt.ExactTuple with { Mode = queueAfter.Mode };
                execution.Accepted = true;
                return;
            }

        }

        RejectTurboPulseUnsafe(
            $"Direct same-slot action {attempt.ExactTuple.ResolvedActionId} was {nativeOutcome} with sequenceAdvanced={sequenceAdvanced}");
    }

    private bool IsOwnedMacroTurboQueueDrain(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out NativeQueueDrainAttempt? drainAttempt)
    {
        drainAttempt = null;
        var runtime = macroTurboRuntime;
        if (runtime is null
            || actionManager == null
            || mode != ActionManager.UseActionMode.Queue
            || runtime.OwnedQueueTuple is not { } ownedTuple
            || !turboEngine.Snapshot.HasActiveHold
            || !inputGenerations.IsCurrent(runtime.Generation)
            || Volatile.Read(ref latestCertifiedPressId) != runtime.Press.PressId
            || physicalHotbarInput?.IsStillHeld(runtime.Press) != true
            || actionType != (ActionType)ownedTuple.ActionType
            || actionId == 0
            || actionId != ownedTuple.RequestedActionId && actionId != ownedTuple.ResolvedActionId
            || targetId != ownedTuple.TargetId
            || extraParam != ownedTuple.Param
            || comboRouteId != ownedTuple.RouteId
            || excludedIntegrationActionIds.Contains(ownedTuple.RequestedActionId)
            || excludedIntegrationActionIds.Contains(ownedTuple.ResolvedActionId)
            || !compatibility.IsLiveMOActionUnowned(
                ownedTuple.RequestedActionId,
                ownedTuple.ResolvedActionId)
            || !IsTurboSafetySafe(ObserveMacroTurbo(runtime, checkMacroHash: true).Safety))
        {
            return false;
        }

        var currentQueue = CaptureNativeQueue(actionManager);
        if (!currentQueue.IsQueued)
        {
            // ReAction may be the outer hook and hide the exact entry before
            // this detour observes its Queue-mode drain. Attribute the call only
            // to the same sidecar-bound owner and only while no lease exists.
            return nativeQueueOwnership.CanDeferExactHiddenDrain(
                runtime.Generation,
                ownedTuple);
        }

        if (!TryBeginOwnedNativeQueueDrain(
                runtime.Generation,
                actionManager->LastUsedActionSequence,
                currentQueue,
                ownedTuple,
                out var lease))
        {
            return false;
        }

        drainAttempt = new NativeQueueDrainAttempt(
            lease,
            runtime.Generation,
            runtime,
            null);
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Macro Turbo leased exact native queue drain generation={Generation}, action={Action}.",
                runtime.Generation,
                actionId);
        }

        return true;
    }

    private bool IsOwnedMacroTurboExecutionContinuation(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out MacroActionInvocation entry,
        out bool firstInitialEntry,
        out bool suppressCurrentCall,
        out bool observedOwnedMacroContinuation)
    {
        entry = default;
        firstInitialEntry = false;
        suppressCurrentCall = false;
        observedOwnedMacroContinuation = false;
        var runtime = macroTurboRuntime;
        var macroLocked = IsMacroExecutionActive();
        var bindingMatches = runtime is not null
            && TryReadCurrentSlotIdentity(runtime.Press, out var currentIdentity)
            && currentIdentity == runtime.SlotIdentity;
        var ownedExecutorContext = runtime is not null
            && runtime.OwnsMacroExecutor
            && macroLocked
            && turboEngine.Snapshot.HasActiveHold
            && inputGenerations.IsCurrent(runtime.Generation)
            && Volatile.Read(ref latestCertifiedPressId) == runtime.Press.PressId
            && physicalHotbarInput?.IsStillHeld(runtime.Press) == true
            && bindingMatches;
        if (runtime is not null
            && runtime.OwnsMacroExecutor
            && macroLocked
            && runtime.ActiveExecutionBudget is not null
            && runtime.ActiveExecutionEpoch > 0)
        {
            suppressCurrentCall = true;
        }

        observedOwnedMacroContinuation = runtime is not null
            && ownedExecutorContext
            && mode == ActionManager.UseActionMode.Macro;
        if (observedOwnedMacroContinuation && !runtime!.InitialMacroLockCompleted)
        {
            // Count before eligibility so a failing current call cannot create
            // slack when cancellation retires the physical executor.
            runtime.InitialPhysicalActionCallCount = Math.Min(
                runtime.InitialPhysicalActionCallCount + 1,
                runtime.MacroProfile.ActionCount);
        }

        if (runtime is null
            || !ownedExecutorContext
            || mode != ActionManager.UseActionMode.Macro
            || !IsTurboSafetySafe(ObserveMacroTurbo(runtime, checkMacroHash: true).Safety))
        {
            if (runtime is not null && suppressCurrentCall)
            {
                QuarantineSyntheticMacroExecutor(
                    runtime,
                    "frozen asynchronous executor continuation failed safety or mode validation");
            }

            if (observedOwnedMacroContinuation)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.PluginChange,
                    "Owned Macro continuation failed live safety validation",
                    ownedQueuePolicy: runtime!.InitialMacroLockCompleted
                        ? OwnedQueueCancelPolicy.ExactClear
                        : OwnedQueueCancelPolicy.Preserve);
            }

            return false;
        }

        runtime.InitialMacroLockObserved = true;
        var authorized = TryAuthorizeRuntimeMacroInvocation(
            runtime,
            actionManager,
            actionType,
            actionId,
            targetId,
            extraParam,
            mode,
            comboRouteId,
            out entry,
            out firstInitialEntry);
        if (authorized) suppressCurrentCall = false;
        return authorized;
    }

    private bool IsOwnedTurboActionContinuation(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out NativeQueueDrainAttempt? drainAttempt)
    {
        drainAttempt = null;
        var runtime = turboRuntime;
        if (runtime is null
            || actionManager == null
            || !turboEngine.Snapshot.HasActiveHold
            || Volatile.Read(ref latestCertifiedPressId) != runtime.Press.PressId
            || physicalHotbarInput?.IsStillHeld(runtime.Press) != true)
        {
            return false;
        }

        if (runtime.OwnedQueueTuple is not { } ownedTuple) return false;
        var exactInvocation = actionType == (ActionType)ownedTuple.ActionType
            && actionId is var observedActionId
            && observedActionId != 0
            && (observedActionId == ownedTuple.RequestedActionId
                || observedActionId == ownedTuple.ResolvedActionId)
            && targetId == ownedTuple.TargetId
            && extraParam == ownedTuple.Param
            && comboRouteId == ownedTuple.RouteId;
        var currentQueue = CaptureNativeQueue(actionManager);
        var ownedQueueTuple = currentQueue.IsQueued
            ? ownedTuple with
            {
                // QueueType describes the stored entry and is not the same thing as
                // the UseActionMode.Queue invocation that drains it. Preserve the
                // exact stored identity for ownership authorization while requiring
                // the observed invocation itself to be an explicit native drain.
                Mode = currentQueue.Mode,
            }
            : ownedTuple;
        if (mode == ActionManager.UseActionMode.Queue
            && exactInvocation
            && !currentQueue.IsQueued)
        {
            return nativeQueueOwnership.CanDeferExactHiddenDrain(
                runtime.Candidate.InputGeneration,
                ownedQueueTuple);
        }

        var lease = default(NativeQueueDrainLease);
        var authorized = mode == ActionManager.UseActionMode.Queue
            && exactInvocation
            && TryBeginOwnedNativeQueueDrain(
                runtime.Candidate.InputGeneration,
                actionManager->LastUsedActionSequence,
                currentQueue,
                ownedQueueTuple,
                out lease);
        if (authorized)
        {
            drainAttempt = new NativeQueueDrainAttempt(
                lease,
                runtime.Candidate.InputGeneration,
                null,
                runtime);
        }

        return authorized;
    }

    private static bool IsMacroExecutionActive()
    {
        var shell = RaptureShellModule.Instance();
        return shell != null && shell->MacroLocked;
    }

    private void QuarantineSyntheticMacroExecutor(
        MacroTurboRuntime runtime,
        string reason)
    {
        if (runtime.ActiveExecutionBudget is null || runtime.ActiveExecutionEpoch <= 0) return;
        var now = NowMilliseconds;
        var existing = syntheticMacroExecutorQuarantine;
        if (existing is null
            || existing.Generation != runtime.Generation
            || existing.ExecutionEpoch != runtime.ActiveExecutionEpoch)
        {
            syntheticMacroExecutorQuarantine = new SyntheticMacroExecutorQuarantine(
                runtime.Generation,
                runtime.Press.PressId,
                runtime.ActiveExecutionEpoch,
                now,
                SaturatingAdd(now, MaximumMacroCaptureMilliseconds));
        }

        lastEvent = $"Synthetic Macro executor quarantined: {reason}";
        if (configuration.DetailedLogging)
        {
            log.Warning(
                "Synthetic Macro Turbo executor quarantined generation={Generation}, epoch={Epoch}, reason={Reason}.",
                runtime.Generation,
                runtime.ActiveExecutionEpoch,
                reason);
        }
    }

    private bool ShouldSuppressQuarantinedSyntheticMacroCall()
    {
        if (syntheticMacroExecutorQuarantine is not { } quarantine
            || !IsMacroExecutionActive())
        {
            return false;
        }

        syntheticMacroSuppressedCallCount++;
        lastEvent = $"Suppressed quarantined synthetic Macro call #{syntheticMacroSuppressedCallCount}";
        if (configuration.DetailedLogging)
        {
            log.Warning(
                "Suppressed quarantined synthetic Macro call generation={Generation}, epoch={Epoch}, total={Total}.",
                quarantine.Generation,
                quarantine.ExecutionEpoch,
                syntheticMacroSuppressedCallCount);
        }

        return true;
    }

    private void ReconcileSyntheticMacroExecutorQuarantine(long now)
    {
        var quarantine = syntheticMacroExecutorQuarantine;
        if (quarantine is null) return;
        if (IsMacroExecutionActive())
        {
            if (!quarantine.TimeoutReported
                && (now < 0 || now > quarantine.ExpiresAtMilliseconds))
            {
                syntheticMacroExecutorQuarantine = quarantine with { TimeoutReported = true };
                lastEvent = "Synthetic Macro quarantine exceeded two seconds and remains sealed until native MacroLocked clears";
                log.Warning(
                    "Synthetic Macro Turbo quarantine exceeded its diagnostic timeout while MacroLocked remained active; suppression stays armed generation={Generation}, epoch={Epoch}.",
                    quarantine.Generation,
                    quarantine.ExecutionEpoch);
            }

            // A timeout can never authorize a stale native executor. Keep its
            // Macro-mode tombstone until the game proves MacroLocked false.
            return;
        }

        syntheticMacroExecutorQuarantine = null;
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Synthetic Macro executor quarantine cleared generation={Generation}, epoch={Epoch}, reason={Reason}.",
                quarantine.Generation,
                quarantine.ExecutionEpoch,
                "native MacroLocked observed false");
        }
    }

    private void RetainRetiredPhysicalMacroExecutor(
        MacroTurboRuntime runtime,
        string reason)
    {
        if (runtime.InitialMacroLockCompleted
            || !runtime.OwnsMacroExecutor
            || !IsMacroExecutionActive()
            || runtime.InitialExecutionBudget is null)
        {
            return;
        }

        RetainRetiredPhysicalMacroExecutor(
            runtime.Generation,
            runtime.Press.PressId,
            runtime.SlotIdentity,
            runtime.MacroProfile,
            runtime.Snapshot,
            Math.Min(runtime.InitialPhysicalActionCallCount, runtime.MacroProfile.ActionCount),
            reason);
    }

    private void RetainRetiredPhysicalMacroExecutor(
        HotbarInputScope scope,
        string reason)
    {
        if (scope.CertifiedPress is not { } press
            || scope.SlotIdentity is not { CommandType: MacroHotbarSlotType } slotIdentity
            || scope.MacroProfileAtPress is not { } profile
            || scope.MacroExecutionBudget is null
            || !IsMacroExecutionActive())
        {
            return;
        }

        RetainRetiredPhysicalMacroExecutor(
            scope.Generation,
            press.PressId,
            slotIdentity,
            profile,
            scope.MacroSnapshotAtPress!,
            Math.Min(scope.ActionInvocationCount, profile.ActionCount),
            reason);
    }

    private void RetainRetiredPhysicalMacroExecutor(
        long generation,
        long pressId,
        HotbarSlotIdentity slotIdentity,
        SafeActionMacroProfile profile,
        Snapshot snapshot,
        int observedActionCalls,
        string reason)
    {
        var now = NowMilliseconds;
        var existing = retiredPhysicalMacroExecutor;
        if (existing is not null && existing.Generation > generation) return;
        if (existing is not null
            && existing.Generation == generation
            && (existing.PressId != pressId
                || existing.SlotIdentity != slotIdentity
                || existing.ContentFingerprint != profile.ContentFingerprint
                || existing.MaximumActionCalls != profile.ActionCount
                || existing.Snapshot != snapshot))
        {
            // Same-generation identity reuse is not provable. Retire the
            // observer rather than attaching it to a different executor.
            retiredPhysicalMacroExecutor = null;
            return;
        }

        if (existing is null || existing.Generation < generation)
        {
            retiredPhysicalMacroExecutor = new RetiredPhysicalMacroExecutor(
                generation,
                pressId,
                slotIdentity,
                profile.ContentFingerprint,
                profile.ActionCount,
                snapshot,
                observedActionCalls,
                now,
                SaturatingAdd(now, MaximumMacroCaptureMilliseconds));
        }
        else if (existing is not null && observedActionCalls > existing.ObservedActionCalls)
        {
            retiredPhysicalMacroExecutor = existing with
            {
                ObservedActionCalls = Math.Min(observedActionCalls, existing.MaximumActionCalls),
            };
        }

        if (configuration.DetailedLogging)
        {
            log.Information(
                "Retained non-suppressing physical Macro outcome observer generation={Generation}, press={PressId}, reason={Reason}.",
                generation,
                pressId,
                reason);
        }
    }

    private void ReconcileRetiredPhysicalMacroExecutor(long now)
    {
        var retired = retiredPhysicalMacroExecutor;
        if (retired is null) return;
        if (IsMacroExecutionActive())
        {
            if (!retired.TimeoutReported
                && (now < 0 || now > retired.ExpiresAtMilliseconds))
            {
                retiredPhysicalMacroExecutor = retired with { TimeoutReported = true };
                log.Warning(
                    "Physical Macro outcome observer exceeded its diagnostic timeout and remains read-only until MacroLocked clears generation={Generation}, press={PressId}.",
                    retired.Generation,
                    retired.PressId);
            }

            return;
        }

        retiredPhysicalMacroExecutor = null;
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Retired physical Macro outcome observer cleared after MacroLocked became false generation={Generation}, press={PressId}.",
                retired.Generation,
                retired.PressId);
        }
    }

    private void TryClearSyntheticMacroQuarantineForCertifiedRoot(HotbarInputScope scope)
    {
        var quarantine = syntheticMacroExecutorQuarantine;
        if (quarantine is null
            || scope.CertifiedPress is not { } press
            || scope.SlotIdentity is not { CommandType: MacroHotbarSlotType }
            || scope.MacroWasLockedBeforeExecution
            || IsMacroExecutionActive()
            || scope.Generation <= quarantine.Generation
            || press.PressId <= quarantine.PressId)
        {
            return;
        }

        syntheticMacroExecutorQuarantine = null;
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Synthetic Macro executor quarantine cleared by newer certified root Macro press={PressId}, generation={Generation}.",
                press.PressId,
                scope.Generation);
        }
    }

    private void CompleteHotbarInput()
    {
        var scope = activeHotbarInput;
        activeHotbarInput = null;
        if (scope is null) return;

        try
        {
            lock (dispatchGate)
            {
                var actionManager = ActionManager.Instance();
                if (actionManager != null)
                {
                    // A terminal cancellation may invalidate this scope while
                    // its native Original is still returning. Clear a newly
                    // proven exact queue before the stale-generation exit.
                    RetryExactOwnedNativeQueueSafetyClear(
                        actionManager,
                        "after the complete hotbar call");
                }

                RetainRetiredPhysicalMacroExecutor(
                    scope,
                    "certified physical Macro remained MacroLocked after hotbar completion");

                if (!inputGenerations.IsCurrent(scope.Generation)) return;
                if (actionManager != null && scope.MaySupersedeOwnedQueue)
                {
                    TryReplaceOwnedNativeQueue(actionManager, scope.Generation, "after the complete hotbar call");
                }

                // Held-input repetition is produced upstream by
                // PhysicalHotbarInputSource at the native logical-input boundary.
                // Never replay ExecuteSlot or actions from this completion path.
            }
        }
        catch (Exception exception)
        {
            Fault(exception, "Hotbar completion validation failed");
        }
    }

    private void TryStartTurbo(HotbarInputScope scope)
    {
        if (!configuration.Enabled
            || !configuration.TurboEnabled
            || configuration.DryRun
            || physicalHotbarInput is not { } inputSource
            || scope.CertifiedPress is not { } press
            || scope.SlotIdentity is not { } slotIdentity
            || !inputGenerations.IsCurrent(scope.Generation)
            || Volatile.Read(ref latestCertifiedPressId) != press.PressId
            || (!configuration.TurboOutOfCombat && !condition[ConditionFlag.InCombat]))
        {
            return;
        }

        if (activeConflicts.Count > 0)
        {
            LogTurboStartRejected(
                slotIdentity,
                $"compatibility conflict: {string.Join(" | ", activeConflicts)}");
            return;
        }

        if (compatibilityQuarantineFrames > 0)
        {
            LogTurboStartRejected(slotIdentity, "compatibility profile is waiting for one clean frame");
            return;
        }

        if (slotIdentity.CommandType == MacroHotbarSlotType)
        {
            TryBeginMacroTurbo(scope, press, slotIdentity, inputSource);
            return;
        }

        if (slotIdentity.CommandType != DirectActionHotbarSlotType)
        {
            LogTurboStartRejected(slotIdentity, "slot type is outside the audited direct/macro scope");
            return;
        }

        if (scope.DirectSnapshotAtPress is not { } directPressSnapshot
            || !IsSafeSnapshot(directPressSnapshot))
        {
            LogTurboStartRejected(
                slotIdentity,
                "direct slot was mounted or otherwise unsafe at the physical press edge");
            return;
        }

        if (scope.TurboDisqualified || scope.ActionInvocationCount > 1)
        {
            LogTurboStartRejected(
                slotIdentity,
                DescribeDirectTurboIneligibility(slotIdentity, "physical slot emitted an ineligible or non-unique action call"));
            return;
        }


        var candidate = scope.TurboCandidate;
        if (candidate is null
            && scope.ActionInvocationCount == 0
            && !TryCreateDirectTurboCandidate(scope, slotIdentity, out candidate, out var failure))
        {
            LogTurboStartRejected(slotIdentity, failure);
            return;
        }

        if (candidate is null || scope.ActionInvocationCount is not (0 or 1))
        {
            LogTurboStartRejected(slotIdentity, "direct slot did not establish safe same-slot ownership");
            return;
        }

        if (candidate.InputGeneration != scope.Generation
            || slotIdentity.CommandId != candidate.RequestedActionId)
        {
            LogTurboStartRejected(slotIdentity, "action identity no longer matches the certified slot");
            return;
        }

        StartTurboRuntime(scope, press, slotIdentity, candidate, null, inputSource);
    }

    private void TryBeginMacroTurbo(
        HotbarInputScope scope,
        CertifiedHotbarPress press,
        HotbarSlotIdentity slotIdentity,
        PhysicalHotbarInputSource inputSource)
    {
        if (!configuration.TurboMacrosEnabled)
        {
            LogTurboStartRejected(slotIdentity, "macro Turbo is disabled");
            return;
        }

        if (scope.MacroWasLockedBeforeExecution)
        {
            LogTurboStartRejected(slotIdentity, "another macro already owned the native macro executor at the physical press");
            return;
        }

        if (scope.MacroProfileAtPress is not { } certifiedProfile)
        {
            LogTurboStartRejected(slotIdentity, "macro was not certified before native slot execution");
            return;
        }

        if (scope.MacroProvenanceDisqualified
            || scope.MacroExecutionBudget is null)
        {
            LogTurboStartRejected(
                slotIdentity,
                scope.MacroProvenanceFailure ?? "macro action provenance was unavailable");
            return;
        }

        if (!TryReadSafeMacroProfile(slotIdentity, out var profile, out var failure)
            || profile.ContentFingerprint != certifiedProfile.ContentFingerprint)
        {
            LogTurboStartRejected(slotIdentity, $"macro profile rejected or changed ({failure})");
            return;
        }

        if (scope.MacroSnapshotAtPress is not { } macroSnapshot
            || !IsSafeSnapshot(macroSnapshot))
        {
            LogTurboStartRejected(slotIdentity, "macro context was unavailable or unsafe at the certified press");
            return;
        }

        StartMacroTurboRuntime(
            scope,
            press,
            slotIdentity,
            profile,
            macroSnapshot,
            inputSource);
    }

    private void StartMacroTurboRuntime(
        HotbarInputScope scope,
        CertifiedHotbarPress press,
        HotbarSlotIdentity slotIdentity,
        SafeActionMacroProfile macroProfile,
        Snapshot macroSnapshot,
        PhysicalHotbarInputSource inputSource)
    {
        var currentSnapshot = CaptureSnapshot(0, 0, includeResolverTargets: true);
        if (candidateIdentityChanged())
        {
            LogTurboStartRejected(slotIdentity, "macro binding, content, context, or compatibility changed during execution");
            return;
        }

        var macroLocked = IsMacroExecutionActive();
        var initialBudget = scope.MacroExecutionBudget;
        if (initialBudget is null)
        {
            LogTurboStartRejected(slotIdentity, "macro execution budget was unavailable");
            return;
        }

        if (!macroLocked)
        {
            var completion = initialBudget.Finish();
            if (completion != MacroTurboExecutionBudgetResult.Complete)
            {
                LogTurboStartRejected(
                    slotIdentity,
                    $"synchronous macro exceeded its action-call budget ({completion}, observed={initialBudget.ObservedActionCalls}, max={initialBudget.MaxActionCalls})");
                return;
            }
        }

        var options = CreateTurboOptions();
        if (turboEngine.Options != options)
        {
            CancelTurboUnsafe(HoldRepeatCancelReason.PluginChange, "Turbo timing changed");
            turboEngine = new HoldRepeatEngine(options);
        }

        if (turboEngine.Snapshot.State == HoldRepeatState.NeedsRelease)
        {
            turboEngine.ObserveRelease();
        }

        var intentFingerprint = NonZeroFingerprint(
            slotIdentity.CommandType,
            slotIdentity.CommandId,
            macroSnapshot.TargetFingerprint,
            macroSnapshot.ContextFingerprint,
            Convert.ToUInt64(macroProfile.ContentFingerprint[..16], 16));
        var request = new HoldRepeatStartRequest(
            press.PressId,
            scope.Generation,
            slotIdentity.ControlFingerprint,
            intentFingerprint,
            IsCertifiedFreshPress: true);
        var result = turboEngine.TryStart(request, NowMilliseconds);
        if (result is not (HoldRepeatStartResult.Started or HoldRepeatStartResult.Replaced))
        {
            return;
        }

        turboRuntime = null;
        Interlocked.Exchange(ref turboAcknowledgement, null);
        Interlocked.Exchange(ref macroTurboAcknowledgement, null);
        var runtime = new MacroTurboRuntime(
            press,
            slotIdentity,
            macroProfile,
            macroSnapshot,
            compatibilitySignature,
            scope.Generation,
            request,
            SaturatingAdd(NowMilliseconds, MaximumMacroCaptureMilliseconds),
            macroLocked ? initialBudget : null,
            Math.Min(scope.ActionInvocationCount, macroProfile.ActionCount))
        {
            InitialMacroLockObserved = scope.MacroLockObservedDuringExecution || macroLocked,
            // Returning from the certified native slot while unlocked is itself
            // a completion barrier for instant/synchronous macros. We never wait
            // for or adopt an unrelated future MacroLocked state.
            InitialMacroLockCompleted = !macroLocked,
            InitialAcceptedOutcomeCount = scope.InitialMacroAcceptedOutcomeCount,
            OwnsMacroExecutor = macroLocked,
            OwnedQueueTuple = scope.OwnedMacroQueueTuple,
        };
        macroTurboRuntime = runtime;
        if (scope.InitialMacroAcknowledgement is { } initialAcknowledgement
            && !BeginMacroTurboAcknowledgement(
                runtime,
                pulse: null,
                executionEpoch: 0,
                initialAcknowledgement))
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.PulseRejected,
                "Macro Turbo could not prove the original macro action acknowledgement barrier");
            return;
        }

        turboStartCount++;
        lastEvent = $"Macro Turbo owns hotbar {slotIdentity.Binding.HotbarId + 1}, slot {slotIdentity.Binding.SlotId + 1}";
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Turbo macro start press={PressId}, generation={Generation}, hotbar={Hotbar}, slot={Slot}, expectedActions={ExpectedActions}, observedActions={ObservedActions}, initialLockObserved={LockObserved}, initialLockActive={LockActive}, result={Result}.",
                press.PressId,
                scope.Generation,
                slotIdentity.Binding.HotbarId + 1,
                slotIdentity.Binding.SlotId + 1,
                macroProfile.ActionCount,
                initialBudget.ObservedActionCalls,
                scope.MacroLockObservedDuringExecution || macroLocked,
                macroLocked,
                result);
        }

        return;

        bool candidateIdentityChanged() =>
            !inputGenerations.IsCurrent(scope.Generation)
            || !inputSource.IsStillHeld(press)
            || Volatile.Read(ref latestCertifiedPressId) != press.PressId
            || !TryReadCurrentSlotIdentity(press, out var currentIdentity)
            || currentIdentity != slotIdentity
            || !TryReadSafeMacroProfile(slotIdentity, out var currentMacro, out _)
            || currentMacro.ContentFingerprint != macroProfile.ContentFingerprint
            || !macroSnapshot.Equals(currentSnapshot)
            || !IsSafeSnapshot(currentSnapshot)
            || !compatibility.IsLiveReActionProfileCurrent();
    }

    private void StartTurboRuntime(
        HotbarInputScope scope,
        CertifiedHotbarPress press,
        HotbarSlotIdentity slotIdentity,
        Candidate candidate,
        SafeActionMacroProfile? macroProfile,
        PhysicalHotbarInputSource inputSource)
    {
        if (macroProfile is not null && !IsMacroTargetProven(candidate))
        {
            LogTurboStartRejected(slotIdentity, "macro target was not resolved to an immutable explicit identity");
            return;
        }

        if (candidate.InputGeneration != scope.Generation
            || !inputSource.IsStillHeld(press)
            || !TryReadCurrentSlotIdentity(press, out var currentIdentity)
            || currentIdentity != slotIdentity
            || !compatibility.IsLiveReActionProfileCurrent()
            || !compatibility.IsLiveMOActionUnowned(candidate.RequestedActionId, candidate.ResolvedActionId)
            || !ExplicitTargetStillExists(candidate))
        {
            LogTurboStartRejected(slotIdentity, "runtime identity or compatibility changed");
            return;
        }

        if (macroProfile is { } expectedMacro
            && (!TryReadSafeMacroProfile(slotIdentity, out var currentMacro, out _)
                || currentMacro.ContentFingerprint != expectedMacro.ContentFingerprint))
        {
            LogTurboStartRejected(slotIdentity, "macro content changed during capture");
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            LogTurboStartRejected(slotIdentity, "native action manager was unavailable");
            return;
        }

        var options = CreateTurboOptions();
        if (turboEngine.Options != options)
        {
            CancelTurboUnsafe(HoldRepeatCancelReason.PluginChange, "Turbo timing changed");
            turboEngine = new HoldRepeatEngine(options);
        }

        // The certified edge proves that this newly pressed control was released
        // before the edge. That is sufficient to leave the fail-closed startup state,
        // even when a different, older key remains physically held.
        if (turboEngine.Snapshot.State == HoldRepeatState.NeedsRelease)
        {
            turboEngine.ObserveRelease();
        }

        var intentFingerprint = NonZeroFingerprint(
            (ulong)candidate.ActionType,
            candidate.RequestedActionId,
            candidate.TargetId,
            candidate.ExtraParam,
            candidate.ComboRouteId,
            candidate.Snapshot.TargetFingerprint,
            candidate.Snapshot.ContextFingerprint,
            macroProfile is { } macro
                ? Convert.ToUInt64(macro.ContentFingerprint[..16], 16)
                : 0);
        var request = new HoldRepeatStartRequest(
            press.PressId,
            scope.Generation,
            slotIdentity.ControlFingerprint,
            intentFingerprint,
            IsCertifiedFreshPress: true);
        var result = turboEngine.TryStart(request, NowMilliseconds);
        if (result is not (HoldRepeatStartResult.Started or HoldRepeatStartResult.Replaced))
        {
            return;
        }

        var runtime = new TurboRuntime(
            press,
            slotIdentity,
            candidate,
            macroProfile,
            compatibilitySignature,
            request,
            scope.ActionInvocationCount == 1);
        macroTurboRuntime = null;
        Interlocked.Exchange(ref macroTurboAcknowledgement, null);
        turboRuntime = runtime;
        if (scope.InitialAcknowledgement is { } initialAcknowledgement
            && !BeginInitialTurboAcknowledgement(runtime, initialAcknowledgement))
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.PulseRejected,
                "Turbo could not prove the original action acknowledgement barrier");
            return;
        }

        turboStartCount++;
        lastEvent = $"Turbo owns hotbar {slotIdentity.Binding.HotbarId + 1}, slot {slotIdentity.Binding.SlotId + 1}";
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Turbo start press={PressId}, generation={Generation}, hotbar={Hotbar}, slot={Slot}, slotType={SlotType}, type={Type}, action={Requested}->{Resolved}, result={Result}.",
                press.PressId,
                scope.Generation,
                slotIdentity.Binding.HotbarId + 1,
                slotIdentity.Binding.SlotId + 1,
                slotIdentity.CommandType,
                candidate.ActionType,
                candidate.RequestedActionId,
                candidate.ResolvedActionId,
                result);
        }
    }

    private void LogTurboStartRejected(HotbarSlotIdentity slotIdentity, string reason)
    {
        lastEvent = $"Turbo did not start for hotbar {slotIdentity.Binding.HotbarId + 1}, slot {slotIdentity.Binding.SlotId + 1}: {reason}";
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Turbo start rejected hotbar={Hotbar}, slot={Slot}, slotType={SlotType}, command={Command}: {Reason}.",
                slotIdentity.Binding.HotbarId + 1,
                slotIdentity.Binding.SlotId + 1,
                slotIdentity.CommandType,
                slotIdentity.CommandId,
                reason);
        }
    }

    private void ProcessTurbo(long now, long frameGap)
    {
        lock (dispatchGate)
        {
            var snapshot = turboEngine.Snapshot;
            if (!snapshot.HasActiveHold)
            {
                turboRuntime = null;
                macroTurboRuntime = null;
                Interlocked.Exchange(ref turboAcknowledgement, null);
                Interlocked.Exchange(ref macroTurboAcknowledgement, null);
                return;
            }

            if (frameGap < 0 || frameGap > MaximumFrameGapMilliseconds)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.InputLost,
                    $"Turbo cancelled after {frameGap} ms frame gap",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return;
            }

            if (macroTurboRuntime is { } macroRuntime)
            {
                ProcessMacroTurboUnsafe(macroRuntime, snapshot, now);
                return;
            }

            if (turboRuntime is not { } runtime)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.Fault,
                    "Turbo runtime token mismatch",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return;
            }

            var due = now >= snapshot.NextPulseAtMilliseconds;
            var observation = ObserveTurbo(runtime, checkLiveMOAction: due);
            var acknowledgement = Volatile.Read(ref turboAcknowledgement);
            var decision = turboEngine.Tick(
                now,
                observation.Safety,
                observation.ActionReady && acknowledgement is null);
            if (decision.Kind == HoldRepeatDecisionKind.Cancelled)
            {
                CancelTurboUnsafe(
                    decision.CancelReason,
                    $"Turbo cancelled: {decision.CancelReason}",
                    logTerminatedHold: true,
                    ownedQueuePolicy: QueuePolicyForHoldTermination(decision.CancelReason));
                return;
            }

            acknowledgement = Volatile.Read(ref turboAcknowledgement);
            if (acknowledgement is not null)
            {
                if (acknowledgement.StartedAtMilliseconds <= 0
                    || now - acknowledgement.StartedAtMilliseconds < 0
                    || now - acknowledgement.StartedAtMilliseconds > MaximumTurboAcknowledgementMilliseconds)
                {
                    if (!ReferenceEquals(
                            Interlocked.CompareExchange(ref turboAcknowledgement, null, acknowledgement),
                            acknowledgement))
                    {
                        return;
                    }

                    turboRejectedCount++;
                    CancelTurboUnsafe(
                        HoldRepeatCancelReason.PulseRejected,
                        "Turbo received no matching action-effect acknowledgement; hold ended without retry",
                        ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                }

                return;
            }

            if (decision.Kind != HoldRepeatDecisionKind.Pulse) return;
            DispatchTurboPulse(runtime, decision.Pulse);
        }
    }

    private void ProcessMacroTurboUnsafe(
        MacroTurboRuntime runtime,
        HoldRepeatSnapshot snapshot,
        long now)
    {
        if (!ReferenceEquals(macroTurboRuntime, runtime))
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.Fault,
                "Macro Turbo runtime token mismatch",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return;
        }

        var macroLocked = IsMacroExecutionActive();
        if (macroLocked)
        {
            if (!runtime.OwnsMacroExecutor)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.PluginChange,
                    "Macro Turbo observed a foreign native macro executor owner",
                    ownedQueuePolicy: runtime.InitialMacroLockCompleted
                        ? OwnedQueueCancelPolicy.ExactClear
                        : OwnedQueueCancelPolicy.Preserve);
                return;
            }

            runtime.InitialMacroLockObserved = true;
        }
        else if (runtime.OwnsMacroExecutor)
        {
            runtime.OwnsMacroExecutor = false;
            if (!runtime.InitialMacroLockCompleted)
            {
                if (!TryCompleteInitialMacroExecution(runtime)) return;
            }
            else if (runtime.ActiveExecutionBudget is not null
                && !TryCompleteMacroExecutionEpoch(
                    runtime,
                    runtime.ActiveExecutionEpoch,
                    "asynchronous native macro completion"))
            {
                return;
            }
        }

        if (!runtime.InitialMacroLockCompleted
            && (runtime.InitialMacroLockDeadlineMilliseconds <= 0
                || now < 0
                || now > runtime.InitialMacroLockDeadlineMilliseconds))
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.InputLost,
                "Macro Turbo never observed the initial native macro lock complete");
            return;
        }

        var acknowledgement = Volatile.Read(ref macroTurboAcknowledgement);
        var due = now >= snapshot.NextPulseAtMilliseconds;
        var observation = ObserveMacroTurbo(runtime, checkMacroHash: due);
        var decision = turboEngine.Tick(
            now,
            observation.Safety,
            observation.ActionReady && acknowledgement is null);
        if (decision.Kind == HoldRepeatDecisionKind.Cancelled)
        {
            CancelTurboUnsafe(
                decision.CancelReason,
                $"Macro Turbo cancelled: {decision.CancelReason}",
                logTerminatedHold: true,
                ownedQueuePolicy: QueuePolicyForHoldTermination(decision.CancelReason));
            return;
        }

        acknowledgement = Volatile.Read(ref macroTurboAcknowledgement);
        if (acknowledgement is not null)
        {
            if (!IsMacroTurboAcknowledgementCurrent(acknowledgement))
            {
                Interlocked.CompareExchange(
                    ref macroTurboAcknowledgement,
                    null,
                    acknowledgement);
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.PluginChange,
                    "Macro Turbo acknowledgement identity became stale",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return;
            }

            if (acknowledgement.StartedAtMilliseconds <= 0
                || now - acknowledgement.StartedAtMilliseconds < 0
                || now - acknowledgement.StartedAtMilliseconds > MaximumTurboAcknowledgementMilliseconds)
            {
                if (!ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref macroTurboAcknowledgement,
                            null,
                            acknowledgement),
                        acknowledgement))
                {
                    return;
                }

                turboRejectedCount++;
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.PulseRejected,
                    "Macro Turbo received no matching action-effect acknowledgement; hold ended without retry",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            }

            // Safety still ticks above, but an accepted action can never authorize
            // a later pulse until its exact server acknowledgement has arrived.
            return;
        }

        if (decision.Kind == HoldRepeatDecisionKind.Pulse)
        {
            DispatchMacroTurboPulse(runtime, decision.Pulse);
        }
    }

    private bool TryCompleteInitialMacroExecution(MacroTurboRuntime runtime)
    {
        if (runtime.InitialExecutionBudget is not { } budget)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.Fault,
                "Macro Turbo initial execution budget disappeared before native MacroLock ended");
            return false;
        }

        var completion = budget.Finish();
        runtime.InitialExecutionBudget = null;
        if (completion != MacroTurboExecutionBudgetResult.Complete)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.ResolvedActionChange,
                $"Macro Turbo initial action-call budget failed ({completion}, observed={budget.ObservedActionCalls}, max={budget.MaxActionCalls})");
            return false;
        }

        runtime.InitialMacroLockCompleted = true;
        return true;
    }

    private bool TryCompleteMacroExecutionEpoch(
        MacroTurboRuntime runtime,
        long epoch,
        string phase)
    {
        if (epoch <= 0
            || runtime.ActiveExecutionEpoch != epoch
            || runtime.ActiveExecutionBudget is not { } budget)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.Fault,
                $"Macro Turbo bounded execution epoch was missing at {phase}",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            return false;
        }

        var completion = budget.Finish();
        runtime.ActiveExecutionBudget = null;
        runtime.ActiveExecutionToken = null;
        runtime.ActiveExecutionEpoch = 0;
        if (completion == MacroTurboExecutionBudgetResult.Complete)
        {
            // A macro pulse may legally find none of its authored fallback lines
            // locally executable yet. That is a bounded no-op, not a server
            // rejection: keep the physical hold and wait for the next cadence.
            if (budget.AcceptedOutcomeCount == 1) turboAcceptedCount++;
            else turboRejectedCount++;
            if (configuration.DetailedLogging)
            {
                log.Information(
                    "Macro Turbo execution completed phase={Phase}, epoch={Epoch}, observedActions={ObservedActions}, acceptedOutcomes={AcceptedOutcomes}, maxActions={MaxActions}.",
                    phase,
                    epoch,
                    budget.ObservedActionCalls,
                    budget.AcceptedOutcomeCount,
                    budget.MaxActionCalls);
            }

            return true;
        }

        CancelTurboUnsafe(
            HoldRepeatCancelReason.ResolvedActionChange,
            $"Macro Turbo bounded execution ended {completion} at {phase} (observed={budget.ObservedActionCalls}, accepted={budget.AcceptedOutcomeCount}, max={budget.MaxActionCalls})",
            ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
        return false;
    }

    private void DispatchMacroTurboPulse(
        MacroTurboRuntime runtime,
        HoldRepeatPulseToken token)
    {
        lock (dispatchGate)
        {
            if (!turboEngine.IsTokenCurrent(token)
                || !ReferenceEquals(macroTurboRuntime, runtime)
                || activeMacroPulseExecution is not null
                || engine.Pending is not null)
            {
                return;
            }

            var observation = ObserveMacroTurbo(runtime, checkMacroHash: true);
            if (!IsTurboSafetySafe(observation.Safety))
            {
                CancelTurboUnsafe(
                    GetTurboCancellationReason(observation.Safety),
                    "Macro Turbo final safety check failed",
                    ownedQueuePolicy: QueuePolicyForHoldTermination(
                        GetTurboCancellationReason(observation.Safety)));
                return;
            }

            if (!observation.ActionReady
                || observation.HotbarModule == null)
            {
                return;
            }

            if (runtime.NextExecutionEpoch == long.MaxValue)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.Fault,
                    "Macro Turbo bounded execution epoch was exhausted",
                    ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
                return;
            }

            byte result;
            var executionEpoch = ++runtime.NextExecutionEpoch;
            runtime.ActiveExecutionEpoch = executionEpoch;
            var executionBudget = new MacroTurboExecutionBudget(runtime.MacroProfile.ActionCount);
            runtime.ActiveExecutionBudget = executionBudget;
            runtime.ActiveExecutionToken = token;
            activeMacroPulseExecution = new MacroPulseExecutionScope(runtime, token, executionEpoch);
            runtime.OwnsMacroExecutor = true;
            turboDispatching = true;
            hotbarExecutionDepth++;
            try
            {
                // Same-control Macro Turbo intentionally repeats the certified
                // native slot. The macro executor and FFXIV select the first
                // executable action line; PulseQueue never selects an action or
                // target from the macro itself.
                result = executeSlotByIdHook.Original(
                    observation.HotbarModule,
                    runtime.SlotIdentity.Binding.HotbarId,
                    runtime.SlotIdentity.Binding.SlotId);
            }
            finally
            {
                hotbarExecutionDepth--;
                turboDispatching = false;
                activeMacroPulseExecution = null;
                runtime.OwnsMacroExecutor = ReferenceEquals(macroTurboRuntime, runtime)
                    && IsMacroExecutionActive();
                if (ReferenceEquals(macroTurboRuntime, runtime)
                    && !runtime.OwnsMacroExecutor)
                {
                    TryCompleteMacroExecutionEpoch(
                        runtime,
                        executionEpoch,
                        "synchronous slot return");
                }
            }

            turboPulseCount++;
            if (!ReferenceEquals(macroTurboRuntime, runtime)) return;

            lastEvent = $"Macro Turbo pulsed hotbar {runtime.SlotIdentity.Binding.HotbarId + 1}, slot {runtime.SlotIdentity.Binding.SlotId + 1}";
            if (configuration.DetailedLogging)
            {
                log.Information(
                    "Turbo macro pulse ordinal={Ordinal}, hotbar={Hotbar}, slot={Slot}, result={Result}, observedActions={ObservedActions}, acceptedOutcomes={AcceptedOutcomes}, maxActions={MaxActions}, macroLocked={MacroLocked}.",
                    token.Ordinal,
                    runtime.SlotIdentity.Binding.HotbarId + 1,
                    runtime.SlotIdentity.Binding.SlotId + 1,
                    result,
                    executionBudget.ObservedActionCalls,
                    executionBudget.AcceptedOutcomeCount,
                    executionBudget.MaxActionCalls,
                    IsMacroExecutionActive());
            }
        }
    }

    private MacroTurboObservation ObserveMacroTurbo(
        MacroTurboRuntime runtime,
        bool checkMacroHash)
    {
        var inputSource = physicalHotbarInput;
        var stableInputContext = !disposed
            && clientState.IsLoggedIn
            && !IsBetweenAreas
            && clientState.TerritoryType == runtime.Snapshot.TerritoryId;
        var physicalControlDown = stableInputContext
            && inputSource?.IsStillHeld(runtime.Press) == true;
        var macroProfileMatches = !checkMacroHash
            || TryReadSafeMacroProfile(runtime.SlotIdentity, out var currentMacroProfile, out _)
                && currentMacroProfile.ContentFingerprint == runtime.MacroProfile.ContentFingerprint;
        var actionManager = ActionManager.Instance();
        var bindingMatches = Volatile.Read(ref latestCertifiedPressId) == runtime.Press.PressId
            && TryReadCurrentSlotIdentity(runtime.Press, out var currentIdentity)
            && currentIdentity == runtime.SlotIdentity;
        var currentSnapshot = stableInputContext
            ? CaptureSnapshot(0, 0, includeResolverTargets: true)
            : null;
        var targetMatches = currentSnapshot is { } targetSnapshot
            && targetSnapshot.TargetFingerprint == runtime.Snapshot.TargetFingerprint;
        var territoryMatches = currentSnapshot is { } territorySnapshot
            && territorySnapshot.TerritoryId == runtime.Snapshot.TerritoryId;
        var instanceMatches = currentSnapshot is { } instanceSnapshot
            && instanceSnapshot.ContextFingerprint == runtime.Snapshot.ContextFingerprint
            && instanceSnapshot.LocalGameObjectId == runtime.Snapshot.LocalGameObjectId
            && instanceSnapshot.LocalAddress == runtime.Snapshot.LocalAddress;
        var pluginStateMatches = string.Equals(
                compatibilitySignature,
                runtime.CompatibilitySignature,
                StringComparison.Ordinal)
            && compatibility.IsLiveReActionProfileCurrent();
        var local = objectTable.LocalPlayer;
        var knockbackActive = condition[ConditionFlag.BeingMoved]
            || Volatile.Read(ref forcedMovementObserved) != 0;
        var turboEnabled = configuration.Enabled
            && configuration.TurboEnabled
            && configuration.TurboMacrosEnabled
            && !configuration.DryRun
            && (configuration.TurboOutOfCombat || condition[ConditionFlag.InCombat]);
        var safety = new HoldRepeatSafetyState(
            Enabled: turboEnabled && !disposed,
            ConflictDetected: activeConflicts.Count > 0 || compatibilityQuarantineFrames > 0,
            LoggedIn: clientState.IsLoggedIn && local is not null && !IsBetweenAreas,
            IsAlive: local is { IsDead: false } && !condition[ConditionFlag.Unconscious],
            IsMounted: condition[ConditionFlag.Mounted],
            IsStunned: IsStunned(local),
            IsKnockbackActive: knockbackActive,
            PhysicalControlDown: physicalControlDown,
            ReleaseObserved: !physicalControlDown,
            TerritoryMatches: territoryMatches,
            InstanceMatches: instanceMatches,
            TargetMatches: targetMatches,
            ResolvedActionMatches: macroProfileMatches,
            BindingMatches: bindingMatches,
            PluginStateMatches: pluginStateMatches,
            Faulted: faulted);
        var hotbarModule = bindingMatches ? RaptureHotbarModule.Instance() : null;
        var actionReady = IsTurboSafetySafe(safety)
            && runtime.InitialMacroLockCompleted
            && runtime.ActiveExecutionBudget is null
            && runtime.ActiveExecutionEpoch == 0
            && engine.Pending is null
            && actionManager != null
            && hotbarModule != null
            && !IsMacroExecutionActive()
            && !actionManager->ActionQueued
            && actionManager->AnimationLock <= AnimationLockEpsilonSeconds;
        return new MacroTurboObservation(hotbarModule, safety, actionReady);
    }

    private void DispatchTurboPulse(TurboRuntime runtime, HoldRepeatPulseToken token)
    {
        lock (dispatchGate)
        {
            if (!turboEngine.IsTokenCurrent(token)
                || !ReferenceEquals(turboRuntime, runtime)
                || engine.Pending is not null)
            {
                return;
            }

            var observation = ObserveTurbo(runtime, checkLiveMOAction: true);
            if (!IsTurboSafetySafe(observation.Safety))
            {
                CancelTurboUnsafe(
                    GetTurboCancellationReason(observation.Safety),
                    "Turbo final safety check failed",
                    ownedQueuePolicy: QueuePolicyForHoldTermination(
                        GetTurboCancellationReason(observation.Safety)));
                return;
            }

            if (!observation.ActionReady
                || observation.ActionManager == null
                || observation.HotbarModule == null)
            {
                return;
            }

            var execution = new DirectPulseExecutionScope(runtime, token, observation.ResolvedActionId);
            byte result;
            activeDirectPulseExecution = execution;
            turboDispatching = true;
            hotbarExecutionDepth++;
            try
            {
                // Repeat the exact certified native slot once. Its base command
                // remains immutable while FFXIV may legitimately adjust that
                // same command to a new combo/transformed action between pulses.
                result = executeSlotByIdHook.Original(
                    observation.HotbarModule,
                    runtime.SlotIdentity.Binding.HotbarId,
                    runtime.SlotIdentity.Binding.SlotId);
            }
            finally
            {
                hotbarExecutionDepth--;
                turboDispatching = false;
                activeDirectPulseExecution = null;
            }

            turboPulseCount++;
            if (!ReferenceEquals(turboRuntime, runtime)) return;
            if (execution.InvocationCount != 1
                || !execution.Completed
                || !execution.Accepted
                || execution.ExactTuple is not { } exactTuple)
            {
                RejectTurboPulseUnsafe(
                    $"Turbo same-slot pulse produced no single exact accepted action (calls={execution.InvocationCount}, completed={execution.Completed}, accepted={execution.Accepted})");
                return;
            }

            turboAcceptedCount++;

            lastEvent = $"Turbo pulsed hotbar {runtime.SlotIdentity.Binding.HotbarId + 1}, slot {runtime.SlotIdentity.Binding.SlotId + 1}";
            if (configuration.DetailedLogging)
            {
                log.Information(
                    "Turbo pulse ordinal={Ordinal}, source={Source}, hotbar={Hotbar}, slot={Slot}, action={Action}, result={Result}, sequence={Before}->{After}, queued={Queued}.",
                    token.Ordinal,
                    runtime.IsMacro ? "captured-macro-action" : "direct-hotbar-slot",
                    runtime.SlotIdentity.Binding.HotbarId + 1,
                    runtime.SlotIdentity.Binding.SlotId + 1,
                    exactTuple.ResolvedActionId,
                    result,
                    execution.SequenceBefore,
                    execution.SequenceAfter,
                    execution.QueueAfter.IsQueued);
            }
        }
    }

    private TurboObservation ObserveTurbo(TurboRuntime runtime, bool checkLiveMOAction)
    {
        var inputSource = physicalHotbarInput;
        var stableInputContext = !disposed
            && clientState.IsLoggedIn
            && !IsBetweenAreas
            && clientState.TerritoryType == runtime.Candidate.Snapshot.TerritoryId;
        var physicalControlDown = stableInputContext
            && inputSource?.IsStillHeld(runtime.Press) == true;
        var macroProfileMatches = runtime.MacroProfile is null
            || !checkLiveMOAction
            || TryReadSafeMacroProfile(runtime.SlotIdentity, out var currentMacroProfile, out _)
                && currentMacroProfile.ContentFingerprint == runtime.MacroProfile.Value.ContentFingerprint;
        var bindingMatches = Volatile.Read(ref latestCertifiedPressId) == runtime.Press.PressId
            && TryReadCurrentSlotIdentity(runtime.Press, out var currentIdentity)
            && currentIdentity == runtime.SlotIdentity
            && macroProfileMatches;
        var actionManager = ActionManager.Instance();
        var resolvedActionId = actionManager == null
            ? 0
            : actionManager->GetAdjustedActionId(runtime.Candidate.RequestedActionId);
        var profileMatches = resolvedActionId != 0
            && TryGetEligibleActionProfile(
                runtime.Candidate.ActionType,
                resolvedActionId,
                runtime.Candidate.TargetId,
                out var includeResolverTargets)
            && includeResolverTargets == runtime.Candidate.IncludeResolverTargets;
        var currentSnapshot = resolvedActionId == 0
            ? null
            : CaptureSnapshot(
                runtime.Candidate.TargetId,
                resolvedActionId,
                runtime.Candidate.IncludeResolverTargets);
        var targetMatches = currentSnapshot is { } observedSnapshot
            && observedSnapshot.TargetFingerprint == runtime.Candidate.Snapshot.TargetFingerprint
            && ExplicitTargetStillExists(runtime.Candidate);
        var territoryMatches = currentSnapshot is { } territorySnapshot
            && territorySnapshot.TerritoryId == runtime.Candidate.Snapshot.TerritoryId;
        var instanceMatches = currentSnapshot is { } instanceSnapshot
            && instanceSnapshot.ContextFingerprint == runtime.Candidate.Snapshot.ContextFingerprint;
        var pluginStateMatches = string.Equals(
                compatibilitySignature,
                runtime.CompatibilitySignature,
                StringComparison.Ordinal)
            && compatibility.IsLiveReActionProfileCurrent()
            && (!checkLiveMOAction
                || compatibility.IsLiveMOActionUnowned(
                    runtime.Candidate.RequestedActionId,
                    resolvedActionId));
        var local = objectTable.LocalPlayer;
        var knockbackActive = condition[ConditionFlag.BeingMoved]
            || Volatile.Read(ref forcedMovementObserved) != 0;
        var turboEnabled = configuration.Enabled
            && configuration.TurboEnabled
            && (!runtime.IsMacro || configuration.TurboMacrosEnabled)
            && !configuration.DryRun
            && (configuration.TurboOutOfCombat || condition[ConditionFlag.InCombat]);
        var safety = new HoldRepeatSafetyState(
            Enabled: turboEnabled && !disposed,
            ConflictDetected: activeConflicts.Count > 0 || compatibilityQuarantineFrames > 0,
            LoggedIn: clientState.IsLoggedIn && local is not null && !IsBetweenAreas,
            IsAlive: local is { IsDead: false } && !condition[ConditionFlag.Unconscious],
            IsMounted: condition[ConditionFlag.Mounted],
            IsStunned: IsStunned(local),
            IsKnockbackActive: knockbackActive,
            PhysicalControlDown: physicalControlDown,
            ReleaseObserved: !physicalControlDown,
            TerritoryMatches: territoryMatches,
            InstanceMatches: instanceMatches,
            TargetMatches: targetMatches,
            ResolvedActionMatches: profileMatches,
            BindingMatches: bindingMatches,
            PluginStateMatches: pluginStateMatches,
            Faulted: faulted);
        var actionReady = IsTurboSafetySafe(safety)
            && engine.Pending is null
            && actionManager != null
            && (!runtime.IsMacro || !IsMacroExecutionActive())
            && !actionManager->ActionQueued
            && actionManager->AnimationLock <= AnimationLockEpsilonSeconds
            && actionManager->IsActionOffCooldown(runtime.Candidate.ActionType, resolvedActionId)
            && actionManager->GetActionStatus(
                runtime.Candidate.ActionType,
                resolvedActionId,
                runtime.Candidate.TargetId,
                true,
                true) == 0;
        return new TurboObservation(
            actionManager,
            bindingMatches ? RaptureHotbarModule.Instance() : null,
            resolvedActionId,
            safety,
            actionReady);
    }

    private static bool IsTurboSafetySafe(HoldRepeatSafetyState safety) =>
        safety.Enabled
        && !safety.ConflictDetected
        && safety.LoggedIn
        && safety.IsAlive
        && !safety.IsMounted
        && !safety.IsStunned
        && !safety.IsKnockbackActive
        && safety.PhysicalControlDown
        && !safety.ReleaseObserved
        && safety.TerritoryMatches
        && safety.InstanceMatches
        && safety.TargetMatches
        && safety.ResolvedActionMatches
        && safety.BindingMatches
        && safety.PluginStateMatches
        && !safety.Faulted;

    private static HoldRepeatCancelReason GetTurboCancellationReason(HoldRepeatSafetyState safety)
    {
        if (safety.ReleaseObserved || !safety.PhysicalControlDown) return HoldRepeatCancelReason.Released;
        if (safety.Faulted) return HoldRepeatCancelReason.Fault;
        if (!safety.Enabled) return HoldRepeatCancelReason.Disabled;
        if (safety.ConflictDetected) return HoldRepeatCancelReason.Conflict;
        if (!safety.LoggedIn) return HoldRepeatCancelReason.Logout;
        if (!safety.IsAlive) return HoldRepeatCancelReason.Death;
        if (safety.IsMounted) return HoldRepeatCancelReason.Mounted;
        if (safety.IsStunned) return HoldRepeatCancelReason.Stun;
        if (safety.IsKnockbackActive) return HoldRepeatCancelReason.Knockback;
        if (!safety.PluginStateMatches) return HoldRepeatCancelReason.PluginChange;
        if (!safety.TerritoryMatches) return HoldRepeatCancelReason.TerritoryChange;
        if (!safety.InstanceMatches) return HoldRepeatCancelReason.InstanceChange;
        if (!safety.TargetMatches) return HoldRepeatCancelReason.TargetChange;
        if (!safety.ResolvedActionMatches) return HoldRepeatCancelReason.ResolvedActionChange;
        if (!safety.BindingMatches) return HoldRepeatCancelReason.BindingChange;
        return HoldRepeatCancelReason.Fault;
    }

    private bool TryReadCurrentSlotIdentity(
        CertifiedHotbarPress press,
        out HotbarSlotIdentity identity)
    {
        identity = default;
        var hotbarModule = RaptureHotbarModule.Instance();
        if (hotbarModule == null) return false;
        var slot = hotbarModule->GetSlotById(press.Binding.HotbarId, press.Binding.SlotId);
        var captured = CaptureHotbarSlotIdentity(press, slot);
        if (captured is not { } value) return false;
        identity = value;
        return true;
    }

    private static HotbarSlotIdentity? CaptureHotbarSlotIdentity(
        CertifiedHotbarPress? press,
        RaptureHotbarModule.HotbarSlot* slot)
    {
        if (press is not { } certified || slot == null) return null;
        var commandType = (uint)slot->CommandType;
        var commandId = slot->CommandId;
        if (commandType is not (DirectActionHotbarSlotType or MacroHotbarSlotType)
            || commandType == DirectActionHotbarSlotType && commandId == 0)
        {
            return null;
        }

        var controlFingerprint = NonZeroFingerprint(
            (ulong)(int)certified.Binding.InputId,
            (ulong)(int)certified.PhysicalKey,
            (ulong)certified.RequiredModifiers,
            (ulong)certified.ActiveModifiers,
            certified.KeySettingIndex,
            certified.Binding.HotbarId,
            certified.Binding.SlotId,
            commandType,
            commandId);
        return new HotbarSlotIdentity(
            certified.Binding,
            commandType,
            commandId,
            controlFingerprint);
    }

    private static bool TryReadSafeMacroProfile(
        HotbarSlotIdentity identity,
        out SafeActionMacroProfile profile,
        out MacroSafetyFailure failure)
    {
        profile = default;
        failure = MacroSafetyFailure.Empty;
        if (identity.CommandType != MacroHotbarSlotType
            || !TryDecodeMacroCommandId(identity.CommandId, out var macroSet, out var macroIndex))
        {
            failure = MacroSafetyFailure.UnsupportedCommand;
            return false;
        }

        var macroModule = RaptureMacroModule.Instance();
        if (macroModule == null) return false;
        var macro = macroModule->GetMacro(macroSet, macroIndex);
        if (macro == null || !macro->IsNotEmpty()) return false;

        var lines = macro->Lines;
        var text = new string?[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            text[index] = lines[index].ToString();
        }

        var analysis = MacroSafetyAnalyzer.Analyze(text);
        profile = analysis.Profile;
        failure = analysis.Failure;
        return analysis.IsSafe;
    }

    private static bool TryDecodeMacroCommandId(
        uint commandId,
        out uint macroSet,
        out uint macroIndex)
    {
        macroSet = 0;
        macroIndex = 0;
        if (commandId < 100)
        {
            macroIndex = commandId;
            return true;
        }

        if (commandId is >= 256 and < 356)
        {
            macroSet = 1;
            macroIndex = commandId - 256;
            return true;
        }

        return false;
    }

    private HoldRepeatOptions CreateTurboOptions() => new HoldRepeatOptions(
        configuration.TurboInitialDelayMs,
        configuration.TurboRepeatIntervalMs,
        HoldRepeatOptions.AbsoluteMaximumHoldMilliseconds).Normalize();

    private bool TryReplaceOwnedNativeQueue(
        ActionManager* actionManager,
        long replacingGeneration,
        string phase)
    {
        if (actionManager == null || !CanReplaceOwnedQueuesForNewestInput()) return false;
        var current = CaptureNativeQueue(actionManager);
        // ReAction may temporarily hide an older queue around its outer hook and
        // restore it only after PulseQueue's inner UseAction returns. Preserve
        // ownership across that empty interval; the full hotbar completion check
        // sees the stable post-hook state.
        if (!current.IsQueued) return false;
        var replacedExactOwner = nativeQueueOwnership.TryTakeForNewerInput(
                replacingGeneration,
                actionManager->LastUsedActionSequence,
                current,
                out var replaced);
        SynchronizeOwnedNativeQueueSafetyContext();
        if (!replacedExactOwner)
        {
            return false;
        }

        // This is the sole native queue mutation in PulseQueue. Ownership proves
        // that the exact queue entry came from an older certified hotbar input;
        // a foreign or changed queue can never reach this write.
        actionManager->ActionQueued = false;
        ownedNativeQueueReplacementCount++;
        lastEvent = $"Newest input replaced owned native queue action {replaced.ActionId}";
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Replaced owned native queue action={Action}, generation={Generation}, phase={Phase}.",
                replaced.ActionId,
                replacingGeneration,
                phase);
        }

        return true;
    }

    private void RequestExactOwnedNativeQueueSafetyClear(string phase)
    {
        latestCertifiedQueueReplacementGeneration = 0;
        RequestExactOwnedNativeQueueSafetyClearThrough(
            inputGenerations.Current,
            phase);
    }

    private void RequestExactOwnedNativeQueueSafetyClearThrough(
        long maximumGeneration,
        string phase)
    {
        if (maximumGeneration <= 0) return;
        // Keep a tombstone even when the in-flight Original has not claimed its
        // queue yet. A terminal event may race with outcome classification, and
        // an untouched asynchronous vanilla Macro may emit its later line only
        // after the next framework boundary.
        ownedNativeQueueSafetyClearPending = true;
        ownedNativeQueueSafetyClearThroughGeneration = Math.Max(
            ownedNativeQueueSafetyClearThroughGeneration,
            maximumGeneration);

        var actionManager = ActionManager.Instance();
        if (actionManager != null)
        {
            RetryExactOwnedNativeQueueSafetyClear(actionManager, phase);
        }
    }

    private bool RetryExactOwnedNativeQueueSafetyClear(
        ActionManager* actionManager,
        string phase)
    {
        if (configuration.DryRun)
        {
            AbandonOwnedQueueProvenanceForDetectOnly();
            return false;
        }

        if (!ownedNativeQueueSafetyClearPending
            || ownedNativeQueueSafetyClearThroughGeneration <= 0
            || actionManager == null)
        {
            return false;
        }
        if (!nativeQueueOwnership.HasOwnership)
        {
            // Keep it ready for an in-flight or asynchronous original outcome
            // whose owned generation is at or below the terminal cutoff.
            return false;
        }

        var current = CaptureNativeQueue(actionManager);
        if (!current.IsQueued)
        {
            // Preserve proof across the short interval in which an outer hook
            // can hide ActionQueued. The next stable framework boundary either
            // clears the restored exact entry or reconciles a truly empty queue.
            return false;
        }

        var clearedExactOwner = nativeQueueOwnership.TryTakeExactCurrent(
                ownedNativeQueueSafetyClearThroughGeneration,
                actionManager->LastUsedActionSequence,
                current,
                out var cleared);
        SynchronizeOwnedNativeQueueSafetyContext();
        if (!clearedExactOwner)
        {
            // A foreign visible queue may revoke older ownership while an
            // already in-flight pre-cancellation Original has not returned yet.
            // Keep the generation-bounded terminal intent for an older outcome.
            return false;
        }

        actionManager->ActionQueued = false;
        ownedNativeQueueSafetyClearCount++;
        lastEvent = $"Safety cancellation cleared owned native queue action {cleared.ActionId}";
        if (configuration.DetailedLogging)
        {
            log.Information(
                "Cleared owned native queue action={Action} throughGeneration={Generation} after terminal safety cancellation phase={Phase}.",
                cleared.ActionId,
                ownedNativeQueueSafetyClearThroughGeneration,
                phase);
        }

        return true;
    }

    private bool TryClaimOwnedNativeQueue(
        long generation,
        ushort sequenceMarker,
        NativeQueueSnapshot before,
        NativeQueueSnapshot after,
        ExactActionTuple attempted,
        OwnedNativeQueueSafetySeed safetySeed,
        ActionManager* actionManager,
        string phase)
    {
        if (!configuration.Enabled
            || configuration.DryRun
            || faulted
            || disposed
            || reActionSmartActionTransformActive
            || activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0)
        {
            return false;
        }

        var claimed = nativeQueueOwnership.TryClaimNewQueue(
            generation,
            sequenceMarker,
            before,
            after,
            attempted);
        if (!claimed)
        {
            SynchronizeOwnedNativeQueueSafetyContext();
            return false;
        }

        // Publish the semantic provenance before any already-armed terminal
        // tombstone or re-entrant cancellation can inspect the new exact owner.
        ownedNativeQueueSafetyContext = new OwnedNativeQueueSafetyContext(
            generation,
            attempted,
            safetySeed.RootSnapshot,
            safetySeed.InvocationSnapshot,
            safetySeed.IncludeResolverTargets,
            safetySeed.ExplicitTargetAddress);

        if (ownedNativeQueueSafetyClearPending)
        {
            RetryExactOwnedNativeQueueSafetyClear(
                actionManager,
                $"{phase}; applying an already-armed terminal cutoff");
            if (!nativeQueueOwnership.HasOwnership) return false;
        }

        return EnforceOwnedNativeQueueSafety(
            actionManager,
            frameGap: null,
            phase);
    }

    private bool TryBeginOwnedNativeQueueDrain(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        ExactActionTuple attempted,
        out NativeQueueDrainLease lease)
    {
        var leased = nativeQueueOwnership.TryBeginExactDrain(
            generation,
            sequenceMarker,
            current,
            attempted,
            out lease);
        SynchronizeOwnedNativeQueueSafetyContext();
        return leased;
    }

    private void ProcessOwnedNativeQueueDrainOutcome(
        ActionManager* actionManager,
        NativeQueueDrainAttempt attempt,
        ushort currentSequence,
        string phase)
    {
        var currentQueue = CaptureNativeQueue(actionManager);
        var result = nativeQueueOwnership.CompleteExactDrain(
            attempt.Lease,
            currentSequence,
            currentQueue);
        SynchronizeOwnedNativeQueueSafetyContext();

        if (result != NativeQueueDrainFinalizeResult.OwnershipRetained)
        {
            if (attempt.MacroRuntime is { } macroRuntime)
            {
                macroRuntime.OwnedQueueTuple = null;
            }
            if (attempt.DirectRuntime is { } directRuntime)
            {
                directRuntime.OwnedQueueTuple = null;
            }
        }

        // Terminal/newer-input exact takes deliberately decline while a lease
        // is in flight. Retry them immediately after the native/outer hook has
        // either consumed or restored the queue.
        if (actionManager != null)
        {
            if (ownedNativeQueueSafetyClearPending)
            {
                RetryExactOwnedNativeQueueSafetyClear(
                    actionManager,
                    $"{phase}; after exact drain lease finalization");
            }
            if (latestCertifiedQueueReplacementGeneration > attempt.Generation)
            {
                TryReplaceOwnedNativeQueue(
                    actionManager,
                    latestCertifiedQueueReplacementGeneration,
                    $"{phase}; after exact drain lease finalization");
            }
            if (ownedNativeQueueSafetyContext is { Generation: var ownedGeneration }
                && ownedGeneration == attempt.Generation)
            {
                EnforceOwnedNativeQueueSafety(
                    actionManager,
                    frameGap: null,
                    $"{phase}; after exact drain lease finalization");
            }
        }

        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Finalized exact native queue drain lease generation={Generation}, result={Result}, phase={Phase}.",
                attempt.Generation,
                result,
                phase);
        }
    }

    private void ReconcileOwnedNativeQueue(
        uint sequenceMarker,
        NativeQueueSnapshot current)
    {
        nativeQueueOwnership.Reconcile(sequenceMarker, current);
        SynchronizeOwnedNativeQueueSafetyContext();
    }

    private void SynchronizeOwnedNativeQueueSafetyContext()
    {
        if (!nativeQueueOwnership.HasOwnership)
        {
            ownedNativeQueueSafetyContext = null;
        }
    }

    private bool EnforceOwnedNativeQueueSafety(
        ActionManager* actionManager,
        long? frameGap,
        string phase)
    {
        if (!nativeQueueOwnership.HasOwnership)
        {
            ownedNativeQueueSafetyContext = null;
            return true;
        }

        if (ownedNativeQueueSafetyContext is not { } context)
        {
            // This is an internal invariant failure. Consume only the exact
            // visible owner and fail closed; never write the native flag from
            // sidecar state alone.
            RequestExactOwnedNativeQueueSafetyClearThrough(
                inputGenerations.Current,
                $"{phase}; exact owner had no semantic safety provenance");
            RetryExactOwnedNativeQueueSafetyClear(
                actionManager,
                $"{phase}; exact owner had no semantic safety provenance");
            return false;
        }

        var current = CaptureSnapshot(
            context.Attempted.TargetId,
            context.Attempted.ResolvedActionId,
            context.IncludeResolverTargets);
        var reason = GetOwnedNativeQueueSafetyFailure(
            context,
            current,
            frameGap,
            out var detail);
        if (reason == CancelReason.None) return true;

        // A semantic mismatch belongs to this owner generation. Do not widen
        // the cutoff to a newer input merely because the older queue was hidden
        // while that newer generation began.
        RequestExactOwnedNativeQueueSafetyClearThrough(
            context.Generation,
            $"{phase}; {detail}");
        RetryExactOwnedNativeQueueSafetyClear(
            actionManager,
            $"{phase}; {detail}");

        if (inputGenerations.Current == context.Generation)
        {
            // The invalid owner is also the current input generation, so its
            // pending/hold scheduling must terminate with the same hard rule.
            Cancel(reason, $"Owned native queue safety failed: {detail}");
        }
        else
        {
            lastEvent = $"Cleared stale owned native queue: {detail}";
        }

        return false;
    }

    private CancelReason GetOwnedNativeQueueSafetyFailure(
        OwnedNativeQueueSafetyContext context,
        Snapshot current,
        long? frameGap,
        out string detail)
    {
        detail = string.Empty;
        if (!configuration.Enabled || faulted || disposed)
        {
            detail = "PulseQueue is disabled or faulted";
            return CancelReason.Disabled;
        }
        if (activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || !compatibility.IsLiveReActionProfileCurrent()
            || !compatibility.IsLiveMOActionUnowned(
                context.Attempted.RequestedActionId,
                context.Attempted.ResolvedActionId))
        {
            detail = "plugin compatibility ownership changed";
            return CancelReason.Conflict;
        }
        if (!clientState.IsLoggedIn
            || IsBetweenAreas
            || objectTable.LocalPlayer is null
            || current.LocalGameObjectId == 0
            || current.LocalAddress == nint.Zero)
        {
            detail = "login or local-player identity is unavailable";
            return CancelReason.Logout;
        }
        if (objectTable.LocalPlayer is { IsDead: true }
            || condition[ConditionFlag.Unconscious])
        {
            detail = "the local player died or became unconscious";
            return CancelReason.Death;
        }
        if (current.IsMounted || context.InvocationSnapshot.IsMounted)
        {
            detail = "mounted state became active";
            return CancelReason.Mounted;
        }
        if (current.IsStunned || context.InvocationSnapshot.IsStunned)
        {
            detail = "stun became active";
            return CancelReason.Stun;
        }
        if (current.IsBeingMoved || context.InvocationSnapshot.IsBeingMoved)
        {
            detail = "forced movement became active";
            return CancelReason.Knockback;
        }
        if (frameGap is < 0 or > MaximumFrameGapMilliseconds)
        {
            detail = $"framework gap was {frameGap} ms";
            return CancelReason.Expired;
        }

        var root = context.RootSnapshot;
        var invocation = context.InvocationSnapshot;
        if (root.TerritoryId != invocation.TerritoryId
            || root.TerritoryId != current.TerritoryId)
        {
            detail = "territory changed";
            return CancelReason.TerritoryChange;
        }
        if (root.ContextFingerprint != invocation.ContextFingerprint
            || root.ContextFingerprint != current.ContextFingerprint
            || root.LocalGameObjectId != invocation.LocalGameObjectId
            || root.LocalGameObjectId != current.LocalGameObjectId
            || root.LocalAddress != invocation.LocalAddress
            || root.LocalAddress != current.LocalAddress)
        {
            detail = "instance, map, job, PvP, or local-player identity changed";
            return CancelReason.InstanceChange;
        }
        if (root.HardTargetId != invocation.HardTargetId
            || root.HardTargetId != current.HardTargetId
            || root.SoftTargetId != invocation.SoftTargetId
            || root.SoftTargetId != current.SoftTargetId
            || context.IncludeResolverTargets
                && (root.MouseOverTargetId != invocation.MouseOverTargetId
                    || root.MouseOverTargetId != current.MouseOverTargetId
                    || root.MouseOverNameplateTargetId != invocation.MouseOverNameplateTargetId
                    || root.MouseOverNameplateTargetId != current.MouseOverNameplateTargetId))
        {
            detail = "target or resolver identity changed";
            return CancelReason.TargetChange;
        }
        if (context.ExplicitTargetAddress != nint.Zero
            && FindTargetAddress(context.Attempted.TargetId) != context.ExplicitTargetAddress)
        {
            detail = "the explicit target disappeared or was replaced";
            return CancelReason.TargetChange;
        }

        return CancelReason.None;
    }

    private Snapshot? CaptureDirectSnapshotAtPress(HotbarSlotIdentity slotIdentity)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null || slotIdentity.CommandId == 0) return null;
        var resolvedActionId = actionManager->GetAdjustedActionId(slotIdentity.CommandId);
        return resolvedActionId == 0
            ? null
            : CaptureSnapshot(0, resolvedActionId, includeResolverTargets: true);
    }

    private bool TryCreateDirectTurboCandidate(
        HotbarInputScope scope,
        HotbarSlotIdentity slotIdentity,
        out Candidate? candidate,
        out string failure)
    {
        candidate = null;
        failure = "direct slot action identity was unavailable";
        var actionManager = ActionManager.Instance();
        if (actionManager == null
            || slotIdentity.CommandId == 0
            || scope.DirectSnapshotAtPress is not { } pressSnapshot)
        {
            return false;
        }

        var resolvedActionId = actionManager->GetAdjustedActionId(slotIdentity.CommandId);
        if (resolvedActionId == 0
            || !TryGetEligibleActionProfile(
                ActionType.Action,
                resolvedActionId,
                0,
                out var includeResolverTargets))
        {
            failure = DescribeDirectTurboIneligibility(
                slotIdentity,
                "direct slot action is outside the instant non-ground non-movement Turbo scope");
            return false;
        }

        if (excludedIntegrationActionIds.Contains(slotIdentity.CommandId)
            || excludedIntegrationActionIds.Contains(resolvedActionId)
            || !compatibility.IsLiveMOActionUnowned(slotIdentity.CommandId, resolvedActionId))
        {
            failure = "direct slot action is owned by MOAction or another audited integration";
            return false;
        }

        var snapshot = pressSnapshot with
        {
            MouseOverTargetId = includeResolverTargets ? pressSnapshot.MouseOverTargetId : 0,
            MouseOverNameplateTargetId = includeResolverTargets ? pressSnapshot.MouseOverNameplateTargetId : 0,
            TargetFingerprint = Fingerprint(
                0,
                pressSnapshot.HardTargetId,
                pressSnapshot.SoftTargetId,
                includeResolverTargets ? pressSnapshot.MouseOverTargetId : 0,
                includeResolverTargets ? pressSnapshot.MouseOverNameplateTargetId : 0),
            ResolvedActionId = resolvedActionId,
        };
        if (!IsSafeSnapshot(snapshot))
        {
            failure = "direct slot press context was unavailable or unsafe";
            return false;
        }

        candidate = new Candidate(
            ActionType.Action,
            slotIdentity.CommandId,
            resolvedActionId,
            0,
            0,
            0,
            actionManager->LastUsedActionSequence,
            NowMilliseconds,
            scope.Generation,
            includeResolverTargets,
            snapshot,
            new ExactActionTuple(
                (uint)ActionType.Action,
                slotIdentity.CommandId,
                resolvedActionId,
                0,
                0,
                (uint)ActionManager.UseActionMode.None,
                0),
            CaptureNativeQueue(actionManager));
        failure = string.Empty;
        return true;
    }

    private string DescribeDirectTurboIneligibility(
        HotbarSlotIdentity slotIdentity,
        string fallback)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null || slotIdentity.CommandId == 0) return fallback;
        var resolvedActionId = actionManager->GetAdjustedActionId(slotIdentity.CommandId);
        if (resolvedActionId == 0) return fallback;
        if (ActionManager.GetAdjustedCastTime(ActionType.Action, resolvedActionId) > 0)
        {
            return $"cast-time action {resolvedActionId} is currently unsupported by Turbo";
        }

        var action = dataManager.GetExcelSheet<GameAction>()?.GetRow(resolvedActionId);
        if (action is not { } actionRow || actionRow.RowId != resolvedActionId) return fallback;
        if (actionRow.TargetArea) return $"ground-target action {resolvedActionId} is unsupported by Turbo";
        if (actionRow.AffectsPosition || resolvedActionId == ReActionCameraRelativeMovementException)
        {
            return $"movement action {resolvedActionId} is unsupported by Turbo";
        }

        return fallback;
    }

    private Candidate? TryCreateCandidate(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        ActionManager.UseActionMode requiredMode = ActionManager.UseActionMode.None)
    {
        if (disposed
            || faulted
            || !configuration.Enabled
            || reActionSmartActionTransformActive
            || actionManager == null
            || mode != requiredMode)
        {
            return null;
        }

        // Compatibility discovery walks foreign plugin assemblies and may invoke an IPC
        // provider. Keep that work off the latency-sensitive per-press path; topology
        // changes invalidate immediately and the framework refreshes the immutable
        // snapshot on a bounded poll.
        RefreshConflicts();
        if (!compatibility.IsLiveReActionProfileCurrent())
        {
            MarkCompatibilityProfileDirty("ReAction safety settings changed");
            return null;
        }

        if (activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || actionType is not (ActionType.Action or ActionType.PvPAction)
            || actionId == 0)
        {
            return null;
        }

        var local = objectTable.LocalPlayer;
        if (local is null || !clientState.IsLoggedIn)
        {
            return null;
        }

        var resolvedId = actionManager->GetAdjustedActionId(actionId);
        if (resolvedId == 0)
        {
            return null;
        }

        if (excludedIntegrationActionIds.Contains(actionId)
            || excludedIntegrationActionIds.Contains(resolvedId))
        {
            integrationExclusionCount++;
            lastEvent = $"Action {resolvedId} is owned by MOAction targeting and was not buffered";
            return null;
        }

        if (!TryGetEligibleActionProfile(actionType, resolvedId, targetId, out var includeResolverTargets))
        {
            return null;
        }

        var snapshot = CaptureSnapshot(targetId, resolvedId, includeResolverTargets);
        if (!IsSafeSnapshot(snapshot))
        {
            return null;
        }

        var candidate = new Candidate(
            actionType,
            actionId,
            resolvedId,
            targetId,
            extraParam,
            comboRouteId,
            actionManager->LastUsedActionSequence,
            NowMilliseconds,
            inputGenerations.Current,
            includeResolverTargets,
            snapshot,
            new ExactActionTuple(
                (uint)actionType,
                actionId,
                resolvedId,
                targetId,
                extraParam,
                (uint)mode,
                comboRouteId),
            CaptureNativeQueue(actionManager))
        {
            ExplicitTargetAddress = FindTargetAddress(targetId),
        };

        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Eligible input generation={Generation}, type={Type}, action={Base}->{Resolved}, nativeQueueBefore={Queued}.",
                candidate.InputGeneration,
                candidate.ActionType,
                candidate.RequestedActionId,
                candidate.ResolvedActionId,
                candidate.QueueAtCapture.IsQueued);
        }

        return candidate;
    }

    private void ProcessOriginalOutcome(
        ActionManager* actionManager,
        Candidate candidate,
        bool originalResult,
        ushort currentSequence,
        bool* outOptAreaTargeted)
    {
        var sequenceAdvanced = currentSequence != candidate.SequenceAtCapture;
        if (sequenceAdvanced)
        {
            RecordSentSequence(currentSequence, NowMilliseconds);
        }

        var areaTargetingStarted = outOptAreaTargeted != null && *outOptAreaTargeted;
        if (actionManager == null)
        {
            lastEvent = "ActionManager unavailable after the original input";
            return;
        }

        var queueAfter = CaptureNativeQueue(actionManager);
        var nativeOutcome = NativeActionOutcomeClassifier.Classify(
            originalResult || sequenceAdvanced || areaTargetingStarted,
            candidate.QueueAtCapture,
            queueAfter,
            candidate.ExactTuple);
        var exactQueueClaimed = nativeOutcome == NativeActionOutcome.MatchingNewQueue
            && !sequenceAdvanced
            && TryClaimOwnedNativeQueue(
                candidate.InputGeneration,
                currentSequence,
                candidate.QueueAtCapture,
                queueAfter,
                candidate.ExactTuple,
                new OwnedNativeQueueSafetySeed(
                    candidate.Snapshot,
                    candidate.Snapshot,
                    candidate.IncludeResolverTargets,
                    candidate.ExplicitTargetAddress),
                actionManager,
                "after an original hotbar queue outcome");
        if (ownedNativeQueueSafetyClearPending)
        {
            RetryExactOwnedNativeQueueSafetyClear(
                actionManager,
                "after an original hotbar outcome");
        }

        if (latestCertifiedQueueReplacementGeneration > candidate.InputGeneration)
        {
            TryReplaceOwnedNativeQueue(
                actionManager,
                latestCertifiedQueueReplacementGeneration,
                "after an original outcome superseded by a newer certified root");
        }

        if (!inputGenerations.IsCurrent(candidate.InputGeneration)) return;

        RecordInitialTurboOutcome(
            candidate,
            nativeOutcome,
            sequenceAdvanced,
            currentSequence,
            exactQueueClaimed,
            allowQueuedOutcome: true);
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Original outcome generation={Generation}, action={Action}, outcome={Outcome}, result={Result}, sequenceAdvanced={SequenceAdvanced}, queueAfter={Queued}.",
                candidate.InputGeneration,
                candidate.ResolvedActionId,
                nativeOutcome,
                originalResult,
                sequenceAdvanced,
                queueAfter.IsQueued);
        }

        if (nativeOutcome is NativeActionOutcome.ImmediateAcceptance or NativeActionOutcome.MatchingNewQueue)
        {
            if (nativeOutcome == NativeActionOutcome.MatchingNewQueue)
            {
                nativeQueueAcceptedCount++;
                lastEvent = $"Native queue accepted action {candidate.ResolvedActionId}";
            }
            else
            {
                lastEvent = "The original input was accepted immediately";
            }

            return;
        }

        if (nativeOutcome == NativeActionOutcome.ForeignOrPreexistingQueue)
        {
            nativeQueueBlockedCount++;
            lastEvent = $"Existing native queue blocked action {candidate.ResolvedActionId}; PulseQueue did not overwrite it";
            return;
        }

        if (disposed || faulted || !configuration.Enabled || !inputGenerations.IsCurrent(candidate.InputGeneration))
        {
            return;
        }

        if (!compatibility.IsLiveReActionProfileCurrent())
        {
            MarkCompatibilityProfileDirty("ReAction safety settings changed");
            return;
        }

        if (!compatibility.IsLiveMOActionUnowned(
                candidate.RequestedActionId,
                candidate.ResolvedActionId))
        {
            MarkCompatibilityProfileDirty("MOAction ownership changed");
            return;
        }

        RefreshConflicts();
        var currentSnapshot = CaptureSnapshot(
            candidate.TargetId,
            actionManager->GetAdjustedActionId(candidate.RequestedActionId),
            candidate.IncludeResolverTargets);
        if (activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || !candidate.Snapshot.Equals(currentSnapshot)
            || !IsSafeSnapshot(currentSnapshot))
        {
            return;
        }

        var structuralStatus = actionManager->GetActionStatus(
            candidate.ActionType,
            candidate.ResolvedActionId,
            candidate.TargetId,
            false,
            false);
        if (structuralStatus != 0)
        {
            lastEvent = $"Rejected as non-temporal (status {structuralStatus})";
            return;
        }

        var remainingMilliseconds = GetTemporalRemainingMilliseconds(
            actionManager,
            candidate.ActionType,
            candidate.ResolvedActionId);
        var holdWindow = CurrentHoldWindowMilliseconds;
        if (!double.IsFinite(remainingMilliseconds)
            || remainingMilliseconds <= 0
            || remainingMilliseconds >= holdWindow
            || remainingMilliseconds >= BufferEngine.AbsoluteHoldCapMilliseconds)
        {
            lastEvent = $"Temporal remainder {remainingMilliseconds:0.0} ms outside {holdWindow} ms window";
            return;
        }

        var actionRequest = new ActionRequest(
            candidate.RequestedActionId,
            candidate.ResolvedActionId,
            candidate.Snapshot.TargetFingerprint,
            candidate.Snapshot.TerritoryId,
            candidate.Snapshot.ContextFingerprint);
        var failure = actionManager->AnimationLock > AnimationLockEpsilonSeconds
            ? ActionFailureKind.AnimationLock
            : ActionFailureKind.Cooldown;
        var intent = new BufferIntent(actionRequest, failure, IsEligibleForBuffering: true);

        if (!engine.Arm(intent, candidate.CapturedAtMilliseconds, holdWindow))
        {
            return;
        }

        pendingRuntimeAction = new RuntimeAction(
            candidate,
            actionRequest,
            remainingMilliseconds,
            SaturatingAdd(candidate.CapturedAtMilliseconds, holdWindow));
        capturedCount++;
        lastEvent = $"Buffered action {candidate.ResolvedActionId} ({remainingMilliseconds:0} ms early)";
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Buffered {Type} {Base}->{Resolved} remainder={Remaining:0.0}ms window={Window}ms.",
                candidate.ActionType,
                candidate.RequestedActionId,
                candidate.ResolvedActionId,
                remainingMilliseconds,
                holdWindow);
        }
    }

    private void RecordInitialTurboOutcome(
        Candidate candidate,
        NativeActionOutcome outcome,
        bool sequenceAdvanced,
        ushort currentSequence,
        bool exactQueueClaimed,
        bool allowQueuedOutcome)
    {
        TurboAcknowledgementSeed? seed = outcome switch
        {
            NativeActionOutcome.ImmediateAcceptance when sequenceAdvanced && currentSequence != 0 =>
                CreateTurboAcknowledgementSeed(
                    candidate,
                    TurboAcknowledgementSequenceMode.ImmediateExact,
                    currentSequence),
            NativeActionOutcome.MatchingNewQueue
                when allowQueuedOutcome
                    && !sequenceAdvanced
                    && exactQueueClaimed
                    && currentSequence != 0 =>
                CreateTurboAcknowledgementSeed(
                    candidate,
                    TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                    currentSequence),
            _ => null,
        };

        var disqualified = outcome is NativeActionOutcome.ForeignOrPreexistingQueue
            || outcome == NativeActionOutcome.ImmediateAcceptance && seed is null
            || outcome == NativeActionOutcome.MatchingNewQueue && seed is null;
        ApplyTurboCaptureOutcome(candidate, seed, disqualified);
    }

    private static TurboAcknowledgementSeed CreateTurboAcknowledgementSeed(
        Candidate candidate,
        TurboAcknowledgementSequenceMode sequenceMode,
        ushort sequenceMarker) => new(
            new TurboActionEffectExpectation(
                (uint)candidate.ActionType,
                candidate.RequestedActionId,
                candidate.ResolvedActionId,
                sequenceMode,
                sequenceMarker),
            NowMilliseconds);

    private void ApplyTurboCaptureOutcome(
        Candidate candidate,
        TurboAcknowledgementSeed? seed,
        bool disqualified)
    {
        if (activeHotbarInput is { } scope
            && scope.Generation == candidate.InputGeneration
            && scope.TurboCandidate?.ExactTuple == candidate.ExactTuple)
        {
            scope.InitialAcknowledgement = seed;
            scope.TurboDisqualified |= disqualified;
        }

    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
    {
        // Serialize the topology transition with the last native dispatch boundary.
        // Whichever side acquires the gate first has a single, auditable ordering.
        lock (dispatchGate)
        {
            Cancel(CancelReason.Conflict, "Plugin topology changed");
            Interlocked.Exchange(ref pluginTopologyDirty, 1);
        }
    }

    private void MarkCompatibilityProfileDirty(string detail)
    {
        Cancel(CancelReason.Conflict, $"{detail}; reassessment scheduled");
        Interlocked.Exchange(ref pluginTopologyDirty, 1);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed) return;

        var now = NowMilliseconds;
        try
        {
            if (Interlocked.Exchange(ref pluginTopologyDirty, 0) != 0)
            {
                Cancel(CancelReason.Conflict, "Plugin topology changed; waiting for one clean frame");
                sentSequences.Clear();
                observedResponseTimes.Clear();
                recentLocalActionEffects.Clear();
                latency.Reset();
                nextCompatibilityRefreshAt = 0;
                compatibilityQuarantineFrames = Math.Max(compatibilityQuarantineFrames, 1);
            }

            var frameGap = now - lastFrameworkAt;
            lastFrameworkAt = now;
            Volatile.Write(ref localEntityId, objectTable.LocalPlayer?.EntityId ?? 0);
            ObserveNativeInputContextTransitions();
            if (frameGap < 0 || frameGap > MaximumFrameGapMilliseconds)
            {
                Cancel(CancelReason.Expired, $"Cancelled native input ownership after {frameGap} ms frame gap");
                return;
            }

            if (Interlocked.Exchange(ref forcedMovementObserved, 0) != 0)
            {
                Cancel(CancelReason.Knockback, "Cleared by a local-player knockback action effect");
            }

            RefreshConflicts();
            RemoveStaleSequenceMarkers(now);
            RemoveStaleActionEffects(now);
            DrainTimingSamples();
            DrainTimingHookErrors();

            lock (dispatchGate)
            {
                ReconcileSyntheticMacroExecutorQuarantine(now);
                ReconcileRetiredPhysicalMacroExecutor(now);
                ReconcileNativeMacroRepeatTail(now, observedUnlockedBoundary: false);
            }

            var observedActionManager = ActionManager.Instance();
            if (observedActionManager != null)
            {
                lock (dispatchGate)
                {
                    ResolveLogicalRepeatQueuePending(
                        observedActionManager,
                        now,
                        "at the stable framework boundary",
                        stableBoundary: true);
                    if (latestLogicalRepeatQueueReplacementGeneration > 0)
                    {
                        TryReplaceLogicalRepeatNativeQueue(
                            observedActionManager,
                            latestLogicalRepeatQueueReplacementGeneration,
                            "at the stable framework boundary after a newer input");
                    }

                    if (latestCertifiedQueueReplacementGeneration > 0)
                    {
                        TryReplaceOwnedNativeQueue(
                            observedActionManager,
                            latestCertifiedQueueReplacementGeneration,
                            "at the stable framework boundary after a certified hotbar root");
                    }

                    RetryExactOwnedNativeQueueSafetyClear(
                        observedActionManager,
                        "at the stable framework boundary after terminal cancellation");
                    ReconcileOwnedNativeQueue(
                        observedActionManager->LastUsedActionSequence,
                        CaptureNativeQueue(observedActionManager));
                    logicalRepeatQueueOwnership.Reconcile(
                        observedActionManager->LastUsedActionSequence,
                        CaptureNativeQueue(observedActionManager));
                    EnforceOwnedNativeQueueSafety(
                        observedActionManager,
                        frameGap,
                        "at the stable framework boundary");
                }
            }
            else
            {
                lock (dispatchGate)
                {
                    // There is no native queue instance whose exact identity can
                    // survive logout/manager teardown. Abandon provenance without
                    // writing native state so it can never cross into a new login.
                    ReconcileOwnedNativeQueue(0, NativeQueueSnapshot.Empty);
                    logicalRepeatQueueOwnership.Clear();
                    logicalRepeatQueuePending = null;
                }
            }

            if (compatibilityQuarantineFrames > 0)
            {
                compatibilityQuarantineFrames--;
                if (engine.Pending is not null || turboEngine.Snapshot.HasActiveHold)
                {
                    Cancel(CancelReason.Conflict, "Compatibility state is settling for one clean frame");
                }

                return;
            }

            if (engine.Pending is null)
            {
                pendingRuntimeAction = null;
                return;
            }

            if (pendingRuntimeAction is not { } runtime)
            {
                Fault(new InvalidOperationException("Core token exists without its immutable runtime tuple."), "Runtime token mismatch");
                return;
            }

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                Cancel(CancelReason.Explicit, "ActionManager unavailable");
                return;
            }

            var snapshot = CaptureSnapshot(
                runtime.Candidate.TargetId,
                actionManager->GetAdjustedActionId(runtime.Candidate.RequestedActionId),
                runtime.Candidate.IncludeResolverTargets);
            var safety = ToCoreSafety(runtime.ActionRequest, snapshot);

            var safetyDecision = engine.Evaluate(new BufferContext(safety, ActionIsExecutable: false), now);
            if (safetyDecision.Kind is BufferDecisionKind.Cancelled or BufferDecisionKind.Expired)
            {
                pendingRuntimeAction = null;
                lastEvent = $"Cancelled: {safetyDecision.Reason}";
                return;
            }

            if (actionManager->ActionQueued
                || actionManager->LastUsedActionSequence != runtime.Candidate.SequenceAtCapture)
            {
                Cancel(CancelReason.Replaced, "Native queue or action sequence changed");
                return;
            }

            if (!runtime.Candidate.Snapshot.Equals(snapshot)
                || !ExplicitTargetStillExists(runtime.Candidate))
            {
                Cancel(CancelReason.TargetChange, "Target identity or resolver context changed");
                return;
            }

            var structuralStatus = actionManager->GetActionStatus(
                runtime.Candidate.ActionType,
                runtime.Candidate.ResolvedActionId,
                runtime.Candidate.TargetId,
                false,
                false);
            if (structuralStatus != 0)
            {
                Cancel(CancelReason.NonTransientFailure, $"Action became structurally invalid ({structuralStatus})");
                return;
            }

            var fullStatus = actionManager->GetActionStatus(
                runtime.Candidate.ActionType,
                runtime.Candidate.ResolvedActionId,
                runtime.Candidate.TargetId,
                true,
                true);
            var executable = fullStatus == 0
                && actionManager->AnimationLock <= AnimationLockEpsilonSeconds
                && actionManager->IsActionOffCooldown(runtime.Candidate.ActionType, runtime.Candidate.ResolvedActionId)
                && !actionManager->ActionQueued;

            var decision = engine.Evaluate(new BufferContext(safety, executable), now);
            if (decision.Kind != BufferDecisionKind.Dispatch)
            {
                if (decision.Kind is BufferDecisionKind.Cancelled or BufferDecisionKind.Expired)
                {
                    pendingRuntimeAction = null;
                    lastEvent = $"Cancelled: {decision.Reason}";
                }

                return;
            }

            // The engine has already consumed the token. From this point every exit is terminal.
            pendingRuntimeAction = null;
            if (configuration.DryRun)
            {
                dryRunDispatchCount++;
                lastEvent = $"Dry run: would dispatch {runtime.Candidate.ResolvedActionId}";
                return;
            }

            DispatchOnce(actionManager, runtime);
        }
        catch (Exception exception)
        {
            Fault(exception, "Framework validation failed");
        }
    }

    private void DispatchOnce(ActionManager* actionManager, RuntimeAction runtime)
    {
        lock (dispatchGate)
        {
            if (!IsStrictlyReady(actionManager, runtime))
            {
                lastEvent = "Final dispatch check cancelled the consumed token";
                return;
            }

            replaying = true;
            try
            {
                // The candidate generation is checked again under the dispatch gate.
                // A later input or cancellation can therefore never revive this token.
                if (!inputGenerations.IsCurrent(runtime.Candidate.InputGeneration))
                {
                    lastEvent = "Final generation check cancelled the consumed token";
                    return;
                }

                if (NowMilliseconds >= runtime.ExpiresAtMilliseconds)
                {
                    lastEvent = "Final deadline check expired the consumed token";
                    return;
                }

                var sequenceBefore = actionManager->LastUsedActionSequence;
                var queueBefore = CaptureNativeQueue(actionManager);
                var areaTargeted = false;
                var accepted = useActionHook.Original(
                    actionManager,
                    runtime.Candidate.ActionType,
                    runtime.Candidate.RequestedActionId,
                    runtime.Candidate.TargetId,
                    runtime.Candidate.ExtraParam,
                    ActionManager.UseActionMode.Queue,
                    runtime.Candidate.ComboRouteId,
                    &areaTargeted);
                var sequenceAfter = actionManager->LastUsedActionSequence;
                var queueAfter = CaptureNativeQueue(actionManager);
                var replayTuple = runtime.Candidate.ExactTuple with
                {
                    Mode = (uint)ActionManager.UseActionMode.Queue,
                };
                var sequenceAdvanced = sequenceAfter != sequenceBefore;
                var nativeOutcome = NativeActionOutcomeClassifier.Classify(
                    accepted || sequenceAdvanced,
                    queueBefore,
                    queueAfter,
                    replayTuple);

                dispatchedCount++;
                if (nativeOutcome == NativeActionOutcome.ImmediateAcceptance && sequenceAdvanced)
                {
                    RecordSentSequence(sequenceAfter, NowMilliseconds);
                    lastEvent = $"Dispatched action {runtime.Candidate.ResolvedActionId} once";
                    if (!BeginOneShotTurboAcknowledgement(
                            runtime.Candidate,
                            TurboAcknowledgementSequenceMode.ImmediateExact,
                            sequenceAfter,
                            replayTuple))
                    {
                        CancelMatchingTurboAfterOneShot(
                            runtime.Candidate,
                            "One-shot send had no provable Turbo acknowledgement barrier");
                    }
                }
                else if (nativeOutcome == NativeActionOutcome.MatchingNewQueue && !sequenceAdvanced)
                {
                    var claimed = TryClaimOwnedNativeQueue(
                        runtime.Candidate.InputGeneration,
                        sequenceAfter,
                        queueBefore,
                        queueAfter,
                        replayTuple,
                        new OwnedNativeQueueSafetySeed(
                            runtime.Candidate.Snapshot,
                            runtime.Candidate.Snapshot,
                            runtime.Candidate.IncludeResolverTargets,
                            runtime.Candidate.ExplicitTargetAddress),
                        actionManager,
                        "after a one-shot replay queue outcome");
                    lastEvent = $"Replay queued action {runtime.Candidate.ResolvedActionId} once";
                    if (!claimed
                        || !BeginOneShotTurboAcknowledgement(
                            runtime.Candidate,
                            TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                            sequenceBefore,
                            replayTuple))
                    {
                        CancelMatchingTurboAfterOneShot(
                            runtime.Candidate,
                            "One-shot queue had no provable Turbo acknowledgement barrier");
                    }
                }
                else if (nativeOutcome == NativeActionOutcome.Rejected)
                {
                    replayRejectedCount++;
                    lastEvent = $"One-shot replay rejected for {runtime.Candidate.ResolvedActionId}; no retry";
                    CancelMatchingTurboAfterOneShot(
                        runtime.Candidate,
                        "One-shot replay was rejected; held input cannot retry it");
                }
                else
                {
                    replayRejectedCount++;
                    lastEvent = $"One-shot replay outcome {nativeOutcome} was unproven; no retry";
                    CancelMatchingTurboAfterOneShot(
                        runtime.Candidate,
                        "One-shot replay outcome was not an exact send or newly owned queue");
                }

                if (configuration.DetailedLogging)
                {
                    log.Debug(
                        "One-shot replay {Result}: {Type} {Base}->{Resolved}, sequence {Before}->{After}.",
                        accepted,
                        runtime.Candidate.ActionType,
                        runtime.Candidate.RequestedActionId,
                        runtime.Candidate.ResolvedActionId,
                        sequenceBefore,
                        sequenceAfter);
                }
            }
            finally
            {
                replaying = false;
            }
        }
    }

    private bool BeginOneShotTurboAcknowledgement(
        Candidate candidate,
        TurboAcknowledgementSequenceMode sequenceMode,
        ushort sequenceMarker,
        ExactActionTuple exactTuple)
    {
        var runtime = turboRuntime;
        if (runtime is null
            || !turboEngine.Snapshot.HasActiveHold
            || runtime.Candidate.InputGeneration != candidate.InputGeneration)
        {
            return true;
        }

        var expectation = new TurboActionEffectExpectation(
            exactTuple.ActionType,
            exactTuple.RequestedActionId,
            exactTuple.ResolvedActionId,
            sequenceMode,
            sequenceMarker);
        return BeginTurboAcknowledgement(
            runtime,
            pulse: null,
            expectation,
            NowMilliseconds);
    }

    private void CancelMatchingTurboAfterOneShot(Candidate candidate, string detail)
    {
        if (turboRuntime is { } runtime
            && runtime.Candidate.InputGeneration == candidate.InputGeneration)
        {
            CancelTurboUnsafe(
                HoldRepeatCancelReason.PulseRejected,
                detail,
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
        }
    }

    private bool IsStrictlyReady(ActionManager* actionManager, RuntimeAction runtime)
    {
        if (disposed
            || faulted
            || !configuration.Enabled
            || Volatile.Read(ref forcedMovementObserved) != 0
            || NowMilliseconds >= runtime.ExpiresAtMilliseconds
            || !inputGenerations.IsCurrent(runtime.Candidate.InputGeneration)) return false;
        if (!compatibility.IsLiveReActionProfileCurrent())
        {
            MarkCompatibilityProfileDirty("ReAction safety settings changed");
            return false;
        }

        if (!compatibility.IsLiveMOActionUnowned(
                runtime.Candidate.RequestedActionId,
                runtime.Candidate.ResolvedActionId))
        {
            MarkCompatibilityProfileDirty("MOAction ownership changed");
            return false;
        }

        RefreshConflicts();
        if (activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || actionManager == null
            || actionManager->ActionQueued) return false;
        if (actionManager->LastUsedActionSequence != runtime.Candidate.SequenceAtCapture) return false;
        if (actionManager->AnimationLock > AnimationLockEpsilonSeconds) return false;
        if (!actionManager->IsActionOffCooldown(runtime.Candidate.ActionType, runtime.Candidate.ResolvedActionId)) return false;
        if (actionManager->GetAdjustedActionId(runtime.Candidate.RequestedActionId) != runtime.Candidate.ResolvedActionId) return false;
        if (actionManager->GetActionStatus(
                runtime.Candidate.ActionType,
                runtime.Candidate.ResolvedActionId,
                runtime.Candidate.TargetId,
                true,
                true) != 0) return false;

        var snapshot = CaptureSnapshot(
            runtime.Candidate.TargetId,
            runtime.Candidate.ResolvedActionId,
            runtime.Candidate.IncludeResolverTargets);
        return runtime.Candidate.Snapshot.Equals(snapshot)
            && IsSafeSnapshot(snapshot)
            && ExplicitTargetStillExists(runtime.Candidate)
            && inputGenerations.IsCurrent(runtime.Candidate.InputGeneration);
    }

    private void ReceiveActionEffectDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        try
        {
            var currentLocalEntityId = Volatile.Read(ref localEntityId);
            if (header == null || currentLocalEntityId == 0)
            {
                return;
            }

            if (header->SourceSequence != 0
                && casterEntityId == currentLocalEntityId
                && sentSequences.TryRemove(header->SourceSequence, out var sentAt))
            {
                var elapsed = NowMilliseconds - sentAt;
                if (elapsed is > 0 and <= MaximumTimingSampleAgeMilliseconds)
                {
                    observedResponseTimes.Enqueue(elapsed);
                }
            }

            if (header->SourceSequence != 0 && casterEntityId == currentLocalEntityId)
            {
                recentLocalActionEffects.Enqueue(new TimedTurboActionEffect(
                    new TurboActionEffectObservation(
                        header->ActionType,
                        header->ActionId,
                        header->SourceSequence),
                    NowMilliseconds));
                TryCompleteTurboAcknowledgement(header);
            }

            if (effects != null && targetEntityIds != null)
            {
                var targetCount = Math.Min((int)header->NumTargets, MaximumActionEffectTargets);
                for (var index = 0; index < targetCount; index++)
                {
                    if (targetEntityIds[index].ObjectId != currentLocalEntityId
                        || !ContainsActionEffectType(&effects[index], KnockbackActionEffectType))
                    {
                        continue;
                    }

                    lock (dispatchGate)
                    {
                        Cancel(CancelReason.Knockback, "Cleared by a local-player knockback action effect");
                        Interlocked.Exchange(ref forcedMovementObserved, 1);
                    }
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            timingHookErrors.Enqueue(exception);
        }
        finally
        {
            receiveActionEffectHook.Original(
                casterEntityId,
                casterPtr,
                targetPosition,
                header,
                effects,
                targetEntityIds);
        }
    }

    private Snapshot CaptureSnapshot(
        ulong explicitTargetId,
        uint resolvedActionId,
        bool includeResolverTargets)
    {
        var local = objectTable.LocalPlayer;
        var hardTarget = targetManager.Target?.GameObjectId ?? 0;
        var softTarget = targetManager.SoftTarget?.GameObjectId ?? 0;
        var mouseOverTarget = includeResolverTargets ? targetManager.MouseOverTarget?.GameObjectId ?? 0 : 0;
        var mouseOverNameplateTarget = includeResolverTargets
            ? targetManager.MouseOverNameplateTarget?.GameObjectId ?? 0
            : 0;
        var jobId = local?.ClassJob.RowId ?? 0;
        var targetFingerprint = Fingerprint(
            explicitTargetId,
            hardTarget,
            softTarget,
            mouseOverTarget,
            mouseOverNameplateTarget);
        var contextFingerprint = Fingerprint(
            clientState.MapId,
            clientState.Instance,
            jobId,
            clientState.IsPvP ? 1UL : 0UL);

        return new Snapshot(
            clientState.TerritoryType,
            clientState.MapId,
            clientState.Instance,
            jobId,
            clientState.IsPvP,
            local?.GameObjectId ?? 0,
            local?.Address ?? nint.Zero,
            hardTarget,
            softTarget,
            mouseOverTarget,
            mouseOverNameplateTarget,
            targetFingerprint,
            contextFingerprint,
            resolvedActionId,
            condition[ConditionFlag.Mounted],
            IsStunned(local),
            condition[ConditionFlag.BeingMoved]);
    }

    private BufferSafetyState ToCoreSafety(ActionRequest request, Snapshot snapshot) => new(
        Enabled: configuration.Enabled && !faulted && !disposed,
        ConflictDetected: activeConflicts.Count > 0 || compatibilityQuarantineFrames > 0,
        LoggedIn: clientState.IsLoggedIn && objectTable.LocalPlayer is not null && !IsBetweenAreas,
        IsAlive: objectTable.LocalPlayer is { IsDead: false } && !condition[ConditionFlag.Unconscious],
        IsMounted: snapshot.IsMounted,
        IsStunned: snapshot.IsStunned,
        IsKnockbackActive: snapshot.IsBeingMoved,
        TerritoryId: snapshot.TerritoryId,
        InstanceId: snapshot.ContextFingerprint,
        TargetId: snapshot.TargetFingerprint,
        ResolvedActionId: snapshot.ResolvedActionId);

    private bool IsSafeSnapshot(Snapshot snapshot) =>
        configuration.Enabled
        && !faulted
        && !disposed
        && activeConflicts.Count == 0
        && compatibilityQuarantineFrames == 0
        && clientState.IsLoggedIn
        && snapshot.LocalGameObjectId != 0
        && snapshot.LocalAddress != nint.Zero
        && objectTable.LocalPlayer is { IsDead: false }
        && !condition[ConditionFlag.Unconscious]
        && !snapshot.IsMounted
        && !snapshot.IsStunned
        && !snapshot.IsBeingMoved
        && !IsBetweenAreas;

    private bool ExplicitTargetStillExists(Candidate candidate)
    {
        if (candidate.TargetId is 0 or InvalidObjectId || candidate.TargetId == candidate.Snapshot.LocalGameObjectId)
        {
            return true;
        }

        foreach (var gameObject in objectTable)
        {
            if (gameObject.GameObjectId == candidate.TargetId)
            {
                return gameObject.Address == candidate.ExplicitTargetAddress;
            }
        }

        return false;
    }

    private bool TryGetEligibleActionProfile(
        ActionType actionType,
        uint resolvedActionId,
        ulong targetId,
        out bool includeResolverTargets)
    {
        includeResolverTargets = false;
        if (ActionManager.GetAdjustedCastTime(actionType, resolvedActionId) > 0) return false;
        var sheet = dataManager.GetExcelSheet<GameAction>();
        if (sheet is null) return false;
        var action = sheet.GetRow(resolvedActionId);
        // Movement actions are deliberately excluded: a camera-relative dash can change
        // direction between the press and a later replay even when its action/target tuple
        // is otherwise identical.
        if (action.RowId != resolvedActionId
            || action.TargetArea
            || action.AffectsPosition
            || resolvedActionId == ReActionCameraRelativeMovementException) return false;

        includeResolverTargets = targetId is 0 or InvalidObjectId
            && (action.CanTargetAlliance
                || action.CanTargetAlly
                || action.CanTargetHostile
                || action.CanTargetOwnPet
                || action.CanTargetParty
                || action.CanTargetPartyPet);
        return true;
    }

    private double GetTemporalRemainingMilliseconds(
        ActionManager* actionManager,
        ActionType actionType,
        uint resolvedActionId)
    {
        var animationLock = Math.Max(0, actionManager->AnimationLock * 1000.0);
        var cooldown = 0.0;
        if (!actionManager->IsActionOffCooldown(actionType, resolvedActionId))
        {
            var total = actionManager->GetRecastTime(actionType, resolvedActionId);
            var elapsed = actionManager->GetRecastTimeElapsed(actionType, resolvedActionId);
            var spellId = ActionManager.GetSpellIdForAction(actionType, resolvedActionId);
            var level = (uint)(objectTable.LocalPlayer?.Level ?? 100);
            var maximumCharges = Math.Max(1, (int)ActionManager.GetMaxCharges(spellId, level));
            cooldown = CooldownTiming.GetNextChargeRemainingMilliseconds(
                total,
                elapsed,
                maximumCharges);
        }

        return Math.Max(animationLock, cooldown);
    }

    private static NativeQueueSnapshot CaptureNativeQueue(ActionManager* actionManager)
    {
        if (actionManager == null) return NativeQueueSnapshot.Empty;
        return new NativeQueueSnapshot(
            actionManager->ActionQueued,
            (uint)actionManager->QueuedActionType,
            actionManager->QueuedActionId,
            (ulong)actionManager->QueuedTargetId,
            actionManager->QueuedExtraParam,
            (uint)actionManager->QueueType,
            actionManager->QueuedComboRouteId);
    }

    private int CurrentHoldWindowMilliseconds
    {
        get
        {
            if (latency.AcceptedSampleCount < 5) return BufferEngine.AbsoluteHoldCapMilliseconds;
            return Math.Clamp(
                (int)Math.Round(latency.SuggestedHold.TotalMilliseconds, MidpointRounding.AwayFromZero),
                80,
                BufferEngine.AbsoluteHoldCapMilliseconds);
        }
    }

    private NativeInputContext CaptureNativeInputContext()
    {
        var local = objectTable.LocalPlayer;
        return new NativeInputContext(
            clientState.IsLoggedIn,
            IsBetweenAreas,
            clientState.TerritoryType,
            clientState.MapId,
            clientState.Instance,
            local?.GameObjectId ?? 0,
            local?.Address ?? nint.Zero,
            targetManager.Target?.GameObjectId ?? 0,
            targetManager.SoftTarget?.GameObjectId ?? 0,
            local is null || local.IsDead || condition[ConditionFlag.Unconscious],
            condition[ConditionFlag.Mounted],
            IsStunned(local),
            condition[ConditionFlag.BeingMoved]);
    }

    private void ObserveNativeInputContextTransitions()
    {
        var current = CaptureNativeInputContext();
        var previous = lastNativeInputContext;
        lastNativeInputContext = current;
        if (previous is null) return;

        CancelReason reason;
        string detail;
        if (previous.LoggedIn && !current.LoggedIn)
        {
            reason = CancelReason.Logout;
            detail = "Native repeat ownership cleared by logout";
        }
        else if (!previous.BetweenAreas && current.BetweenAreas
            || previous.TerritoryId != current.TerritoryId)
        {
            reason = CancelReason.TerritoryChange;
            detail = "Native repeat ownership cleared by territory transition";
        }
        else if (previous.MapId != current.MapId
            || previous.InstanceId != current.InstanceId
            || previous.LocalGameObjectId != current.LocalGameObjectId
            || previous.LocalAddress != current.LocalAddress)
        {
            reason = CancelReason.InstanceChange;
            detail = "Native repeat ownership cleared by instance/player transition";
        }
        else if (!previous.IsDead && current.IsDead)
        {
            reason = CancelReason.Death;
            detail = "Native repeat ownership cleared by death";
        }
        else if (!previous.IsMounted && current.IsMounted)
        {
            reason = CancelReason.Mounted;
            detail = "Native repeat ownership cleared by mounting";
        }
        else if (!previous.IsStunned && current.IsStunned)
        {
            reason = CancelReason.Stun;
            detail = "Native repeat ownership cleared by stun";
        }
        else if (!previous.IsBeingMoved && current.IsBeingMoved)
        {
            reason = CancelReason.Knockback;
            detail = "Native repeat ownership cleared by forced movement";
        }
        else if (previous.HardTargetId != current.HardTargetId
            || previous.SoftTargetId != current.SoftTargetId)
        {
            reason = CancelReason.TargetChange;
            detail = "Native repeat ownership cleared by target change";
        }
        else
        {
            return;
        }

        Cancel(reason, detail);
    }

    private bool IsBetweenAreas =>
        condition[ConditionFlag.BetweenAreas]
        || condition[ConditionFlag.BetweenAreas51];

    private static bool IsStunned(IPlayerCharacter? player) =>
        player?.StatusList.Any(status => status.StatusId == StunStatusId) == true;

    private void RefreshConflicts(bool force = false)
    {
        var now = NowMilliseconds;
        if (!force && now < nextCompatibilityRefreshAt) return;
        nextCompatibilityRefreshAt = SaturatingAdd(now, CompatibilityPollIntervalMilliseconds);

        var assessment = compatibility.Assess();
        var changed = compatibilitySignature.Length > 0
            && !string.Equals(compatibilitySignature, assessment.Signature, StringComparison.Ordinal);

        compatibilitySignature = assessment.Signature;
        activeConflicts = assessment.Conflicts;
        activeIntegrations = assessment.Integrations;
        excludedIntegrationActionIds = assessment.ExcludedActionIds;
        reActionTurboHotbarsEnabled = assessment.ReActionAudited
            && assessment.ReActionTurboHotbarsEnabled;
        reActionTurboHotbarsOutOfCombatEnabled = assessment.ReActionAudited
            && assessment.ReActionTurboHotbarsOutOfCombatEnabled;
        reActionMacroQueueEnabled = assessment.ReActionAudited
            && assessment.ReActionMacroQueueEnabled;
        reActionLoaded = assessment.ReActionLoaded;
        reActionAudited = assessment.ReActionAudited;
        reActionSmartActionTransformActive = assessment.ReActionLoaded
            && (!assessment.ReActionAudited
                || assessment.ReActionAutoTargetEnabled
                || assessment.ReActionActionStacksEnabled);

        if (changed)
        {
            lock (dispatchGate)
            {
                compatibilityQuarantineFrames = Math.Max(compatibilityQuarantineFrames, 1);
                Cancel(CancelReason.Conflict, "Plugin compatibility settings changed; waiting for one clean frame");
            }
        }

        if (activeConflicts.Count > 0
            && (engine.Pending is not null || turboEngine.Snapshot.HasActiveHold))
        {
            lock (dispatchGate)
            {
                Cancel(CancelReason.Conflict, "Suspended by the current plugin compatibility profile");
            }
        }
    }

    private void RecordSentSequence(ushort sequence, long sentAt)
    {
        if (sequence != 0) sentSequences[sequence] = sentAt;
    }

    private void RemoveStaleSequenceMarkers(long now)
    {
        foreach (var pair in sentSequences)
        {
            if (now - pair.Value > MaximumTimingSampleAgeMilliseconds)
            {
                sentSequences.TryRemove(pair.Key, out _);
            }
        }
    }

    private void RemoveStaleActionEffects(long now)
    {
        while (recentLocalActionEffects.TryPeek(out var observed)
            && (observed.ObservedAtMilliseconds <= 0
                || now - observed.ObservedAtMilliseconds > MaximumRecentActionEffectAgeMilliseconds))
        {
            recentLocalActionEffects.TryDequeue(out _);
        }
    }

    private bool WasRecentlyAcknowledged(TurboAcknowledgementSeed seed) =>
        WasRecentlyAcknowledged(seed.Expectation, seed.StartedAtMilliseconds);

    private bool WasRecentlyAcknowledged(MacroTurboAcknowledgementSeed seed) =>
        WasRecentlyAcknowledged(seed.Expectation, seed.StartedAtMilliseconds);

    private bool WasRecentlyAcknowledged(
        TurboActionEffectExpectation expectation,
        long startedAtMilliseconds)
    {
        foreach (var observed in recentLocalActionEffects)
        {
            if (observed.ObservedAtMilliseconds < startedAtMilliseconds) continue;
            if (TurboActionEffectAcknowledgementMatcher.Matches(
                    expectation,
                    observed.Observation))
            {
                return true;
            }
        }

        return false;
    }

    private void DrainTimingSamples()
    {
        while (observedResponseTimes.TryDequeue(out var elapsed))
        {
            var result = latency.AddSample(TimeSpan.FromMilliseconds(elapsed));
            if (configuration.DetailedLogging)
            {
                log.Verbose(
                    "Action response: {Elapsed} ms ({Result}, estimate {Estimate:0.0} ms).",
                    elapsed,
                    result,
                    latency.EstimatedRtt.TotalMilliseconds);
            }
        }
    }

    private void DrainTimingHookErrors()
    {
        if (!timingHookErrors.TryDequeue(out var exception)) return;
        while (timingHookErrors.TryDequeue(out _))
        {
        }

        if (Interlocked.Exchange(ref timingHookErrorLogged, 1) == 0)
        {
            log.Error(exception, "PulseQueue action-effect observer failed; further hook errors are suppressed until reload.");
        }
    }

    private static bool ContainsActionEffectType(
        ActionEffectHandler.TargetEffects* targetEffects,
        byte expectedType)
    {
        var effectSlots = targetEffects->Effects;
        for (var index = 0; index < effectSlots.Length; index++)
        {
            if (effectSlots[index].Type == expectedType) return true;
        }

        return false;
    }

    private void Fault(Exception exception, string context)
    {
        lock (dispatchGate)
        {
            var actionManager = ActionManager.Instance();
            if (!configuration.DryRun && actionManager != null)
            {
                ResolveLogicalRepeatQueuePending(
                    actionManager,
                    NowMilliseconds,
                    "before fault cancellation");
            }

            faulted = true;
            inputGenerations.Invalidate();
            physicalHotbarInput?.CancelAndRequireRelease();
            latestLogicalRepeatQueueReplacementGeneration = inputGenerations.Current;
            if (configuration.DryRun)
            {
                AbandonOwnedQueueProvenanceForDetectOnly();
            }
            else if (actionManager != null)
            {
                TryReplaceLogicalRepeatNativeQueue(
                    actionManager,
                    latestLogicalRepeatQueueReplacementGeneration,
                    "during fault cancellation");
            }
            else
            {
                logicalRepeatQueueOwnership.Clear();
                logicalRepeatQueuePending = null;
            }
            engine.Cancel(CancelReason.Explicit);
            pendingRuntimeAction = null;
            CancelTurboUnsafe(
                HoldRepeatCancelReason.Fault,
                $"Faulted: {context}",
                ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
            lastEvent = $"Faulted: {context}";
            if (!faultLogged)
            {
                faultLogged = true;
                log.Error(exception, "PulseQueue faulted closed: {Context}. Reload or explicitly reset before buffering resumes.", context);
            }
        }
    }

    private void CancelTurboUnsafe(
        HoldRepeatCancelReason reason,
        string detail,
        bool logTerminatedHold = false,
        OwnedQueueCancelPolicy ownedQueuePolicy = OwnedQueueCancelPolicy.Preserve)
    {
        if (reason == HoldRepeatCancelReason.None) reason = HoldRepeatCancelReason.InputLost;
        if (macroTurboRuntime is { } physicalMacroRuntime
            && !physicalMacroRuntime.InitialMacroLockCompleted)
        {
            RetainRetiredPhysicalMacroExecutor(
                physicalMacroRuntime,
                $"Turbo cancellation {reason}");
        }

        if (macroTurboRuntime is { } macroRuntime
            && macroRuntime.ActiveExecutionBudget is not null
            && macroRuntime.ActiveExecutionEpoch > 0
            && macroRuntime.OwnsMacroExecutor
            && IsMacroExecutionActive())
        {
            // Cancellation removes the runtime owner immediately, but the native
            // macro executor may still emit later lines. Preserve only the small
            // bounded-epoch tombstone needed to suppress those Macro-mode calls.
            QuarantineSyntheticMacroExecutor(
                macroRuntime,
                $"runtime cancelled during active bounded executor ({reason})");
        }

        if (ownedQueuePolicy == OwnedQueueCancelPolicy.ExactClear)
        {
            RequestExactOwnedNativeQueueSafetyClear($"Turbo cancellation {reason}: {detail}");
        }

        var hadActiveHold = logTerminatedHold || turboEngine.Snapshot.HasActiveHold;
        turboEngine.Cancel(reason);
        turboRuntime = null;
        macroTurboRuntime = null;
        Interlocked.Exchange(ref turboAcknowledgement, null);
        Interlocked.Exchange(ref macroTurboAcknowledgement, null);
        turboLastCancelReason = reason;
        lastEvent = detail;
        if (hadActiveHold && configuration.DetailedLogging)
        {
            log.Information("Turbo cancelled: {Reason}. {Detail}", reason, detail);
        }
    }

    private static HoldRepeatCancelReason ToTurboCancelReason(CancelReason reason) => reason switch
    {
        CancelReason.Replaced => HoldRepeatCancelReason.Replaced,
        CancelReason.Disabled => HoldRepeatCancelReason.Disabled,
        CancelReason.Conflict => HoldRepeatCancelReason.Conflict,
        CancelReason.Logout => HoldRepeatCancelReason.Logout,
        CancelReason.Death => HoldRepeatCancelReason.Death,
        CancelReason.Mounted => HoldRepeatCancelReason.Mounted,
        CancelReason.Stun => HoldRepeatCancelReason.Stun,
        CancelReason.Knockback => HoldRepeatCancelReason.Knockback,
        CancelReason.TerritoryChange => HoldRepeatCancelReason.TerritoryChange,
        CancelReason.InstanceChange => HoldRepeatCancelReason.InstanceChange,
        CancelReason.TargetChange => HoldRepeatCancelReason.TargetChange,
        CancelReason.ResolvedActionChange => HoldRepeatCancelReason.ResolvedActionChange,
        CancelReason.ServerRejected => HoldRepeatCancelReason.PulseRejected,
        CancelReason.Expired => HoldRepeatCancelReason.InputLost,
        CancelReason.NonTransientFailure or CancelReason.Ineligible => HoldRepeatCancelReason.ResolvedActionChange,
        _ => HoldRepeatCancelReason.InputLost,
    };

    private static OwnedQueueCancelPolicy QueuePolicyForHoldTermination(
        HoldRepeatCancelReason reason) => reason is
        HoldRepeatCancelReason.Released or
        HoldRepeatCancelReason.Replaced or
        HoldRepeatCancelReason.MaximumDuration
            ? OwnedQueueCancelPolicy.Preserve
            : OwnedQueueCancelPolicy.ExactClear;

    private void RejectTurboPulseUnsafe(string detail)
    {
        turboRejectedCount++;
        CancelTurboUnsafe(
            HoldRepeatCancelReason.PulseRejected,
            $"{detail}; hold ended without retry",
            ownedQueuePolicy: OwnedQueueCancelPolicy.ExactClear);
    }

    private bool BeginTurboAcknowledgement(
        TurboRuntime runtime,
        HoldRepeatPulseToken pulse,
        TurboAcknowledgementSequenceMode sequenceMode,
        ushort sequenceMarker,
        ExactActionTuple exactTuple)
    {
        var expectation = new TurboActionEffectExpectation(
            exactTuple.ActionType,
            exactTuple.RequestedActionId,
            exactTuple.ResolvedActionId,
            sequenceMode,
            sequenceMarker);
        return BeginTurboAcknowledgement(
            runtime,
            pulse,
            expectation,
            NowMilliseconds);
    }

    private bool BeginInitialTurboAcknowledgement(
        TurboRuntime runtime,
        TurboAcknowledgementSeed seed)
    {
        if (WasRecentlyAcknowledged(seed)) return true;
        return BeginTurboAcknowledgement(
            runtime,
            pulse: null,
            seed.Expectation,
            seed.StartedAtMilliseconds);
    }

    private bool BeginTurboAcknowledgement(
        TurboRuntime runtime,
        HoldRepeatPulseToken? pulse,
        TurboActionEffectExpectation expectation,
        long startedAtMilliseconds)
    {
        var snapshot = turboEngine.Snapshot;
        if (!expectation.IsValid
            || startedAtMilliseconds <= 0
            || !snapshot.HasActiveHold
            || snapshot.PressId != runtime.Press.PressId
            || pulse is { } pulseToken
                && (!pulseToken.IsValid || !turboEngine.IsTokenCurrent(pulseToken))
            || !ReferenceEquals(turboRuntime, runtime))
        {
            return false;
        }

        var acknowledgement = new TurboAcknowledgement(
            runtime,
            pulse,
            snapshot.HoldId,
            snapshot.PressId,
            expectation,
            startedAtMilliseconds);
        return Interlocked.CompareExchange(ref turboAcknowledgement, acknowledgement, null) is null;
    }

    private bool IsTurboAcknowledgementCurrent(TurboAcknowledgement acknowledgement)
    {
        var snapshot = turboEngine.Snapshot;
        return ReferenceEquals(turboRuntime, acknowledgement.Runtime)
            && snapshot.HasActiveHold
            && snapshot.HoldId == acknowledgement.HoldId
            && snapshot.PressId == acknowledgement.PressId
            && Volatile.Read(ref latestCertifiedPressId) == acknowledgement.PressId
            && (acknowledgement.Pulse is not { } pulse
                || turboEngine.IsTokenCurrent(pulse));
    }

    private bool BeginMacroTurboAcknowledgement(
        MacroTurboRuntime runtime,
        HoldRepeatPulseToken? pulse,
        long executionEpoch,
        MacroTurboAcknowledgementSeed seed)
    {
        if (WasRecentlyAcknowledged(seed)) return true;
        var snapshot = turboEngine.Snapshot;
        if (!seed.Expectation.IsValid
            || seed.StartedAtMilliseconds <= 0
            || !snapshot.HasActiveHold
            || snapshot.PressId != runtime.Press.PressId
            || !ReferenceEquals(macroTurboRuntime, runtime)
            || pulse is null && executionEpoch != 0
            || pulse is { } pulseToken
                && (!pulseToken.IsValid
                    || executionEpoch <= 0
                    || !turboEngine.IsTokenCurrent(pulseToken)))
        {
            return false;
        }

        var acknowledgement = new MacroTurboAcknowledgement(
            runtime,
            pulse,
            executionEpoch,
            snapshot.HoldId,
            snapshot.PressId,
            seed.Expectation,
            seed.StartedAtMilliseconds);
        return Interlocked.CompareExchange(
            ref macroTurboAcknowledgement,
            acknowledgement,
            null) is null;
    }

    private bool IsMacroTurboAcknowledgementCurrent(MacroTurboAcknowledgement acknowledgement)
    {
        var snapshot = turboEngine.Snapshot;
        return ReferenceEquals(macroTurboRuntime, acknowledgement.Runtime)
            && snapshot.HasActiveHold
            && snapshot.HoldId == acknowledgement.HoldId
            && snapshot.PressId == acknowledgement.PressId
            && Volatile.Read(ref latestCertifiedPressId) == acknowledgement.PressId
            && (acknowledgement.Pulse is not { } pulse
                || acknowledgement.ExecutionEpoch > 0
                    && acknowledgement.ExecutionEpoch <= acknowledgement.Runtime.NextExecutionEpoch
                    && turboEngine.IsTokenCurrent(pulse));
    }

    private void TryCompleteTurboAcknowledgement(ActionEffectHandler.Header* header)
    {
        if (header == null) return;
        lock (dispatchGate)
        {
            TryCompleteMacroTurboAcknowledgementUnsafe(header);
            var acknowledgement = Volatile.Read(ref turboAcknowledgement);
            if (acknowledgement is null
                || !IsTurboAcknowledgementCurrent(acknowledgement))
            {
                return;
            }

            var observation = new TurboActionEffectObservation(
                header->ActionType,
                header->ActionId,
                header->SourceSequence);
            if (!TurboActionEffectAcknowledgementMatcher.Matches(
                    acknowledgement.Expectation,
                    observation))
            {
                if (configuration.DetailedLogging)
                {
                    log.Debug(
                        "Turbo acknowledgement ignored: expected type={ExpectedType}, action={Requested}/{Resolved}, mode={Mode}, marker={Marker}; observed type={ObservedType}, action={ObservedAction}, spell={ObservedSpell}, sequence={ObservedSequence}.",
                        acknowledgement.Expectation.ActionType,
                        acknowledgement.Expectation.RequestedActionId,
                        acknowledgement.Expectation.ResolvedActionId,
                        acknowledgement.Expectation.SequenceMode,
                        acknowledgement.Expectation.SequenceMarker,
                        header->ActionType,
                        header->ActionId,
                        header->SpellId,
                        header->SourceSequence);
                }

                return;
            }

            if (!ReferenceEquals(
                    Interlocked.CompareExchange(ref turboAcknowledgement, null, acknowledgement),
                    acknowledgement))
            {
                return;
            }

            if (configuration.DetailedLogging)
            {
                log.Information(
                    "Turbo acknowledgement action={Action}, spell={Spell}, type={Type}, sequence={Sequence}, hold={Hold}, ordinal={Ordinal}, origin={Origin}.",
                    header->ActionId,
                    header->SpellId,
                    header->ActionType,
                    header->SourceSequence,
                    acknowledgement.HoldId,
                    acknowledgement.Pulse?.Ordinal ?? 0,
                    acknowledgement.Pulse is null ? "original-or-buffer" : "turbo-pulse");
            }
        }
    }

    private void TryCompleteMacroTurboAcknowledgementUnsafe(ActionEffectHandler.Header* header)
    {
        var acknowledgement = Volatile.Read(ref macroTurboAcknowledgement);
        if (acknowledgement is null
            || !IsMacroTurboAcknowledgementCurrent(acknowledgement))
        {
            return;
        }

        var observation = new TurboActionEffectObservation(
            header->ActionType,
            header->ActionId,
            header->SourceSequence);
        if (!TurboActionEffectAcknowledgementMatcher.Matches(
                acknowledgement.Expectation,
                observation))
        {
            if (configuration.DetailedLogging)
            {
                log.Debug(
                    "Macro Turbo acknowledgement ignored: expected type={ExpectedType}, action={Requested}/{Resolved}, mode={Mode}, marker={Marker}; observed type={ObservedType}, action={ObservedAction}, spell={ObservedSpell}, sequence={ObservedSequence}.",
                    acknowledgement.Expectation.ActionType,
                    acknowledgement.Expectation.RequestedActionId,
                    acknowledgement.Expectation.ResolvedActionId,
                    acknowledgement.Expectation.SequenceMode,
                    acknowledgement.Expectation.SequenceMarker,
                    header->ActionType,
                    header->ActionId,
                    header->SpellId,
                    header->SourceSequence);
            }

            return;
        }

        if (!ReferenceEquals(
                Interlocked.CompareExchange(
                    ref macroTurboAcknowledgement,
                    null,
                    acknowledgement),
                acknowledgement))
        {
            return;
        }

        if (configuration.DetailedLogging)
        {
            log.Information(
                "Macro Turbo acknowledgement action={Action}, spell={Spell}, type={Type}, sequence={Sequence}, hold={Hold}, epoch={Epoch}, ordinal={Ordinal}, origin={Origin}.",
                header->ActionId,
                header->SpellId,
                header->ActionType,
                header->SourceSequence,
                acknowledgement.HoldId,
                acknowledgement.ExecutionEpoch,
                acknowledgement.Pulse?.Ordinal ?? 0,
                acknowledgement.Pulse is null ? "original-macro" : "macro-turbo-pulse");
        }
    }

    private string DescribeTurboState()
    {
        if (!configuration.TurboEnabled) return "Off (opt-in)";
        if (physicalHotbarInput is null)
        {
            return $"Unavailable - {turboInputUnavailableReason}";
        }

        if (!configuration.Enabled) return "Off - PulseQueue is disabled";
        if (configuration.DryRun) return "Paused - dry run never emits Turbo pulses";
        var telemetry = physicalHotbarInput.Telemetry;
        if (telemetry.OwnerLogicalInputId > 0)
        {
            var owner = $"hotbar {telemetry.OwnerHotbarId + 1}, slot {telemetry.OwnerSlotId + 1}";
            return telemetry.Settings.ExternalRepeatOwnerActive
                ? $"Holding {owner} - repeats delegated to ReAction"
                : telemetry.Settings.RepeatEnabled
                    ? $"Holding {owner} - PulseQueue native repeats active"
                    : $"Holding {owner} - waiting for combat policy";
        }

        return Volatile.Read(ref reActionTurboHotbarsEnabled)
            ? "Ready - ReAction repeat ownership detected"
            : "Ready - PulseQueue native repeat ownership available";
    }

    private RuntimeState GetRuntimeState()
    {
        if (faulted) return RuntimeState.Faulted;
        if (!configuration.Enabled || disposed) return RuntimeState.Off;
        if (activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || !clientState.IsLoggedIn
            || IsBetweenAreas) return RuntimeState.Suspended;
        if (engine.Pending is not null) return configuration.DryRun ? RuntimeState.DryRun : RuntimeState.Pending;
        return RuntimeState.Ready;
    }

    private string DescribeState(RuntimeState state) => state switch
    {
        RuntimeState.Off => "Off",
        RuntimeState.Ready => "Ready - waiting for a direct hotbar intent",
        RuntimeState.Pending => "Pending - one exact action is buffered",
        RuntimeState.DryRun => "Dry run - pending actions are observed but never replayed",
        RuntimeState.Suspended when activeConflicts.Count > 0 => $"Suspended - conflict: {string.Join(", ", activeConflicts)}",
        RuntimeState.Suspended when compatibilityQuarantineFrames > 0 => "Suspended - compatibility state is settling",
        RuntimeState.Suspended => "Suspended - player or zone context is not stable",
        RuntimeState.Faulted => "Faulted closed - reload or reset required",
        _ => state.ToString(),
    };

    private static long NowMilliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private static long SaturatingAdd(long value, int delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;

    private static void DisposeSilently(IDisposable value)
    {
        try
        {
            value.Dispose();
        }
        catch
        {
            // Startup is already failing. Best-effort rollback must not hide the
            // original hook-enable exception.
        }
    }

    private static ulong Fingerprint(params ulong[] values)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var value in values)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }

    private static ulong NonZeroFingerprint(params ulong[] values)
    {
        var value = Fingerprint(values);
        return value == 0 ? 1UL : value;
    }

    private nint FindTargetAddress(ulong targetId)
    {
        if (targetId is 0 or InvalidObjectId) return nint.Zero;
        foreach (var gameObject in objectTable)
        {
            if (gameObject.GameObjectId == targetId) return gameObject.Address;
        }

        return nint.Zero;
    }

    private sealed record Candidate(
        ActionType ActionType,
        uint RequestedActionId,
        uint ResolvedActionId,
        ulong TargetId,
        uint ExtraParam,
        uint ComboRouteId,
        ushort SequenceAtCapture,
        long CapturedAtMilliseconds,
        long InputGeneration,
        bool IncludeResolverTargets,
        Snapshot Snapshot,
        ExactActionTuple ExactTuple,
        NativeQueueSnapshot QueueAtCapture)
    {
        public nint ExplicitTargetAddress { get; init; }
    }

    private sealed record RuntimeAction(
        Candidate Candidate,
        ActionRequest ActionRequest,
        double InitialTemporalRemainderMilliseconds,
        long ExpiresAtMilliseconds);

    private sealed record NativeLogicalRepeatExecutionScope(
        long Generation,
        long PressId,
        StandardHotbarBinding Binding,
        long ObservedAtMilliseconds,
        NativeQueueSnapshot QueueAtRoot,
        ushort SequenceAtRoot,
        bool ReActionMacroQueueAtRoot);

    private sealed record NativeMacroRepeatRootAttempt(
        NativeLogicalRepeatExecutionScope Execution,
        long StartedAtMilliseconds,
        long DiagnosticDeadlineMilliseconds);

    private sealed class NativeMacroRepeatTail(
        NativeLogicalRepeatExecutionScope execution,
        long startedAtMilliseconds,
        long diagnosticDeadlineMilliseconds)
    {
        public NativeLogicalRepeatExecutionScope Execution { get; } = execution;

        public long StartedAtMilliseconds { get; } = startedAtMilliseconds;

        public long DiagnosticDeadlineMilliseconds { get; } = diagnosticDeadlineMilliseconds;

        public bool MacroLockObserved { get; set; }

        public bool TimeoutReported { get; set; }
    }

    private sealed class LogicalRepeatQueueAttempt(
        NativeLogicalRepeatExecutionScope execution,
        NativeQueueSnapshot queueBefore,
        ExactActionTuple expected,
        ushort sequenceBefore,
        long startedAtMilliseconds,
        bool allowDeferredOuterHookCorrelation)
    {
        public NativeLogicalRepeatExecutionScope Execution { get; } = execution;

        public NativeQueueSnapshot QueueBefore { get; } = queueBefore;

        public ExactActionTuple Expected { get; } = expected;

        public ushort SequenceBefore { get; } = sequenceBefore;

        public long StartedAtMilliseconds { get; } = startedAtMilliseconds;

        public bool AllowDeferredOuterHookCorrelation { get; } = allowDeferredOuterHookCorrelation;

        public bool SupersededByProvablyDifferentPhysicalInput { get; set; }

        public bool SupersededByTerminalCancellation { get; set; }
    }

    private sealed class LogicalRepeatQueuePending(
        NativeLogicalRepeatExecutionScope execution,
        NativeQueueSnapshot queueBefore,
        ExactActionTuple expected,
        ushort sequenceMarker,
        long expiresAtMilliseconds)
    {
        public NativeLogicalRepeatExecutionScope Execution { get; } = execution;

        public NativeQueueSnapshot QueueBefore { get; } = queueBefore;

        public ExactActionTuple Expected { get; } = expected;

        public ushort SequenceMarker { get; } = sequenceMarker;

        public long ExpiresAtMilliseconds { get; } = expiresAtMilliseconds;

        public bool SupersededByProvablyDifferentPhysicalInput { get; set; }

        public bool SupersededByTerminalCancellation { get; set; }
    }

    private sealed record NativeInputContext(
        bool LoggedIn,
        bool BetweenAreas,
        uint TerritoryId,
        uint MapId,
        uint InstanceId,
        ulong LocalGameObjectId,
        nint LocalAddress,
        ulong HardTargetId,
        ulong SoftTargetId,
        bool IsDead,
        bool IsMounted,
        bool IsStunned,
        bool IsBeingMoved);

    private sealed class HotbarInputScope(
        long generation,
        CertifiedHotbarPress? certifiedPress,
        HotbarSlotIdentity? slotIdentity)
    {
        public long Generation { get; } = generation;

        public CertifiedHotbarPress? CertifiedPress { get; } = certifiedPress;

        public HotbarSlotIdentity? SlotIdentity { get; } = slotIdentity;

        public bool MaySupersedeOwnedQueue { get; set; }

        public int ActionInvocationCount { get; set; }

        public bool TurboDisqualified { get; set; }

        public Candidate? TurboCandidate { get; set; }

        public Snapshot? DirectSnapshotAtPress { get; set; }

        public bool MacroWasLockedBeforeExecution { get; set; }

        public bool MacroLockObservedDuringExecution { get; set; }

        public Snapshot? MacroSnapshotAtPress { get; set; }

        public SafeActionMacroProfile? MacroProfileAtPress { get; set; }

        public MacroTurboExecutionBudget? MacroExecutionBudget { get; set; }

        public bool MacroProvenanceDisqualified { get; set; }

        public string? MacroProvenanceFailure { get; set; }

        public ExactActionTuple? OwnedMacroQueueTuple { get; set; }

        public TurboAcknowledgementSeed? InitialAcknowledgement { get; set; }

        public MacroTurboAcknowledgementSeed? InitialMacroAcknowledgement { get; set; }

        public int InitialMacroAcceptedOutcomeCount { get; set; }
    }

    private readonly record struct HotbarSlotIdentity(
        StandardHotbarBinding Binding,
        uint CommandType,
        uint CommandId,
        ulong ControlFingerprint);

    private sealed record TurboRuntime(
        CertifiedHotbarPress Press,
        HotbarSlotIdentity SlotIdentity,
        Candidate Candidate,
        SafeActionMacroProfile? MacroProfile,
        string CompatibilitySignature,
        HoldRepeatStartRequest StartRequest,
        bool HasCapturedInvocation)
    {
        public bool IsMacro => MacroProfile is not null;

        public ExactActionTuple? OwnedQueueTuple { get; set; }
    }

    private sealed class MacroTurboRuntime(
        CertifiedHotbarPress press,
        HotbarSlotIdentity slotIdentity,
        SafeActionMacroProfile macroProfile,
        Snapshot snapshot,
        string compatibilitySignature,
        long generation,
        HoldRepeatStartRequest startRequest,
        long initialMacroLockDeadlineMilliseconds,
        MacroTurboExecutionBudget? initialExecutionBudget,
        int initialPhysicalActionCallCount)
    {
        public CertifiedHotbarPress Press { get; } = press;

        public HotbarSlotIdentity SlotIdentity { get; } = slotIdentity;

        public SafeActionMacroProfile MacroProfile { get; } = macroProfile;

        public Snapshot Snapshot { get; } = snapshot;

        public string CompatibilitySignature { get; } = compatibilitySignature;

        public long Generation { get; } = generation;

        public HoldRepeatStartRequest StartRequest { get; } = startRequest;

        public long InitialMacroLockDeadlineMilliseconds { get; } = initialMacroLockDeadlineMilliseconds;

        public MacroTurboExecutionBudget? InitialExecutionBudget { get; set; } = initialExecutionBudget;

        public int InitialPhysicalActionCallCount { get; set; } = initialPhysicalActionCallCount;

        public bool InitialMacroLockObserved { get; set; }

        public bool InitialMacroLockCompleted { get; set; }

        public int InitialAcceptedOutcomeCount { get; set; }

        public bool OwnsMacroExecutor { get; set; }

        public ExactActionTuple? OwnedQueueTuple { get; set; }

        public long NextExecutionEpoch { get; set; }

        public long ActiveExecutionEpoch { get; set; }

        public MacroTurboExecutionBudget? ActiveExecutionBudget { get; set; }

        public HoldRepeatPulseToken? ActiveExecutionToken { get; set; }
    }

    private sealed record MacroPulseExecutionScope(
        MacroTurboRuntime Runtime,
        HoldRepeatPulseToken Token,
        long ExecutionEpoch);

    private sealed class DirectPulseExecutionScope(
        TurboRuntime runtime,
        HoldRepeatPulseToken token,
        uint expectedResolvedActionId)
    {
        public TurboRuntime Runtime { get; } = runtime;

        public HoldRepeatPulseToken Token { get; } = token;

        public uint ExpectedResolvedActionId { get; } = expectedResolvedActionId;

        public int InvocationCount { get; set; }

        public bool Completed { get; set; }

        public bool Accepted { get; set; }

        public ExactActionTuple? ExactTuple { get; set; }

        public ushort SequenceBefore { get; set; }

        public ushort SequenceAfter { get; set; }

        public NativeQueueSnapshot QueueAfter { get; set; }
    }

    private sealed record DirectPulseAttempt(
        DirectPulseExecutionScope Execution,
        OwnedNativeQueueSafetySeed SafetySeed,
        ExactActionTuple ExactTuple,
        NativeQueueSnapshot QueueBefore,
        ushort SequenceBefore);

    private sealed record NativeQueueDrainAttempt(
        NativeQueueDrainLease Lease,
        long Generation,
        MacroTurboRuntime? MacroRuntime,
        TurboRuntime? DirectRuntime);

    private sealed record SyntheticMacroExecutorQuarantine(
        long Generation,
        long PressId,
        long ExecutionEpoch,
        long StartedAtMilliseconds,
        long ExpiresAtMilliseconds,
        bool TimeoutReported = false);

    private readonly record struct MacroActionInvocation(
        uint ActionType,
        uint RequestedActionId,
        uint ResolvedActionId,
        ulong TargetId,
        uint ExtraParam,
        uint RouteId,
        Snapshot ActionSnapshot,
        bool IncludeResolverTargets,
        nint ExplicitTargetAddress,
        ulong ResolverFingerprint)
    {
        public bool IsValid =>
            ActionType != 0
            && RequestedActionId != 0
            && ResolvedActionId != 0;
    }

    private sealed record MacroQueueAttempt(
        long Generation,
        MacroTurboRuntime? Runtime,
        HotbarInputScope? InputScope,
        RetiredPhysicalMacroExecutor? RetiredExecutor,
        OwnedNativeQueueSafetySeed SafetySeed,
        ExactActionTuple Attempted,
        NativeQueueSnapshot QueueBefore,
        ushort SequenceBefore,
        HoldRepeatPulseToken? PulseToken,
        long ExecutionEpoch,
        long StartedAtMilliseconds);

    private sealed record RetiredPhysicalMacroExecutor(
        long Generation,
        long PressId,
        HotbarSlotIdentity SlotIdentity,
        string ContentFingerprint,
        int MaximumActionCalls,
        Snapshot Snapshot,
        int ObservedActionCalls,
        long StartedAtMilliseconds,
        long ExpiresAtMilliseconds,
        bool TimeoutReported = false);

    private sealed record OwnedNativeQueueSafetySeed(
        Snapshot RootSnapshot,
        Snapshot InvocationSnapshot,
        bool IncludeResolverTargets,
        nint ExplicitTargetAddress);

    private sealed record OwnedNativeQueueSafetyContext(
        long Generation,
        ExactActionTuple Attempted,
        Snapshot RootSnapshot,
        Snapshot InvocationSnapshot,
        bool IncludeResolverTargets,
        nint ExplicitTargetAddress);

    private sealed record TurboAcknowledgementSeed(
        TurboActionEffectExpectation Expectation,
        long StartedAtMilliseconds);

    private sealed record MacroTurboAcknowledgementSeed(
        TurboActionEffectExpectation Expectation,
        long StartedAtMilliseconds);

    private sealed record TimedTurboActionEffect(
        TurboActionEffectObservation Observation,
        long ObservedAtMilliseconds);

    private sealed record TurboAcknowledgement(
        TurboRuntime Runtime,
        HoldRepeatPulseToken? Pulse,
        long HoldId,
        long PressId,
        TurboActionEffectExpectation Expectation,
        long StartedAtMilliseconds);

    private sealed record MacroTurboAcknowledgement(
        MacroTurboRuntime Runtime,
        HoldRepeatPulseToken? Pulse,
        long ExecutionEpoch,
        long HoldId,
        long PressId,
        TurboActionEffectExpectation Expectation,
        long StartedAtMilliseconds);

    private readonly struct TurboObservation
    {
        public TurboObservation(
            ActionManager* actionManager,
            RaptureHotbarModule* hotbarModule,
            uint resolvedActionId,
            HoldRepeatSafetyState safety,
            bool actionReady)
        {
            ActionManager = actionManager;
            HotbarModule = hotbarModule;
            ResolvedActionId = resolvedActionId;
            Safety = safety;
            ActionReady = actionReady;
        }

        public ActionManager* ActionManager { get; }

        public RaptureHotbarModule* HotbarModule { get; }

        public uint ResolvedActionId { get; }

        public HoldRepeatSafetyState Safety { get; }

        public bool ActionReady { get; }
    }

    private readonly struct MacroTurboObservation
    {
        public MacroTurboObservation(
            RaptureHotbarModule* hotbarModule,
            HoldRepeatSafetyState safety,
            bool actionReady)
        {
            HotbarModule = hotbarModule;
            Safety = safety;
            ActionReady = actionReady;
        }

        public RaptureHotbarModule* HotbarModule { get; }

        public HoldRepeatSafetyState Safety { get; }

        public bool ActionReady { get; }
    }

    private sealed record Snapshot(
        uint TerritoryId,
        uint MapId,
        uint InstanceId,
        uint JobId,
        bool IsPvP,
        ulong LocalGameObjectId,
        nint LocalAddress,
        ulong HardTargetId,
        ulong SoftTargetId,
        ulong MouseOverTargetId,
        ulong MouseOverNameplateTargetId,
        ulong TargetFingerprint,
        ulong ContextFingerprint,
        uint ResolvedActionId,
        bool IsMounted,
        bool IsStunned,
        bool IsBeingMoved);
}
