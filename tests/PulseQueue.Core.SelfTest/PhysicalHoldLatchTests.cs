using PulseQueue.Core;

internal static class PhysicalHoldLatchTests
{
    private static readonly RawPhysicalChord PlainOne = new(
        PhysicalKey: 0x31,
        ChordFingerprint: 0x31);

    private static readonly RawPhysicalChord ShiftOne = new(
        PhysicalKey: 0x31,
        ChordFingerprint: 0x0001_0031);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("physical latch certifies one fresh raw press", FreshRawPressCertifiesOnce);
        yield return ("logical gaps and typematic keep one press identity", LogicalGapsAndTypematicKeepIdentity);
        yield return ("held continuation preserves the repeat deadline", HeldContinuationPreservesDeadline);
        yield return ("raw key-up releases and next down gets a new identity", RawKeyUpAllowsNextIdentity);
        yield return ("modifier changes cannot forge a release or fresh press", ModifierChangesFailClosed);
        yield return ("a different physical key cannot replace a latched hold", DifferentPhysicalKeyFailsClosed);
        yield return ("an already-held key needs release before certification", AlreadyHeldKeyNeedsRelease);
    }

    private static void FreshRawPressCertifiesOnce()
    {
        var latch = new PhysicalHoldLatch();

        var fresh = latch.Observe(Fresh(PlainOne));

        Equal(PhysicalHoldDecisionKind.Fresh, fresh.Kind);
        Equal(1L, fresh.PressId);
        True(fresh.StartsNewPress);
        False(fresh.SuppressDuplicateStart);
        False(fresh.PreserveCurrentDeadline);
        Equal(PhysicalHoldLatchState.Latched, latch.Snapshot.State);
        Equal(PlainOne, latch.Snapshot.Chord);
        True(latch.Snapshot.HasCertifiedHold);
    }

    private static void LogicalGapsAndTypematicKeepIdentity()
    {
        var latch = new PhysicalHoldLatch();
        Equal(1L, latch.Observe(Fresh(PlainOne)).PressId);

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var logicalGap = latch.Observe(new PhysicalHoldObservation(
                PlainOne,
                LogicalPressed: false,
                LogicalDown: false,
                RawPressed: false,
                RawDown: true));
            Equal(PhysicalHoldDecisionKind.HeldContinuation, logicalGap.Kind);
            Equal(1L, logicalGap.PressId);
            True(logicalGap.SuppressDuplicateStart);
            True(logicalGap.PreserveCurrentDeadline);
            False(logicalGap.StartsNewPress);

            var typematic = latch.Observe(new PhysicalHoldObservation(
                PlainOne,
                LogicalPressed: true,
                LogicalDown: true,
                RawPressed: true,
                RawDown: true));
            Equal(PhysicalHoldDecisionKind.HeldContinuation, typematic.Kind);
            Equal(1L, typematic.PressId);
            True(typematic.SuppressDuplicateStart);
        }

        Equal(1L, latch.Snapshot.PressId);
    }

    private static void HeldContinuationPreservesDeadline()
    {
        var latch = new PhysicalHoldLatch();
        var repeat = new HoldRepeatEngine(new HoldRepeatOptions(
            InitialDelayMilliseconds: 180,
            IntervalMilliseconds: 80,
            MaximumHoldMilliseconds: 30_000));
        repeat.ObserveRelease();

        Apply(latch.Observe(Fresh(PlainOne)), nowMilliseconds: 1_000);
        var initial = repeat.Snapshot;
        Equal(1_180L, initial.NextPulseAtMilliseconds);

        Apply(latch.Observe(new PhysicalHoldObservation(
            PlainOne,
            LogicalPressed: false,
            LogicalDown: false,
            RawPressed: false,
            RawDown: true)), nowMilliseconds: 1_060);
        Apply(latch.Observe(new PhysicalHoldObservation(
            PlainOne,
            LogicalPressed: true,
            LogicalDown: true,
            RawPressed: true,
            RawDown: true)), nowMilliseconds: 1_120);

        var afterRepeats = repeat.Snapshot;
        Equal(initial.HoldId, afterRepeats.HoldId);
        Equal(initial.PressId, afterRepeats.PressId);
        Equal(initial.StartedAtMilliseconds, afterRepeats.StartedAtMilliseconds);
        Equal(initial.NextPulseAtMilliseconds, afterRepeats.NextPulseAtMilliseconds);
        Equal(1L, afterRepeats.Counters.StartAttempts);
        Equal(HoldRepeatDecisionKind.Pulse, repeat.Tick(
            1_180,
            HoldRepeatSafetyState.SafeHeld,
            actionReady: true).Kind);

        void Apply(PhysicalHoldDecision decision, long nowMilliseconds)
        {
            if (decision.SuppressDuplicateStart)
            {
                return;
            }

            if (!decision.StartsNewPress)
            {
                throw new InvalidOperationException($"Unexpected decision: {decision.Kind}.");
            }

            Equal(
                HoldRepeatStartResult.Started,
                repeat.TryStart(new HoldRepeatStartRequest(
                    PressId: decision.PressId,
                    InputGeneration: decision.PressId,
                    ControlFingerprint: PlainOne.ChordFingerprint,
                    IntentFingerprint: 0xCAFE,
                    IsCertifiedFreshPress: true),
                    nowMilliseconds));
        }
    }

    private static void RawKeyUpAllowsNextIdentity()
    {
        var latch = new PhysicalHoldLatch();
        Equal(1L, latch.Observe(Fresh(PlainOne)).PressId);

        var released = latch.Observe(new PhysicalHoldObservation(
            PlainOne,
            LogicalPressed: false,
            LogicalDown: true,
            RawPressed: false,
            RawDown: false));
        Equal(PhysicalHoldDecisionKind.Released, released.Kind);
        Equal(1L, released.PressId);
        Equal(PhysicalHoldLatchState.Idle, latch.Snapshot.State);
        False(latch.Snapshot.HasCertifiedHold);

        var second = latch.Observe(Fresh(PlainOne));
        Equal(PhysicalHoldDecisionKind.Fresh, second.Kind);
        Equal(2L, second.PressId);
    }

    private static void ModifierChangesFailClosed()
    {
        var latch = new PhysicalHoldLatch();
        Equal(1L, latch.Observe(Fresh(PlainOne)).PressId);

        var changed = latch.Observe(new PhysicalHoldObservation(
            ShiftOne,
            LogicalPressed: true,
            LogicalDown: true,
            RawPressed: true,
            RawDown: true));
        Equal(PhysicalHoldDecisionKind.Untrusted, changed.Kind);
        Equal(1L, changed.PressId);
        Equal(PhysicalHoldLatchState.Latched, latch.Snapshot.State);
        Equal(PlainOne, latch.Snapshot.Chord);

        var releaseWithChangedModifiers = latch.Observe(new PhysicalHoldObservation(
            ShiftOne,
            LogicalPressed: false,
            LogicalDown: false,
            RawPressed: false,
            RawDown: false));
        Equal(PhysicalHoldDecisionKind.Released, releaseWithChangedModifiers.Kind);
        Equal(1L, releaseWithChangedModifiers.PressId);

        var next = latch.Observe(Fresh(ShiftOne));
        Equal(PhysicalHoldDecisionKind.Fresh, next.Kind);
        Equal(2L, next.PressId);
    }

    private static void DifferentPhysicalKeyFailsClosed()
    {
        var latch = new PhysicalHoldLatch();
        Equal(1L, latch.Observe(Fresh(PlainOne)).PressId);
        var other = new RawPhysicalChord(PhysicalKey: 0x32, ChordFingerprint: 0x32);

        var attemptedReplacement = latch.Observe(Fresh(other));

        Equal(PhysicalHoldDecisionKind.Untrusted, attemptedReplacement.Kind);
        Equal(1L, attemptedReplacement.PressId);
        Equal(PlainOne, latch.Snapshot.Chord);
        Equal(1L, latch.Snapshot.PressId);
    }

    private static void AlreadyHeldKeyNeedsRelease()
    {
        var latch = new PhysicalHoldLatch();

        var firstSeenHeld = latch.Observe(new PhysicalHoldObservation(
            PlainOne,
            LogicalPressed: false,
            LogicalDown: true,
            RawPressed: false,
            RawDown: true));
        Equal(PhysicalHoldDecisionKind.Untrusted, firstSeenHeld.Kind);
        Equal(PhysicalHoldLatchState.NeedsRelease, latch.Snapshot.State);

        var laterTypematic = latch.Observe(Fresh(PlainOne));
        Equal(PhysicalHoldDecisionKind.Untrusted, laterTypematic.Kind);
        Equal(0L, laterTypematic.PressId);

        var release = latch.Observe(new PhysicalHoldObservation(
            PlainOne,
            LogicalPressed: false,
            LogicalDown: false,
            RawPressed: false,
            RawDown: false));
        Equal(PhysicalHoldDecisionKind.Released, release.Kind);
        Equal(PhysicalHoldLatchState.Idle, latch.Snapshot.State);

        var certified = latch.Observe(Fresh(PlainOne));
        Equal(PhysicalHoldDecisionKind.Fresh, certified.Kind);
        Equal(1L, certified.PressId);
    }

    private static PhysicalHoldObservation Fresh(RawPhysicalChord chord) => new(
        chord,
        LogicalPressed: true,
        LogicalDown: true,
        RawPressed: true,
        RawDown: true);

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true, got false.");
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }
}
