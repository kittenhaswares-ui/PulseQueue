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
    long IntegrationExclusions,
    bool TurboConfigured,
    bool TurboInputAvailable,
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
/// animation lock, recast state, targets, or native queue fields.
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
    private const uint ReActionCameraRelativeMovementException = 29494;
    private const uint DirectActionHotbarSlotType = 1;
    private const uint MacroHotbarSlotType = 7;

    [ThreadStatic]
    private static int hotbarExecutionDepth;

    [ThreadStatic]
    private static bool replaying;

    [ThreadStatic]
    private static bool turboDispatching;

    [ThreadStatic]
    private static HotbarInputScope? activeHotbarInput;

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
    private PendingMacroCapture? pendingMacroCapture;
    private TurboRuntime? turboRuntime;
    private IReadOnlyList<string> activeConflicts = Array.Empty<string>();
    private IReadOnlyList<string> activeIntegrations = Array.Empty<string>();
    private IReadOnlySet<uint> excludedIntegrationActionIds = new HashSet<uint>();
    private string compatibilitySignature = string.Empty;
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
    private long observedHotbarInputCount;
    private long replacedPendingCount;
    private long integrationExclusionCount;
    private long turboStartCount;
    private long turboPulseCount;
    private long turboAcceptedCount;
    private long turboRejectedCount;
    private long latestCertifiedPressId;
    private TurboAcknowledgement? turboAcknowledgement;
    private uint localEntityId;
    private int forcedMovementObserved;
    private int timingHookErrorLogged;
    private bool faulted;
    private bool faultLogged;
    private bool disposed;
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
                OnCertifiedPhysicalPress,
                ShouldSuppressHeldRepeat);
        }
        catch (Exception exception)
        {
            turboInputUnavailableReason = "physical keyboard input hook unavailable";
            log.Warning(exception, "PulseQueue native Turbo is unavailable; the one-shot buffer will remain usable.");
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
                integrationExclusionCount,
                configuration.TurboEnabled,
                physicalHotbarInput is not null,
                turboEngine.Snapshot.State,
                turboStartCount,
                turboPulseCount,
                physicalHotbarInput?.SuppressedHeldRepeatCount ?? 0,
                turboAcceptedCount,
                turboRejectedCount,
                turboLastCancelReason,
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
                    turboInputUnavailableReason = "physical keyboard input hook could not be enabled";
                    log.Warning(exception, "PulseQueue native Turbo is unavailable; the one-shot buffer remains enabled.");
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
            inputGenerations.Invalidate();
            engine.Cancel(reason);
            pendingRuntimeAction = null;
            pendingMacroCapture = null;
            recentLocalActionEffects.Clear();
            CancelTurboUnsafe(ToTurboCancelReason(reason), detail);
            lastEvent = detail;
        }
    }

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
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        framework.Update -= OnFrameworkUpdate;
        Cancel(CancelReason.Disabled, "Plugin disposed");
        lock (dispatchGate)
        {
            nativeQueueOwnership.Clear();
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
        if (rootInput)
        {
            try
            {
                CertifiedHotbarPress? certifiedPress = null;
                if (physicalHotbarInput?.TryConsume(thisPtr, slot, NowMilliseconds, out var observedPress) == true)
                {
                    certifiedPress = observedPress;
                }

                BeginHotbarInput(certifiedPress, CaptureHotbarSlotIdentity(certifiedPress, slot));
            }
            catch (Exception exception)
            {
                activeHotbarInput = null;
                Fault(exception, "Physical hotbar certification failed");
            }
        }

        var originalCompleted = false;
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
            if (rootInput)
            {
                if (originalCompleted) CompleteHotbarInput();
                else activeHotbarInput = null;
            }
        }
    }

    private byte ExecuteSlotByIdDetour(RaptureHotbarModule* thisPtr, uint hotbarId, uint slotId)
    {
        var rootInput = hotbarExecutionDepth == 0 && !replaying && !turboDispatching;
        if (rootInput)
        {
            try
            {
                CertifiedHotbarPress? certifiedPress = null;
                if (physicalHotbarInput?.TryConsume(hotbarId, slotId, NowMilliseconds, out var observedPress) == true)
                {
                    certifiedPress = observedPress;
                }

                var slot = thisPtr == null ? null : thisPtr->GetSlotById(hotbarId, slotId);
                BeginHotbarInput(certifiedPress, CaptureHotbarSlotIdentity(certifiedPress, slot));
            }
            catch (Exception exception)
            {
                activeHotbarInput = null;
                Fault(exception, "Physical hotbar certification failed");
            }
        }

        var originalCompleted = false;
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
            if (rootInput)
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
        Candidate? turboOutcomeCandidate = null;
        var nativeHotbarInput = hotbarExecutionDepth > 0 && !replaying && !turboDispatching;
        var sequenceBefore = thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence;

        if (!replaying && !turboDispatching)
        {
            lock (dispatchGate)
            {
                var capturedPendingMacro = false;
                if (!nativeHotbarInput)
                {
                    capturedPendingMacro = TryCapturePendingMacroInvocation(
                        thisPtr,
                        actionType,
                        actionId,
                        targetId,
                        extraParam,
                        mode,
                        comboRouteId,
                        out turboOutcomeCandidate);
                    if (!capturedPendingMacro
                        && !IsOwnedTurboActionContinuation(
                            thisPtr,
                            actionType,
                            actionId,
                            targetId,
                            extraParam,
                            mode,
                            comboRouteId))
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
                            macroScope.ActionInvocationCount++;
                            if (macroScope.MacroWasLockedBeforeExecution
                                || macroScope.ActionInvocationCount > 1
                                || mode != ActionManager.UseActionMode.Macro)
                            {
                                macroScope.TurboCandidate = null;
                                macroScope.TurboDisqualified = true;
                            }
                            else
                            {
                                var macroCandidate = TryCreateCandidate(
                                    thisPtr,
                                    actionType,
                                    actionId,
                                    targetId,
                                    extraParam,
                                    mode,
                                    comboRouteId,
                                    ActionManager.UseActionMode.Macro);
                                if (macroCandidate is null || !IsMacroTargetProven(macroCandidate))
                                {
                                    macroScope.TurboDisqualified = true;
                                }
                                else
                                {
                                    macroScope.TurboCandidate = macroCandidate;
                                    turboOutcomeCandidate = macroCandidate;
                                }
                            }

                            // The physical macro execution stays completely vanilla.
                            // Its action is provenance only; it never enters one-shot
                            // buffering or queue replacement.
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
                        if (candidate is { } ownershipCandidate
                            && nativeQueueOwnership.HasOwnership
                            && !compatibility.IsLiveMOActionUnowned(
                                ownershipCandidate.RequestedActionId,
                                ownershipCandidate.ResolvedActionId))
                        {
                            MarkCompatibilityProfileDirty("MOAction ownership changed");
                            candidate = null;
                        }

                        if (candidate is { } captured
                            && thisPtr->GetActionStatus(
                                captured.ActionType,
                                captured.ResolvedActionId,
                                captured.TargetId,
                                false,
                                false) == 0
                            && GetTemporalRemainingMilliseconds(
                                thisPtr,
                                captured.ActionType,
                                captured.ResolvedActionId) is var supersedingRemainder
                            && double.IsFinite(supersedingRemainder)
                            && supersedingRemainder >= 0
                            && supersedingRemainder < CurrentHoldWindowMilliseconds)
                        {
                            if (activeHotbarInput is { } scope
                                && scope.Generation == captured.InputGeneration)
                            {
                                scope.MaySupersedeOwnedQueue = true;
                            }

                            if (TryReplaceOwnedNativeQueue(
                                    thisPtr,
                                    captured.InputGeneration,
                                    "before the newest native action call"))
                            {
                                candidate = captured with
                                {
                                    QueueAtCapture = CaptureNativeQueue(thisPtr),
                                };
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

        // This is deliberately outside plugin-side exception recovery: the native
        // original is invoked once and only once, and its result/exception is authoritative.
        var result = useActionHook.Original(
            thisPtr,
            actionType,
            actionId,
            targetId,
            extraParam,
            mode,
            comboRouteId,
            outOptAreaTargeted);

        try
        {
            var currentSequence = thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence;
            if (candidate is { } captured)
            {
                lock (dispatchGate)
                {
                    ProcessOriginalOutcome(thisPtr, captured, result, currentSequence, outOptAreaTargeted);
                }
            }
            else if (turboOutcomeCandidate is { } macroCandidate)
            {
                lock (dispatchGate)
                {
                    ProcessMacroTurboOriginalOutcome(
                        thisPtr,
                        macroCandidate,
                        result,
                        currentSequence);
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
        };
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Observed hotbar input generation={Generation}, replacedPending={ReplacedPending}.",
                inputGenerations.Current,
                replacedPending);
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
                Cancel(
                    CancelReason.Replaced,
                    $"Physical hotbar press {press.PressId} preempted every older buffered or Turbo owner");
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

    private bool ShouldSuppressHeldRepeat(CertifiedHotbarPress press)
    {
        lock (dispatchGate)
        {
            if (disposed
                || faulted
                || !configuration.Enabled
                || !configuration.TurboEnabled
                || configuration.DryRun)
            {
                return false;
            }

            // Once a newer physical edge wins, an older still-held key may not
            // revive through OS/native typematic. The player must release it and
            // create a genuinely new hardware edge.
            if (Volatile.Read(ref latestCertifiedPressId) > press.PressId)
            {
                return true;
            }

            if (pendingMacroCapture?.Press.PressId == press.PressId)
            {
                return true;
            }

            return turboRuntime?.Press.PressId == press.PressId
                && turboEngine.Snapshot.HasActiveHold;
        }
    }

    private bool TryCapturePendingMacroInvocation(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        out Candidate? capturedCandidate)
    {
        capturedCandidate = null;
        var pending = pendingMacroCapture;
        if (pending is null
            || pending.Disqualified
            || mode != ActionManager.UseActionMode.Macro
            || !IsMacroExecutionActive()
            || NowMilliseconds > pending.ExpiresAtMilliseconds
            || Volatile.Read(ref latestCertifiedPressId) != pending.Press.PressId
            || physicalHotbarInput?.IsStillHeld(pending.Press) != true)
        {
            return false;
        }

        pending.ActionInvocationCount++;
        if (pending.ActionInvocationCount > 1)
        {
            pending.Candidate = null;
            pending.Disqualified = true;
            return true;
        }

        pending.Candidate = TryCreateCandidate(
            actionManager,
            actionType,
            actionId,
            targetId,
            extraParam,
            mode,
            comboRouteId,
            ActionManager.UseActionMode.Macro);
        if (pending.Candidate is null || !IsMacroTargetProven(pending.Candidate))
        {
            pending.Candidate = null;
            pending.Disqualified = true;
        }
        capturedCandidate = pending.Candidate;
        return true;
    }

    private static bool IsMacroTargetProven(Candidate candidate) =>
        !candidate.IncludeResolverTargets
        || candidate.TargetId is not (0 or InvalidObjectId);

    private bool IsOwnedTurboActionContinuation(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId)
    {
        var runtime = turboRuntime;
        if (runtime is null
            || actionManager == null
            || !turboEngine.Snapshot.HasActiveHold
            || Volatile.Read(ref latestCertifiedPressId) != runtime.Press.PressId
            || physicalHotbarInput?.IsStillHeld(runtime.Press) != true)
        {
            return false;
        }

        var candidate = runtime.Candidate;
        var exactInvocation = actionType == candidate.ActionType
            && actionId is var observedActionId
            && observedActionId != 0
            && (observedActionId == candidate.RequestedActionId
                || observedActionId == candidate.ResolvedActionId)
            && targetId == candidate.TargetId
            && extraParam == candidate.ExtraParam
            && comboRouteId == candidate.ComboRouteId;
        var currentQueue = CaptureNativeQueue(actionManager);
        var ownedQueueTuple = candidate.ExactTuple with
        {
            // QueueType describes the stored entry and is not the same thing as
            // the UseActionMode.Queue invocation that drains it. Preserve the
            // exact stored identity for ownership authorization while requiring
            // the observed invocation itself to be an explicit native drain.
            Mode = currentQueue.Mode,
        };
        return mode == ActionManager.UseActionMode.Queue
            && exactInvocation
            && nativeQueueOwnership.TryAuthorizeExactDrain(
                candidate.InputGeneration,
                actionManager->LastUsedActionSequence,
                currentQueue,
                ownedQueueTuple);
    }

    private static bool IsMacroExecutionActive()
    {
        var shell = RaptureShellModule.Instance();
        return shell != null && shell->MacroLocked;
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
                if (!inputGenerations.IsCurrent(scope.Generation)) return;
                var actionManager = ActionManager.Instance();
                if (actionManager != null && scope.MaySupersedeOwnedQueue)
                {
                    TryReplaceOwnedNativeQueue(actionManager, scope.Generation, "after the complete hotbar call");
                }

                TryStartTurbo(scope);
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
            || activeConflicts.Count > 0
            || compatibilityQuarantineFrames > 0
            || scope.CertifiedPress is not { } press
            || scope.SlotIdentity is not { } slotIdentity
            || !inputGenerations.IsCurrent(scope.Generation)
            || Volatile.Read(ref latestCertifiedPressId) != press.PressId
            || (!configuration.TurboOutOfCombat && !condition[ConditionFlag.InCombat]))
        {
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

        if (scope.TurboCandidate is not { } candidate
            || scope.TurboDisqualified
            || scope.ActionInvocationCount != 1)
        {
            LogTurboStartRejected(slotIdentity, "slot did not produce exactly one eligible Action/PvPAction invocation");
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

        if (!TryReadSafeMacroProfile(slotIdentity, out var profile, out var failure))
        {
            LogTurboStartRejected(slotIdentity, $"macro profile rejected ({failure})");
            return;
        }

        if (scope.TurboDisqualified || scope.ActionInvocationCount > 1)
        {
            LogTurboStartRejected(slotIdentity, "macro produced multiple or ineligible action calls");
            return;
        }

        var macroExecutionActive = IsMacroExecutionActive();
        if (scope.TurboCandidate is null && !macroExecutionActive)
        {
            LogTurboStartRejected(slotIdentity, "macro produced no synchronous action and did not enter the native macro executor");
            return;
        }

        if (macroExecutionActive)
        {
            pendingMacroCapture = new PendingMacroCapture(
                press,
                slotIdentity,
                profile,
                scope.Generation,
                SaturatingAdd(NowMilliseconds, MaximumMacroCaptureMilliseconds))
            {
                Candidate = scope.TurboCandidate,
                ActionInvocationCount = scope.ActionInvocationCount,
                Disqualified = scope.TurboDisqualified,
                InitialAcknowledgement = scope.InitialAcknowledgement,
            };
            lastEvent = $"Waiting for macro hotbar {slotIdentity.Binding.HotbarId + 1}, slot {slotIdentity.Binding.SlotId + 1} to finish exact action capture";
            if (configuration.DetailedLogging)
            {
                log.Information(
                    "Turbo macro capture pending press={PressId}, hotbar={Hotbar}, slot={Slot}, command={Command}, actions={Actions}.",
                    press.PressId,
                    slotIdentity.Binding.HotbarId + 1,
                    slotIdentity.Binding.SlotId + 1,
                    slotIdentity.CommandId,
                    scope.ActionInvocationCount);
            }

            return;
        }

        if (scope.TurboCandidate is not { } capturedMacroAction)
        {
            LogTurboStartRejected(slotIdentity, "macro action capture was unavailable after execution");
            return;
        }

        StartTurboRuntime(
            scope,
            press,
            slotIdentity,
            capturedMacroAction,
            profile,
            inputSource);
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
        if (actionManager == null
            || actionManager->GetActionStatus(
                candidate.ActionType,
                candidate.ResolvedActionId,
                candidate.TargetId,
                false,
                false) != 0)
        {
            LogTurboStartRejected(slotIdentity, "action is structurally unavailable");
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
            request);
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
                Interlocked.Exchange(ref turboAcknowledgement, null);
                return;
            }

            if (frameGap < 0 || frameGap > MaximumFrameGapMilliseconds)
            {
                CancelTurboUnsafe(
                    HoldRepeatCancelReason.InputLost,
                    $"Turbo cancelled after {frameGap} ms frame gap");
                return;
            }

            if (turboRuntime is not { } runtime)
            {
                CancelTurboUnsafe(HoldRepeatCancelReason.Fault, "Turbo runtime token mismatch");
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
                    $"Turbo cancelled: {decision.CancelReason}");
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
                        "Turbo received no matching action-effect acknowledgement; hold ended without retry");
                }

                return;
            }

            if (decision.Kind != HoldRepeatDecisionKind.Pulse) return;
            DispatchTurboPulse(runtime, decision.Pulse);
        }
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
                    "Turbo final safety check failed");
                return;
            }

            if (!observation.ActionReady
                || observation.ActionManager == null)
            {
                return;
            }

            var actionManager = observation.ActionManager;
            var sequenceBefore = actionManager->LastUsedActionSequence;
            var queueBefore = CaptureNativeQueue(actionManager);
            byte result;
            turboDispatching = true;
            try
            {
                // The physical press already ran its slot (or complete macro)
                // exactly once. Every later pulse emits one and only one captured
                // native action tuple. Re-executing the slot could allow a combo
                // transform, macro line, or foreign slot hook to select a different
                // action between pulses.
                var areaTargeted = false;
                var accepted = useActionHook.Original(
                    actionManager,
                    runtime.Candidate.ActionType,
                    runtime.Candidate.RequestedActionId,
                    runtime.Candidate.TargetId,
                    runtime.Candidate.ExtraParam,
                    (ActionManager.UseActionMode)runtime.Candidate.ExactTuple.Mode,
                    runtime.Candidate.ComboRouteId,
                    &areaTargeted);
                result = accepted ? (byte)1 : (byte)0;
            }
            finally
            {
                turboDispatching = false;
            }

            turboPulseCount++;
            var sequenceAfter = actionManager->LastUsedActionSequence;
            var queueAfter = CaptureNativeQueue(actionManager);
            var sequenceAdvanced = sequenceAfter != sequenceBefore;
            var exactTuple = runtime.Candidate.ExactTuple;
            var nativeOutcome = NativeActionOutcomeClassifier.Classify(
                result != 0 || sequenceAdvanced,
                queueBefore,
                queueAfter,
                exactTuple);

            if (nativeOutcome == NativeActionOutcome.ImmediateAcceptance && sequenceAdvanced)
            {
                nativeQueueOwnership.Clear();
                RecordSentSequence(sequenceAfter, NowMilliseconds);
                if (!BeginTurboAcknowledgement(
                        runtime,
                        token,
                        TurboAcknowledgementSequenceMode.ImmediateExact,
                        sequenceAfter,
                        exactTuple))
                {
                    RejectTurboPulseUnsafe(
                        $"Turbo immediate action {observation.ResolvedActionId} had no valid acknowledgement identity");
                    return;
                }
            }
            else if (nativeOutcome == NativeActionOutcome.MatchingNewQueue
                && !sequenceAdvanced
                && !runtime.IsMacro)
            {
                var claimed = nativeQueueOwnership.TryClaimNewQueue(
                    runtime.Candidate.InputGeneration,
                    sequenceAfter,
                    queueBefore,
                    queueAfter,
                    exactTuple);
                if (!claimed
                    || !BeginTurboAcknowledgement(
                        runtime,
                        token,
                        TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                        sequenceBefore,
                        exactTuple))
                {
                    nativeQueueOwnership.Clear();
                    RejectTurboPulseUnsafe(
                        $"Turbo queue for action {observation.ResolvedActionId} could not be proven exact");
                    return;
                }
            }
            else
            {
                // A bool return, foreign/preexisting queue, or simultaneous sequence
                // plus queue transition is not an exact identity. The one native slot
                // invocation remains authoritative, but this hold can never retry it.
                RejectTurboPulseUnsafe(
                    $"Turbo pulse for action {observation.ResolvedActionId} was {nativeOutcome} with sequenceAdvanced={sequenceAdvanced}");
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
                    observation.ResolvedActionId,
                    result,
                    sequenceBefore,
                    sequenceAfter,
                    queueAfter.IsQueued);
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
            && resolvedActionId == runtime.Candidate.ResolvedActionId
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
        if (actionManager == null) return false;
        var current = CaptureNativeQueue(actionManager);
        // ReAction may temporarily hide an older queue around its outer hook and
        // restore it only after PulseQueue's inner UseAction returns. Preserve
        // ownership across that empty interval; the full hotbar completion check
        // sees the stable post-hook state.
        if (!current.IsQueued) return false;
        if (!nativeQueueOwnership.TryTakeForNewerInput(
                replacingGeneration,
                actionManager->LastUsedActionSequence,
                current,
                out var replaced))
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
        if (disposed || faulted || !configuration.Enabled || actionManager == null || mode != requiredMode)
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
            && nativeQueueOwnership.TryClaimNewQueue(
                candidate.InputGeneration,
                currentSequence,
                candidate.QueueAtCapture,
                queueAfter,
                candidate.ExactTuple);
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
                nativeQueueOwnership.Clear();
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

    private void ProcessMacroTurboOriginalOutcome(
        ActionManager* actionManager,
        Candidate candidate,
        bool originalResult,
        ushort currentSequence)
    {
        var sequenceAdvanced = currentSequence != candidate.SequenceAtCapture;
        if (sequenceAdvanced)
        {
            RecordSentSequence(currentSequence, NowMilliseconds);
        }

        if (actionManager == null)
        {
            MarkTurboCaptureDisqualified(candidate);
            return;
        }

        var queueAfter = CaptureNativeQueue(actionManager);
        var nativeOutcome = NativeActionOutcomeClassifier.Classify(
            originalResult || sequenceAdvanced,
            candidate.QueueAtCapture,
            queueAfter,
            candidate.ExactTuple);
        RecordInitialTurboOutcome(
            candidate,
            nativeOutcome,
            sequenceAdvanced,
            currentSequence,
            exactQueueClaimed: false,
            allowQueuedOutcome: false);
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Macro Turbo original outcome generation={Generation}, action={Action}, outcome={Outcome}, sequenceAdvanced={SequenceAdvanced}, queueAfter={Queued}.",
                candidate.InputGeneration,
                candidate.ResolvedActionId,
                nativeOutcome,
                sequenceAdvanced,
                queueAfter.IsQueued);
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

        if (pendingMacroCapture is { } pending
            && pending.Generation == candidate.InputGeneration
            && pending.Candidate?.ExactTuple == candidate.ExactTuple)
        {
            pending.InitialAcknowledgement = seed;
            pending.Disqualified |= disqualified;
        }
    }

    private void MarkTurboCaptureDisqualified(Candidate candidate) =>
        ApplyTurboCaptureOutcome(candidate, seed: null, disqualified: true);

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
    {
        // Serialize the topology transition with the last native dispatch boundary.
        // Whichever side acquires the gate first has a single, auditable ordering.
        lock (dispatchGate)
        {
            Cancel(CancelReason.Conflict, "Plugin topology changed");
            nativeQueueOwnership.Clear();
            Interlocked.Exchange(ref pluginTopologyDirty, 1);
        }
    }

    private void MarkCompatibilityProfileDirty(string detail)
    {
        Cancel(CancelReason.Conflict, $"{detail}; reassessment scheduled");
        nativeQueueOwnership.Clear();
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
                nativeQueueOwnership.Clear();
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
                TryCompletePendingMacroCapture(now);
            }

            var observedActionManager = ActionManager.Instance();
            if (observedActionManager != null)
            {
                lock (dispatchGate)
                {
                    nativeQueueOwnership.Reconcile(
                        observedActionManager->LastUsedActionSequence,
                        CaptureNativeQueue(observedActionManager));
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
                ProcessTurbo(now, frameGap);
                return;
            }

            if (frameGap < 0 || frameGap > MaximumFrameGapMilliseconds)
            {
                Cancel(CancelReason.Expired, $"Cancelled after {frameGap} ms frame gap");
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

    private void TryCompletePendingMacroCapture(long now)
    {
        var pending = pendingMacroCapture;
        if (pending is null) return;

        if (now < 0
            || now > pending.ExpiresAtMilliseconds
            || pending.Disqualified
            || !configuration.Enabled
            || !configuration.TurboEnabled
            || !configuration.TurboMacrosEnabled
            || configuration.DryRun
            || !inputGenerations.IsCurrent(pending.Generation)
            || Volatile.Read(ref latestCertifiedPressId) != pending.Press.PressId
            || physicalHotbarInput?.IsStillHeld(pending.Press) != true)
        {
            pendingMacroCapture = null;
            LogTurboStartRejected(pending.SlotIdentity, "macro capture expired, changed, or was released");
            return;
        }

        if (IsMacroExecutionActive()) return;

        if (pending.ActionInvocationCount != 1 || pending.Candidate is not { } candidate)
        {
            pendingMacroCapture = null;
            LogTurboStartRejected(
                pending.SlotIdentity,
                $"macro produced {pending.ActionInvocationCount} eligible action calls; exactly one is required");
            return;
        }

        var scope = new HotbarInputScope(
            pending.Generation,
            pending.Press,
            pending.SlotIdentity)
        {
            ActionInvocationCount = 1,
            TurboCandidate = candidate,
            InitialAcknowledgement = pending.InitialAcknowledgement,
        };
        pendingMacroCapture = null;
        if (physicalHotbarInput is { } inputSource)
        {
            StartTurboRuntime(
                scope,
                pending.Press,
                pending.SlotIdentity,
                candidate,
                pending.Profile,
                inputSource);
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
                    nativeQueueOwnership.Clear();
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
                    var claimed = nativeQueueOwnership.TryClaimNewQueue(
                        runtime.Candidate.InputGeneration,
                        sequenceAfter,
                        queueBefore,
                        queueAfter,
                        replayTuple);
                    lastEvent = $"Replay queued action {runtime.Candidate.ResolvedActionId} once";
                    if (!claimed
                        || !BeginOneShotTurboAcknowledgement(
                            runtime.Candidate,
                            TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
                            sequenceBefore,
                            replayTuple))
                    {
                        nativeQueueOwnership.Clear();
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
                    nativeQueueOwnership.Clear();
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
            CancelTurboUnsafe(HoldRepeatCancelReason.PulseRejected, detail);
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

        if (changed)
        {
            lock (dispatchGate)
            {
                compatibilityQuarantineFrames = Math.Max(compatibilityQuarantineFrames, 1);
                Cancel(CancelReason.Conflict, "Plugin compatibility settings changed; waiting for one clean frame");
                nativeQueueOwnership.Clear();
            }
        }

        if (activeConflicts.Count > 0
            && (engine.Pending is not null || turboEngine.Snapshot.HasActiveHold))
        {
            lock (dispatchGate)
            {
                Cancel(CancelReason.Conflict, "Suspended by the current plugin compatibility profile");
                nativeQueueOwnership.Clear();
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

    private bool WasRecentlyAcknowledged(TurboAcknowledgementSeed seed)
    {
        foreach (var observed in recentLocalActionEffects)
        {
            if (observed.ObservedAtMilliseconds < seed.StartedAtMilliseconds) continue;
            if (TurboActionEffectAcknowledgementMatcher.Matches(
                    seed.Expectation,
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
            faulted = true;
            inputGenerations.Invalidate();
            engine.Cancel(CancelReason.Explicit);
            pendingRuntimeAction = null;
            CancelTurboUnsafe(HoldRepeatCancelReason.Fault, $"Faulted: {context}");
            nativeQueueOwnership.Clear();
            lastEvent = $"Faulted: {context}";
            if (!faultLogged)
            {
                faultLogged = true;
                log.Error(exception, "PulseQueue faulted closed: {Context}. Reload or explicitly reset before buffering resumes.", context);
            }
        }
    }

    private void CancelTurboUnsafe(HoldRepeatCancelReason reason, string detail)
    {
        if (reason == HoldRepeatCancelReason.None) reason = HoldRepeatCancelReason.InputLost;
        var hadActiveHold = turboEngine.Snapshot.HasActiveHold;
        turboEngine.Cancel(reason);
        turboRuntime = null;
        Interlocked.Exchange(ref turboAcknowledgement, null);
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

    private void RejectTurboPulseUnsafe(string detail)
    {
        turboRejectedCount++;
        CancelTurboUnsafe(
            HoldRepeatCancelReason.PulseRejected,
            $"{detail}; hold ended without retry");
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

    private void TryCompleteTurboAcknowledgement(ActionEffectHandler.Header* header)
    {
        if (header == null) return;
        lock (dispatchGate)
        {
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

    private string DescribeTurboState()
    {
        if (!configuration.TurboEnabled) return "Off (opt-in)";
        if (physicalHotbarInput is null)
        {
            return $"Unavailable - {turboInputUnavailableReason}";
        }

        if (!configuration.Enabled) return "Off - PulseQueue is disabled";
        if (configuration.DryRun) return "Paused - dry run never emits Turbo pulses";
        if (activeConflicts.Count > 0) return "Suspended - resolve the compatibility conflict";
        if (pendingMacroCapture is { } pending)
        {
            return $"Capturing macro hotbar {pending.SlotIdentity.Binding.HotbarId + 1}, slot {pending.SlotIdentity.Binding.SlotId + 1}";
        }

        if (Volatile.Read(ref turboAcknowledgement) is not null) return "Holding - waiting for the last exact action acknowledgement";
        return turboEngine.Snapshot.State switch
        {
            HoldRepeatState.Active when turboRuntime is { } runtime =>
                $"Holding {(runtime.IsMacro ? "captured macro action from " : string.Empty)}hotbar {runtime.SlotIdentity.Binding.HotbarId + 1}, slot {runtime.SlotIdentity.Binding.SlotId + 1}",
            HoldRepeatState.NeedsRelease => "Ready for a fresh physical key press",
            _ => "Ready - no held keyboard slot",
        };
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

        public bool MacroWasLockedBeforeExecution { get; set; }

        public TurboAcknowledgementSeed? InitialAcknowledgement { get; set; }
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
        HoldRepeatStartRequest StartRequest)
    {
        public bool IsMacro => MacroProfile is not null;
    }

    private sealed class PendingMacroCapture(
        CertifiedHotbarPress press,
        HotbarSlotIdentity slotIdentity,
        SafeActionMacroProfile profile,
        long generation,
        long expiresAtMilliseconds)
    {
        public CertifiedHotbarPress Press { get; } = press;

        public HotbarSlotIdentity SlotIdentity { get; } = slotIdentity;

        public SafeActionMacroProfile Profile { get; } = profile;

        public long Generation { get; } = generation;

        public long ExpiresAtMilliseconds { get; } = expiresAtMilliseconds;

        public Candidate? Candidate { get; set; }

        public int ActionInvocationCount { get; set; }

        public bool Disqualified { get; set; }

        public TurboAcknowledgementSeed? InitialAcknowledgement { get; set; }
    }

    private sealed record TurboAcknowledgementSeed(
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
