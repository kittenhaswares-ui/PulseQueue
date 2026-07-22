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
    private const int MaximumActionEffectTargets = 32;
    private const byte KnockbackActionEffectType = 33;
    private const int CompatibilityPollIntervalMilliseconds = 500;
    private const uint ReActionCameraRelativeMovementException = 29494;

    [ThreadStatic]
    private static int hotbarExecutionDepth;

    [ThreadStatic]
    private static bool replaying;

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
    private readonly Hook<ActionManager.Delegates.UseAction> useActionHook;
    private readonly Hook<RaptureHotbarModule.Delegates.ExecuteSlot> executeSlotHook;
    private readonly Hook<RaptureHotbarModule.Delegates.ExecuteSlotById> executeSlotByIdHook;
    private readonly Hook<ActionEffectHandler.Delegates.Receive> receiveActionEffectHook;

    private RuntimeAction? pendingRuntimeAction;
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
    private uint localEntityId;
    private int forcedMovementObserved;
    private int timingHookErrorLogged;
    private bool faulted;
    private bool faultLogged;
    private bool disposed;
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
            framework.Update += OnFrameworkUpdate;
            pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
            log.Information("PulseQueue hooks enabled. Buffer cap is {Cap} ms.", BufferEngine.AbsoluteHoldCapMilliseconds);
        }
        catch
        {
            pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
            framework.Update -= OnFrameworkUpdate;
            DisposeSilently(useActionHook);
            DisposeSilently(receiveActionEffectHook);
            DisposeSilently(executeSlotByIdHook);
            DisposeSilently(executeSlotHook);
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
        sentSequences.Clear();
    }

    private byte ExecuteSlotDetour(RaptureHotbarModule* thisPtr, RaptureHotbarModule.HotbarSlot* slot)
    {
        var rootInput = hotbarExecutionDepth == 0 && !replaying;
        if (rootInput)
        {
            BeginHotbarInput();
        }

        hotbarExecutionDepth++;
        try
        {
            return executeSlotHook.Original(thisPtr, slot);
        }
        finally
        {
            hotbarExecutionDepth--;
            if (rootInput)
            {
                CompleteHotbarInput();
            }
        }
    }

    private byte ExecuteSlotByIdDetour(RaptureHotbarModule* thisPtr, uint hotbarId, uint slotId)
    {
        var rootInput = hotbarExecutionDepth == 0 && !replaying;
        if (rootInput)
        {
            BeginHotbarInput();
        }

        hotbarExecutionDepth++;
        try
        {
            return executeSlotByIdHook.Original(thisPtr, hotbarId, slotId);
        }
        finally
        {
            hotbarExecutionDepth--;
            if (rootInput)
            {
                CompleteHotbarInput();
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
        var certifiedHotbarInput = hotbarExecutionDepth > 0 && !replaying;
        var sequenceBefore = thisPtr == null ? (ushort)0 : thisPtr->LastUsedActionSequence;

        if (!replaying)
        {
            lock (dispatchGate)
            {
                // The outer hotbar hook already invalidated the previous generation before
                // this call. Independent/native invocations still clear it here.
                if (!certifiedHotbarInput)
                {
                    Cancel(CancelReason.Replaced, "Cleared by another native action invocation");
                }

                if (certifiedHotbarInput)
                {
                    try
                    {
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

    private void BeginHotbarInput()
    {
        var replacedPending = engine.Pending is not null;
        observedHotbarInputCount++;
        if (replacedPending) replacedPendingCount++;
        Cancel(CancelReason.Replaced, "Replaced by the newest hotbar input");
        activeHotbarInput = new HotbarInputScope(inputGenerations.Current);
        if (configuration.DetailedLogging)
        {
            log.Debug(
                "Observed hotbar input generation={Generation}, replacedPending={ReplacedPending}.",
                inputGenerations.Current,
                replacedPending);
        }
    }

    private void CompleteHotbarInput()
    {
        var scope = activeHotbarInput;
        activeHotbarInput = null;
        if (scope is not { MaySupersedeOwnedQueue: true }) return;

        try
        {
            lock (dispatchGate)
            {
                if (!inputGenerations.IsCurrent(scope.Generation)) return;
                var actionManager = ActionManager.Instance();
                if (actionManager == null) return;
                TryReplaceOwnedNativeQueue(actionManager, scope.Generation, "after the complete hotbar call");
            }
        }
        catch (Exception exception)
        {
            Fault(exception, "Hotbar completion validation failed");
        }
    }

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
        uint comboRouteId)
    {
        if (disposed || faulted || !configuration.Enabled || actionManager == null || mode != ActionManager.UseActionMode.None)
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
                if (!sequenceAdvanced)
                {
                    nativeQueueOwnership.TryClaimNewQueue(
                        candidate.InputGeneration,
                        currentSequence,
                        candidate.QueueAtCapture,
                        queueAfter,
                        candidate.ExactTuple);
                }

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

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs _)
    {
        // Serialize the topology transition with the last native dispatch boundary.
        // Whichever side acquires the gate first has a single, auditable ordering.
        lock (dispatchGate)
        {
            inputGenerations.Invalidate();
            engine.Cancel(CancelReason.Conflict);
            pendingRuntimeAction = null;
            nativeQueueOwnership.Clear();
            lastEvent = "Plugin topology changed";
            Interlocked.Exchange(ref pluginTopologyDirty, 1);
        }
    }

    private void MarkCompatibilityProfileDirty(string detail)
    {
        inputGenerations.Invalidate();
        engine.Cancel(CancelReason.Conflict);
        pendingRuntimeAction = null;
        nativeQueueOwnership.Clear();
        lastEvent = $"{detail}; reassessment scheduled";
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
            DrainTimingSamples();
            DrainTimingHookErrors();

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
                if (engine.Pending is not null)
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

                dispatchedCount++;
                if (sequenceAfter != sequenceBefore)
                {
                    nativeQueueOwnership.Clear();
                    RecordSentSequence(sequenceAfter, NowMilliseconds);
                    lastEvent = $"Dispatched action {runtime.Candidate.ResolvedActionId} once";
                }
                else if (queueAfter.Matches(replayTuple) && !queueBefore.Matches(replayTuple))
                {
                    nativeQueueOwnership.TryClaimNewQueue(
                        runtime.Candidate.InputGeneration,
                        sequenceAfter,
                        queueBefore,
                        queueAfter,
                        replayTuple);
                    lastEvent = $"Replay queued action {runtime.Candidate.ResolvedActionId} once";
                }
                else if (accepted)
                {
                    nativeQueueOwnership.Clear();
                    lastEvent = $"Replay accepted action {runtime.Candidate.ResolvedActionId} once";
                }
                else
                {
                    replayRejectedCount++;
                    lastEvent = $"One-shot replay rejected for {runtime.Candidate.ResolvedActionId}; no retry";
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
                        inputGenerations.Invalidate();
                        engine.Cancel(CancelReason.Knockback);
                        pendingRuntimeAction = null;
                        lastEvent = "Cleared by a local-player knockback action effect";
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

        if (activeConflicts.Count > 0 && engine.Pending is not null)
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
            nativeQueueOwnership.Clear();
            lastEvent = $"Faulted: {context}";
            if (!faultLogged)
            {
                faultLogged = true;
                log.Error(exception, "PulseQueue faulted closed: {Context}. Reload or explicitly reset before buffering resumes.", context);
            }
        }
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

    private sealed class HotbarInputScope(long generation)
    {
        public long Generation { get; } = generation;

        public bool MaySupersedeOwnedQueue { get; set; }
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
