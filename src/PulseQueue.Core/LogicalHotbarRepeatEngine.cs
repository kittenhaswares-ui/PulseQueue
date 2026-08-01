namespace PulseQueue.Core;

public enum LogicalHotbarRepeatDecisionKind
{
    None = 0,
    PhysicalPress,
    InjectedRepeat,
    DelegatedRepeat,
    SuppressedOlderHold,
    Released,
}

public readonly record struct LogicalHotbarRepeatOptions(
    int InitialDelayMilliseconds,
    int RepeatIntervalMilliseconds)
{
    public const int MinimumRepeatIntervalMilliseconds = 0;
    public const int MaximumInitialDelayMilliseconds = 1_000;
    public const int MaximumRepeatIntervalMilliseconds = 1_000;

    public static LogicalHotbarRepeatOptions Default => new(
        InitialDelayMilliseconds: 0,
        RepeatIntervalMilliseconds: MinimumRepeatIntervalMilliseconds);

    public LogicalHotbarRepeatOptions Normalize() => new(
        Math.Clamp(InitialDelayMilliseconds, 0, MaximumInitialDelayMilliseconds),
        Math.Clamp(
            RepeatIntervalMilliseconds,
            MinimumRepeatIntervalMilliseconds,
            MaximumRepeatIntervalMilliseconds));
}

/// <summary>
/// One observation of a stable logical hotbar input. <see cref="NativePressed"/>
/// represents a press already produced by the game or another repeat owner;
/// <see cref="Held"/> is the physical control's current level.
/// </summary>
public readonly record struct LogicalHotbarRepeatObservation(
    long LogicalInputId,
    bool NativePressed,
    bool Held,
    long NowMilliseconds,
    bool RepeatEnabled,
    bool ExternalRepeatOwnerActive)
{
    public bool IsValid => LogicalInputId > 0 && NowMilliseconds >= 0;
}

public readonly record struct LogicalHotbarRepeatCounters(
    long Observations,
    long PhysicalPresses,
    long HoldsClaimed,
    long HoldsPreempted,
    long InjectedRepeats,
    long DelegatedRepeats,
    long SuppressedOlderHolds,
    long Releases);

public readonly record struct LogicalHotbarRepeatDecision(
    LogicalHotbarRepeatDecisionKind Kind,
    bool ShouldReportPressed,
    bool IsFreshPhysicalEdge,
    long OwnerLogicalInputId,
    LogicalHotbarRepeatCounters Counters);

public readonly record struct LogicalHotbarRepeatSnapshot(
    long OwnerLogicalInputId,
    long NextRepeatAtMilliseconds,
    LogicalHotbarRepeatCounters Counters)
{
    public bool HasOwner => OwnerLogicalInputId > 0;
}

/// <summary>
/// Dependency-free logical-input repeat arbiter. It owns at most one hold, never
/// suppresses a genuine new physical press, and serializes every observation so
/// a due instant can produce at most one repeat signal. External repeat pulses
/// are observed for provenance but never become a new physical owner. Every
/// observed current-owner pulse moves the fallback deadline forward by one
/// interval, so PulseQueue fills gaps instead of creating a second repeat stream.
/// A continuously held, preempted input remains suppressed until it is released.
/// </summary>
public sealed class LogicalHotbarRepeatEngine
{
    private readonly object gate = new();
    private LogicalHotbarRepeatOptions options;
    private readonly Dictionary<long, InputState> inputs = [];
    private long ownerLogicalInputId;
    private long nextRepeatAtMilliseconds;
    private long lastRepeatSignalAtMilliseconds = -1;
    private long observations;
    private long physicalPresses;
    private long holdsClaimed;
    private long holdsPreempted;
    private long injectedRepeats;
    private long delegatedRepeats;
    private long suppressedOlderHolds;
    private long releases;

    public LogicalHotbarRepeatEngine(LogicalHotbarRepeatOptions options)
    {
        this.options = options.Normalize();
    }

    public LogicalHotbarRepeatEngine()
        : this(LogicalHotbarRepeatOptions.Default)
    {
    }

    public LogicalHotbarRepeatOptions Options
    {
        get
        {
            lock (gate) return options;
        }
    }

    public LogicalHotbarRepeatSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return new LogicalHotbarRepeatSnapshot(
                    ownerLogicalInputId,
                    ownerLogicalInputId > 0 ? nextRepeatAtMilliseconds : 0,
                    CreateCounters());
            }
        }
    }

    /// <summary>
    /// Cancels the current repeat owner and release-gates every logical input
    /// that is known to still be held. A gated input cannot become an owner or
    /// surface either a local or delegated repeat until a subsequent
    /// <c>Held == false</c> observation has been processed for that exact input.
    /// Inputs already observed released remain eligible for their next genuine
    /// physical press.
    /// </summary>
    /// <returns>The number of currently held logical inputs that were gated.</returns>
    public int CancelAndRequireRelease()
    {
        lock (gate)
        {
            return CancelAndRequireReleaseLocked();
        }
    }

    /// <summary>
    /// Applies new timing and atomically starts the same release-gated lifecycle
    /// boundary as <see cref="CancelAndRequireRelease"/>. Retaining the known
    /// held-input state prevents a timing change from turning an already-held
    /// control into a fresh owner or delegated repeat.
    /// </summary>
    /// <returns>The number of currently held logical inputs release-gated.</returns>
    public int ReconfigureAndRequireRelease(LogicalHotbarRepeatOptions newOptions)
    {
        lock (gate)
        {
            options = newOptions.Normalize();
            return CancelAndRequireReleaseLocked();
        }
    }

    public LogicalHotbarRepeatDecision Observe(LogicalHotbarRepeatObservation observation)
    {
        if (observation.LogicalInputId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.LogicalInputId,
                "The logical input ID must be positive.");
        }

        if (observation.NowMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.NowMilliseconds,
                "The observation time cannot be negative.");
        }

        lock (gate)
        {
            observations++;
            if (observation.NativePressed)
            {
                physicalPresses++;
            }

            if (!inputs.TryGetValue(observation.LogicalInputId, out var input))
            {
                input = new InputState();
                inputs.Add(observation.LogicalInputId, input);
            }

            if (!observation.Held)
            {
                return ObserveReleased(observation, input);
            }

            var wasHeld = input.Held;
            input.Held = true;

            if (input.SuppressedUntilRelease)
            {
                suppressedOlderHolds++;
                return Decision(
                    LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
                    shouldReportPressed: false);
            }

            // The first native press seen for an eligible, non-suppressed held
            // input is sufficient to claim it. Requiring a prior per-binding
            // release made valid first presses inert after startup, hook-order
            // changes and compatibility refreshes. A preempted older hold is
            // still protected by SuppressedUntilRelease above.
            if (observation.NativePressed
                && ownerLogicalInputId != observation.LogicalInputId)
            {
                // A native pulse from another input that was already known held
                // is typematic/external continuation, not a newer physical edge.
                // Once another input owns cadence it cannot steal ownership.
                if (ownerLogicalInputId > 0 && wasHeld)
                {
                    input.SuppressedUntilRelease = true;
                    suppressedOlderHolds++;
                    return Decision(
                        LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
                        shouldReportPressed: false);
                }

                ClaimNewestOwner(observation, input);
                return Decision(
                    LogicalHotbarRepeatDecisionKind.PhysicalPress,
                    shouldReportPressed: true,
                    isFreshPhysicalEdge: true);
            }

            if (ownerLogicalInputId != observation.LogicalInputId)
            {
                return Decision(
                    observation.NativePressed
                        ? LogicalHotbarRepeatDecisionKind.PhysicalPress
                        : LogicalHotbarRepeatDecisionKind.None,
                    observation.NativePressed);
            }

            if (observation.NativePressed)
            {
                // A native/external pressed result already executes this exact
                // binding for the current scan. Restart the fallback interval
                // from that real pulse. If the external source keeps working,
                // PulseQueue stays silent; if it stops or excludes this binding,
                // PulseQueue fills the first missing interval.
                lastRepeatSignalAtMilliseconds = observation.NowMilliseconds;
                nextRepeatAtMilliseconds = SaturatingAdd(
                    observation.NowMilliseconds,
                    options.RepeatIntervalMilliseconds);

                if (observation.ExternalRepeatOwnerActive)
                {
                    delegatedRepeats++;
                    return Decision(
                        LogicalHotbarRepeatDecisionKind.DelegatedRepeat,
                        shouldReportPressed: true);
                }

                return Decision(
                    LogicalHotbarRepeatDecisionKind.PhysicalPress,
                    shouldReportPressed: true);
            }

            if (!observation.RepeatEnabled
                || observation.NowMilliseconds < nextRepeatAtMilliseconds
                || (options.RepeatIntervalMilliseconds == 0
                    && observation.NowMilliseconds == lastRepeatSignalAtMilliseconds))
            {
                return Decision(LogicalHotbarRepeatDecisionKind.None, shouldReportPressed: false);
            }

            injectedRepeats++;
            lastRepeatSignalAtMilliseconds = observation.NowMilliseconds;
            nextRepeatAtMilliseconds = SaturatingAdd(
                observation.NowMilliseconds,
                options.RepeatIntervalMilliseconds);
            return Decision(
                LogicalHotbarRepeatDecisionKind.InjectedRepeat,
                shouldReportPressed: true);
        }
    }

    /// <summary>
    /// Coalesces a repeat that an outer hook produced after this engine returned
    /// its native result. It can move only the current, still-held owner's
    /// deadline and can never claim or preempt ownership.
    /// </summary>
    public bool CoalesceExternalExecution(long logicalInputId, long nowMilliseconds)
    {
        if (logicalInputId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalInputId),
                logicalInputId,
                "The logical input ID must be positive.");
        }

        if (nowMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nowMilliseconds),
                nowMilliseconds,
                "The observation time cannot be negative.");
        }

        lock (gate)
        {
            if (ownerLogicalInputId != logicalInputId
                || !inputs.TryGetValue(logicalInputId, out var input)
                || !input.Held
                || input.SuppressedUntilRelease)
            {
                return false;
            }

            lastRepeatSignalAtMilliseconds = nowMilliseconds;
            nextRepeatAtMilliseconds = SaturatingAdd(
                nowMilliseconds,
                options.RepeatIntervalMilliseconds);
            return true;
        }
    }

    private LogicalHotbarRepeatDecision ObserveReleased(
        LogicalHotbarRepeatObservation observation,
        InputState input)
    {
        var releaseEdge = !input.ReleaseObserved || input.Held || input.SuppressedUntilRelease;
        input.Held = false;
        input.ReleaseObserved = true;
        input.SuppressedUntilRelease = false;

        if (ownerLogicalInputId == observation.LogicalInputId)
        {
            ownerLogicalInputId = 0;
            nextRepeatAtMilliseconds = 0;
            lastRepeatSignalAtMilliseconds = -1;
            releaseEdge = true;
        }

        if (releaseEdge)
        {
            releases++;
        }

        if (observation.NativePressed)
        {
            if (ownerLogicalInputId > 0
                && ownerLogicalInputId != observation.LogicalInputId)
            {
                SuppressOtherHeldInputs(observation.LogicalInputId);
                holdsPreempted++;
                ownerLogicalInputId = 0;
                nextRepeatAtMilliseconds = 0;
                lastRepeatSignalAtMilliseconds = -1;
            }

            return Decision(
                LogicalHotbarRepeatDecisionKind.PhysicalPress,
                shouldReportPressed: true,
                isFreshPhysicalEdge: true);
        }

        return Decision(
            releaseEdge
                ? LogicalHotbarRepeatDecisionKind.Released
                : LogicalHotbarRepeatDecisionKind.None,
            shouldReportPressed: false);
    }

    private int CancelAndRequireReleaseLocked()
    {
        var gatedInputs = 0;
        foreach (var input in inputs.Values)
        {
            if (!input.Held) continue;

            input.ReleaseObserved = false;
            input.SuppressedUntilRelease = true;
            gatedInputs++;
        }

        ownerLogicalInputId = 0;
        nextRepeatAtMilliseconds = 0;
        lastRepeatSignalAtMilliseconds = -1;
        return gatedInputs;
    }

    private void ClaimNewestOwner(
        LogicalHotbarRepeatObservation observation,
        InputState input)
    {
        if (ownerLogicalInputId > 0 && ownerLogicalInputId != observation.LogicalInputId)
        {
            if (inputs.TryGetValue(ownerLogicalInputId, out var previousOwner))
            {
                previousOwner.SuppressedUntilRelease = previousOwner.Held;
            }

            holdsPreempted++;
        }

        SuppressOtherHeldInputs(observation.LogicalInputId);

        ownerLogicalInputId = observation.LogicalInputId;
        input.ReleaseObserved = false;
        input.SuppressedUntilRelease = false;
        nextRepeatAtMilliseconds = SaturatingAdd(
            observation.NowMilliseconds,
            options.InitialDelayMilliseconds > 0
                ? options.InitialDelayMilliseconds
                : options.RepeatIntervalMilliseconds);
        lastRepeatSignalAtMilliseconds = observation.NowMilliseconds;
        holdsClaimed++;
    }

    private void SuppressOtherHeldInputs(long newestLogicalInputId)
    {
        foreach (var pair in inputs)
        {
            if (pair.Key == newestLogicalInputId || !pair.Value.Held) continue;
            pair.Value.SuppressedUntilRelease = true;
        }
    }

    private LogicalHotbarRepeatDecision Decision(
        LogicalHotbarRepeatDecisionKind kind,
        bool shouldReportPressed,
        bool isFreshPhysicalEdge = false) =>
        new(kind, shouldReportPressed, isFreshPhysicalEdge, ownerLogicalInputId, CreateCounters());

    private LogicalHotbarRepeatCounters CreateCounters() => new(
        observations,
        physicalPresses,
        holdsClaimed,
        holdsPreempted,
        injectedRepeats,
        delegatedRepeats,
        suppressedOlderHolds,
        releases);

    private static long SaturatingAdd(long value, int delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;

    private sealed class InputState
    {
        public bool Held;

        public bool ReleaseObserved;

        public bool SuppressedUntilRelease;
    }
}
