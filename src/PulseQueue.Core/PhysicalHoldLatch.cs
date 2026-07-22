namespace PulseQueue.Core;

/// <summary>
/// The stable physical key and exact raw chord observed for one logical binding.
/// The fingerprint is supplied by the platform adapter and should include every
/// modifier that distinguishes one chord from another.
/// </summary>
public readonly record struct RawPhysicalChord(
    int PhysicalKey,
    ulong ChordFingerprint)
{
    public bool IsValid => PhysicalKey > 0 && ChordFingerprint != 0;
}

/// <summary>
/// One observation made for a single logical binding. Logical state is retained
/// for diagnostics, but only raw state is permitted to release a physical hold.
/// </summary>
public readonly record struct PhysicalHoldObservation(
    RawPhysicalChord Chord,
    bool LogicalPressed,
    bool LogicalDown,
    bool RawPressed,
    bool RawDown);

public enum PhysicalHoldLatchState
{
    Idle = 0,
    NeedsRelease,
    Latched,
}

public enum PhysicalHoldDecisionKind
{
    None = 0,
    Fresh,
    HeldContinuation,
    Released,
    Untrusted,
}

/// <summary>
/// Classification of an observed input. A held continuation deliberately keeps
/// the existing press identity; adapters must not treat it as a replacement or
/// use it to restart an active hold's deadline.
/// </summary>
public readonly record struct PhysicalHoldDecision(
    PhysicalHoldDecisionKind Kind,
    long PressId)
{
    public bool StartsNewPress => Kind == PhysicalHoldDecisionKind.Fresh;

    public bool SuppressDuplicateStart => Kind == PhysicalHoldDecisionKind.HeldContinuation;

    public bool PreserveCurrentDeadline => Kind == PhysicalHoldDecisionKind.HeldContinuation;
}

public readonly record struct PhysicalHoldLatchSnapshot(
    PhysicalHoldLatchState State,
    long PressId,
    RawPhysicalChord Chord)
{
    public bool HasCertifiedHold => State == PhysicalHoldLatchState.Latched && PressId > 0;
}

/// <summary>
/// Dependency-free raw-input latch for one logical binding. A new press can be
/// certified only from a simultaneous logical and raw Pressed+Down observation
/// while idle. Once latched, logical gaps and typematic callbacks remain the
/// same press until raw key-up for the original physical key is observed.
/// </summary>
public sealed class PhysicalHoldLatch
{
    private readonly object gate = new();
    private PhysicalHoldLatchState state;
    private RawPhysicalChord chord;
    private long activePressId;
    private long lastPressId;

    public PhysicalHoldLatchSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                return new PhysicalHoldLatchSnapshot(state, activePressId, chord);
            }
        }
    }

    public PhysicalHoldDecision Observe(PhysicalHoldObservation observation)
    {
        lock (gate)
        {
            if (!observation.Chord.IsValid)
            {
                return Untrusted();
            }

            return state switch
            {
                PhysicalHoldLatchState.Idle => ObserveIdle(observation),
                PhysicalHoldLatchState.NeedsRelease => ObserveNeedsRelease(observation),
                PhysicalHoldLatchState.Latched => ObserveLatched(observation),
                _ => throw new InvalidOperationException($"Unknown physical-hold state: {state}."),
            };
        }
    }

    private PhysicalHoldDecision ObserveIdle(PhysicalHoldObservation observation)
    {
        if (!observation.RawDown)
        {
            return observation.RawPressed || observation.LogicalPressed || observation.LogicalDown
                ? Untrusted()
                : default;
        }

        chord = observation.Chord;
        if (!observation.RawPressed || !observation.LogicalPressed)
        {
            // Observation began after the key was already held, or the two input
            // sources disagreed. Require a real key-up before accepting it.
            state = PhysicalHoldLatchState.NeedsRelease;
            return Untrusted();
        }

        activePressId = NextPositive(ref lastPressId);
        state = PhysicalHoldLatchState.Latched;
        return new PhysicalHoldDecision(PhysicalHoldDecisionKind.Fresh, activePressId);
    }

    private PhysicalHoldDecision ObserveNeedsRelease(PhysicalHoldObservation observation)
    {
        if (observation.Chord.PhysicalKey != chord.PhysicalKey)
        {
            return Untrusted();
        }

        if (observation.RawDown)
        {
            return Untrusted();
        }

        ClearLatch();
        return new PhysicalHoldDecision(PhysicalHoldDecisionKind.Released, PressId: 0);
    }

    private PhysicalHoldDecision ObserveLatched(PhysicalHoldObservation observation)
    {
        if (observation.Chord.PhysicalKey != chord.PhysicalKey)
        {
            return Untrusted();
        }

        if (!observation.RawDown)
        {
            var releasedPressId = activePressId;
            ClearLatch();
            return new PhysicalHoldDecision(PhysicalHoldDecisionKind.Released, releasedPressId);
        }

        if (observation.Chord != chord)
        {
            // Modifier changes cannot forge a release or a second press while the
            // original physical key remains held.
            return Untrusted();
        }

        return new PhysicalHoldDecision(
            PhysicalHoldDecisionKind.HeldContinuation,
            activePressId);
    }

    private PhysicalHoldDecision Untrusted() =>
        new(PhysicalHoldDecisionKind.Untrusted, activePressId);

    private void ClearLatch()
    {
        state = PhysicalHoldLatchState.Idle;
        chord = default;
        activePressId = 0;
    }

    private static long NextPositive(ref long value)
    {
        if (value == long.MaxValue)
        {
            throw new InvalidOperationException("The physical-press sequence is exhausted.");
        }

        value++;
        return value;
    }
}
