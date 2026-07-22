namespace PulseQueue.Core;

public enum HoldRepeatState
{
    NeedsRelease = 0,
    Idle,
    Active,
}

public enum HoldRepeatStartResult
{
    Started = 0,
    Replaced,
    RejectedNeedsRelease,
    RejectedUncertified,
    RejectedInvalid,
    RejectedStale,
}

public enum HoldRepeatCancelReason
{
    None = 0,
    Released,
    Replaced,
    Disabled,
    Conflict,
    Logout,
    Death,
    Mounted,
    Stun,
    Knockback,
    TerritoryChange,
    InstanceChange,
    TargetChange,
    ResolvedActionChange,
    BindingChange,
    PluginChange,
    InputLost,
    PulseRejected,
    Fault,
    MaximumDuration,
}

public enum HoldRepeatDecisionKind
{
    None = 0,
    Pulse,
    Cancelled,
}

/// <summary>
/// A physical-input observation captured before entering the hold-repeat engine.
/// The runtime is responsible for setting <see cref="IsCertifiedFreshPress"/> only
/// after observing a genuine released-to-pressed transition for the exact control.
/// </summary>
public readonly record struct HoldRepeatStartRequest(
    long PressId,
    long InputGeneration,
    ulong ControlFingerprint,
    ulong IntentFingerprint,
    bool IsCertifiedFreshPress)
{
    public bool IsValid =>
        PressId > 0
        && InputGeneration > 0
        && ControlFingerprint != 0
        && IntentFingerprint != 0;
}

public readonly record struct HoldRepeatOptions(
    int InitialDelayMilliseconds,
    int IntervalMilliseconds,
    int MaximumHoldMilliseconds)
{
    public const int MinimumTimingMilliseconds = 60;
    public const int MaximumInitialDelayMilliseconds = 1_000;
    public const int MaximumIntervalMilliseconds = 1_000;
    public const int MinimumHoldDurationMilliseconds = 1_000;
    public const int AbsoluteMaximumHoldMilliseconds = 30_000;

    public static HoldRepeatOptions Default => new(
        InitialDelayMilliseconds: 0,
        IntervalMilliseconds: MinimumTimingMilliseconds,
        MaximumHoldMilliseconds: AbsoluteMaximumHoldMilliseconds);

    public HoldRepeatOptions Normalize()
    {
        var maximumHold = Math.Clamp(
            MaximumHoldMilliseconds,
            MinimumHoldDurationMilliseconds,
            AbsoluteMaximumHoldMilliseconds);
        return new HoldRepeatOptions(
            Math.Clamp(InitialDelayMilliseconds, 0, MaximumInitialDelayMilliseconds),
            Math.Clamp(IntervalMilliseconds, MinimumTimingMilliseconds, MaximumIntervalMilliseconds),
            maximumHold);
    }
}

/// <summary>
/// The complete fail-closed safety observation for one active physical hold.
/// </summary>
public readonly record struct HoldRepeatSafetyState(
    bool Enabled,
    bool ConflictDetected,
    bool LoggedIn,
    bool IsAlive,
    bool IsMounted,
    bool IsStunned,
    bool IsKnockbackActive,
    bool PhysicalControlDown,
    bool ReleaseObserved,
    bool TerritoryMatches,
    bool InstanceMatches,
    bool TargetMatches,
    bool ResolvedActionMatches,
    bool BindingMatches,
    bool PluginStateMatches,
    bool Faulted)
{
    public static HoldRepeatSafetyState SafeHeld => new(
        Enabled: true,
        ConflictDetected: false,
        LoggedIn: true,
        IsAlive: true,
        IsMounted: false,
        IsStunned: false,
        IsKnockbackActive: false,
        PhysicalControlDown: true,
        ReleaseObserved: false,
        TerritoryMatches: true,
        InstanceMatches: true,
        TargetMatches: true,
        ResolvedActionMatches: true,
        BindingMatches: true,
        PluginStateMatches: true,
        Faulted: false);
}

/// <summary>
/// One immutable permission to attempt one repeat pulse. Tokens are current only
/// while their hold is active and they remain that hold's newest issued ordinal.
/// </summary>
public readonly record struct HoldRepeatPulseToken(
    long HoldId,
    long PressId,
    long InputGeneration,
    long Ordinal,
    ulong ControlFingerprint,
    ulong IntentFingerprint,
    long IssuedAtMilliseconds)
{
    public bool IsValid =>
        HoldId > 0
        && PressId > 0
        && InputGeneration > 0
        && Ordinal > 0
        && ControlFingerprint != 0
        && IntentFingerprint != 0;
}

public readonly record struct HoldRepeatDecision(
    HoldRepeatDecisionKind Kind,
    HoldRepeatPulseToken Pulse,
    HoldRepeatCancelReason CancelReason)
{
    public static HoldRepeatDecision None => default;

    public static HoldRepeatDecision ForPulse(HoldRepeatPulseToken pulse) =>
        new(HoldRepeatDecisionKind.Pulse, pulse, HoldRepeatCancelReason.None);

    public static HoldRepeatDecision ForCancellation(HoldRepeatCancelReason reason) =>
        new(HoldRepeatDecisionKind.Cancelled, default, reason);
}

public readonly record struct HoldRepeatCounters(
    long StartAttempts,
    long HoldsStarted,
    long HoldsReplaced,
    long RejectedStarts,
    long ReleaseObservations,
    long PulsesIssued,
    long ReadinessSuppressions,
    long RateLimitSuppressions,
    long HoldsCancelled);

public readonly record struct HoldRepeatSnapshot(
    HoldRepeatState State,
    long HoldId,
    long PressId,
    long InputGeneration,
    long LastIssuedOrdinal,
    long StartedAtMilliseconds,
    long NextPulseAtMilliseconds,
    HoldRepeatCancelReason LastCancelReason,
    HoldRepeatCounters Counters)
{
    public bool HasActiveHold => State == HoldRepeatState.Active && HoldId > 0;
}

/// <summary>
/// Dependency-free single-hold repeat state machine. Every mutation is serialized
/// so concurrent ticks can issue at most one token for a due instant.
/// </summary>
public sealed class HoldRepeatEngine
{
    private readonly object gate = new();
    private readonly HoldRepeatOptions options;
    private HoldRepeatState state = HoldRepeatState.NeedsRelease;
    private ActiveHold? active;
    private long nextHoldId;
    private long lastAcceptedPressId;
    private long lastAcceptedGeneration;
    private HoldRepeatCancelReason lastCancelReason;
    private long startAttempts;
    private long holdsStarted;
    private long holdsReplaced;
    private long rejectedStarts;
    private long releaseObservations;
    private long pulsesIssued;
    private long readinessSuppressions;
    private long rateLimitSuppressions;
    private long holdsCancelled;

    public HoldRepeatEngine(HoldRepeatOptions options)
    {
        this.options = options.Normalize();
    }

    public HoldRepeatEngine()
        : this(HoldRepeatOptions.Default)
    {
    }

    public HoldRepeatOptions Options => options;

    public HoldRepeatSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return CreateSnapshot();
            }
        }
    }

    /// <summary>
    /// Records a real released/up observation. An active hold is terminal; an
    /// engine waiting for release becomes eligible for a later fresh press.
    /// </summary>
    public bool ObserveRelease()
    {
        lock (gate)
        {
            releaseObservations++;
            var terminated = active is not null;
            if (terminated)
            {
                Terminate(HoldRepeatCancelReason.Released, HoldRepeatState.Idle);
            }
            else
            {
                state = HoldRepeatState.Idle;
                lastCancelReason = HoldRepeatCancelReason.Released;
            }

            return terminated;
        }
    }

    public HoldRepeatStartResult TryStart(
        HoldRepeatStartRequest request,
        long nowMilliseconds)
    {
        lock (gate)
        {
            startAttempts++;
            if (!request.IsCertifiedFreshPress)
            {
                rejectedStarts++;
                return HoldRepeatStartResult.RejectedUncertified;
            }

            if (!request.IsValid || nowMilliseconds < 0)
            {
                rejectedStarts++;
                return HoldRepeatStartResult.RejectedInvalid;
            }

            if (state == HoldRepeatState.NeedsRelease)
            {
                rejectedStarts++;
                return HoldRepeatStartResult.RejectedNeedsRelease;
            }

            if (request.PressId <= lastAcceptedPressId
                || request.InputGeneration <= lastAcceptedGeneration)
            {
                rejectedStarts++;
                return HoldRepeatStartResult.RejectedStale;
            }

            var replaced = active is not null;
            if (replaced)
            {
                holdsReplaced++;
                holdsCancelled++;
                lastCancelReason = HoldRepeatCancelReason.Replaced;
            }

            var holdId = NextPositive(ref nextHoldId);
            active = new ActiveHold(
                holdId,
                request,
                nowMilliseconds,
                SaturatingAdd(nowMilliseconds, options.InitialDelayMilliseconds));
            state = HoldRepeatState.Active;
            lastAcceptedPressId = request.PressId;
            lastAcceptedGeneration = request.InputGeneration;
            holdsStarted++;
            return replaced
                ? HoldRepeatStartResult.Replaced
                : HoldRepeatStartResult.Started;
        }
    }

    /// <summary>
    /// Evaluates one framework tick. Even after a long gap this method emits at
    /// most one token and schedules the next pulse from now, never from stale due
    /// times, so missed intervals cannot become a catch-up burst.
    /// </summary>
    public HoldRepeatDecision Tick(
        long nowMilliseconds,
        HoldRepeatSafetyState safety,
        bool actionReady)
    {
        lock (gate)
        {
            if (active is not { } current)
            {
                return HoldRepeatDecision.None;
            }

            if (nowMilliseconds < current.LastObservedAtMilliseconds)
            {
                Terminate(HoldRepeatCancelReason.Fault, HoldRepeatState.NeedsRelease);
                return HoldRepeatDecision.ForCancellation(HoldRepeatCancelReason.Fault);
            }

            current.LastObservedAtMilliseconds = nowMilliseconds;
            var safetyCancellation = GetCancellationReason(safety);
            if (safetyCancellation != HoldRepeatCancelReason.None)
            {
                var nextState = safetyCancellation == HoldRepeatCancelReason.Released
                    ? HoldRepeatState.Idle
                    : HoldRepeatState.NeedsRelease;
                Terminate(safetyCancellation, nextState);
                return HoldRepeatDecision.ForCancellation(safetyCancellation);
            }

            if (nowMilliseconds >= SaturatingAdd(current.StartedAtMilliseconds, options.MaximumHoldMilliseconds))
            {
                Terminate(HoldRepeatCancelReason.MaximumDuration, HoldRepeatState.NeedsRelease);
                return HoldRepeatDecision.ForCancellation(HoldRepeatCancelReason.MaximumDuration);
            }

            if (!actionReady)
            {
                readinessSuppressions++;
                return HoldRepeatDecision.None;
            }

            if (nowMilliseconds < current.NextPulseAtMilliseconds)
            {
                rateLimitSuppressions++;
                return HoldRepeatDecision.None;
            }

            var ordinal = NextPositive(ref current.LastIssuedOrdinal);
            var pulse = new HoldRepeatPulseToken(
                current.HoldId,
                current.Request.PressId,
                current.Request.InputGeneration,
                ordinal,
                current.Request.ControlFingerprint,
                current.Request.IntentFingerprint,
                nowMilliseconds);
            current.NextPulseAtMilliseconds = SaturatingAdd(nowMilliseconds, options.IntervalMilliseconds);
            pulsesIssued++;
            return HoldRepeatDecision.ForPulse(pulse);
        }
    }

    /// <summary>
    /// Invalidates the active hold and every pulse token issued from it. Safety
    /// cancellations require a release observation before another hold can start.
    /// </summary>
    public bool Cancel(HoldRepeatCancelReason reason)
    {
        if (reason == HoldRepeatCancelReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        lock (gate)
        {
            var terminated = active is not null;
            var nextState = reason == HoldRepeatCancelReason.Released
                ? HoldRepeatState.Idle
                : HoldRepeatState.NeedsRelease;
            if (terminated)
            {
                Terminate(reason, nextState);
            }
            else
            {
                state = nextState;
                lastCancelReason = reason;
            }

            return terminated;
        }
    }

    public bool IsTokenCurrent(HoldRepeatPulseToken token)
    {
        if (!token.IsValid)
        {
            return false;
        }

        lock (gate)
        {
            return active is { } current
                && state == HoldRepeatState.Active
                && current.HoldId == token.HoldId
                && current.Request.PressId == token.PressId
                && current.Request.InputGeneration == token.InputGeneration
                && current.Request.ControlFingerprint == token.ControlFingerprint
                && current.Request.IntentFingerprint == token.IntentFingerprint
                && current.LastIssuedOrdinal == token.Ordinal
                && token.IssuedAtMilliseconds <= current.LastObservedAtMilliseconds;
        }
    }

    private HoldRepeatSnapshot CreateSnapshot()
    {
        var counters = new HoldRepeatCounters(
            startAttempts,
            holdsStarted,
            holdsReplaced,
            rejectedStarts,
            releaseObservations,
            pulsesIssued,
            readinessSuppressions,
            rateLimitSuppressions,
            holdsCancelled);
        return active is { } current
            ? new HoldRepeatSnapshot(
                state,
                current.HoldId,
                current.Request.PressId,
                current.Request.InputGeneration,
                current.LastIssuedOrdinal,
                current.StartedAtMilliseconds,
                current.NextPulseAtMilliseconds,
                lastCancelReason,
                counters)
            : new HoldRepeatSnapshot(
                state,
                HoldId: 0,
                PressId: 0,
                InputGeneration: 0,
                LastIssuedOrdinal: 0,
                StartedAtMilliseconds: 0,
                NextPulseAtMilliseconds: 0,
                lastCancelReason,
                counters);
    }

    private void Terminate(HoldRepeatCancelReason reason, HoldRepeatState nextState)
    {
        active = null;
        state = nextState;
        lastCancelReason = reason;
        holdsCancelled++;
    }

    private static HoldRepeatCancelReason GetCancellationReason(HoldRepeatSafetyState safety)
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
        return HoldRepeatCancelReason.None;
    }

    private static long NextPositive(ref long value)
    {
        if (value == long.MaxValue)
        {
            throw new InvalidOperationException("The hold-repeat sequence is exhausted.");
        }

        value++;
        return value;
    }

    private static long SaturatingAdd(long value, int delta) =>
        value > long.MaxValue - delta ? long.MaxValue : value + delta;

    private sealed class ActiveHold(
        long holdId,
        HoldRepeatStartRequest request,
        long startedAtMilliseconds,
        long nextPulseAtMilliseconds)
    {
        public long HoldId { get; } = holdId;

        public HoldRepeatStartRequest Request { get; } = request;

        public long StartedAtMilliseconds { get; } = startedAtMilliseconds;

        public long NextPulseAtMilliseconds { get; set; } = nextPulseAtMilliseconds;

        public long LastObservedAtMilliseconds { get; set; } = startedAtMilliseconds;

        public long LastIssuedOrdinal;
    }
}
