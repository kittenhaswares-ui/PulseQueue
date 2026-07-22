using System.Collections.Concurrent;
using PulseQueue.Core;

internal static class LogicalHotbarRepeatTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("logical repeat requires startup release", StartupReleaseIsRequired);
        yield return ("logical repeat options normalize hard timing bounds", OptionsAreNormalized);
        yield return ("logical repeat injects exactly on cadence", InjectsExactlyOnCadence);
        yield return ("logical repeat zero initial delay still waits one interval", ZeroInitialDelayWaitsOneInterval);
        yield return ("logical repeat zero interval emits at most once per scan", ZeroIntervalEmitsAtMostOncePerScan);
        yield return ("logical repeat newest physical input becomes sole owner", NewestInputBecomesSoleOwner);
        yield return ("logical repeat suppresses preempted hold until its release", PreemptedHoldStaysSuppressedUntilRelease);
        yield return ("logical repeat old release preserves newest owner", OldReleasePreservesNewestOwner);
        yield return ("logical repeat delegates to an external repeat owner", ExternalOwnerDelegatesRepeats);
        yield return ("logical repeat disabled mode passes native input", DisabledRepeatPassesNativeInput);
        yield return ("logical repeat disabled newest input still preempts", DisabledNewestInputStillPreempts);
        yield return ("logical repeat release and repress creates a fresh hold", ReleaseAndRepressCreatesFreshHold);
        yield return ("logical repeat reset gates the current owner until release", ResetGatesCurrentOwnerUntilRelease);
        yield return ("logical repeat reset gates every held input independently", ResetGatesEveryHeldInputIndependently);
        yield return ("logical repeat reset preserves released input eligibility", ResetPreservesReleasedInputEligibility);
        yield return ("logical repeat reset resumes external delegation only after release", ResetResumesExternalDelegationAfterRelease);
        yield return ("logical repeat timing reset preserves held release gate", TimingResetPreservesHeldReleaseGate);
        yield return ("logical repeat skips catch-up bursts", NoCatchUpBurst);
        yield return ("logical repeat certifies a release-proven fast tap", NativeTapIsFreshPhysicalEdge);
        yield return ("logical repeat rejects invalid observations without mutation", InvalidObservationsDoNotMutate);
        yield return ("logical repeat racing due observations inject at most once", RacingDueObservationsInjectOnce);
        yield return ("logical repeat randomized trace preserves invariants", RandomizedTracePreservesInvariants);
    }

    private static void StartupReleaseIsRequired()
    {
        var engine = new LogicalHotbarRepeatEngine();

        var startupPress = engine.Observe(Observation(10, nativePressed: true, held: true, now: 0));
        Decision(
            startupPress,
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 0,
            freshPhysicalEdge: false);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(10, false, true, 1_000)).Kind);
        False(engine.Snapshot.HasOwner);

        Decision(
            engine.Observe(Observation(10, nativePressed: false, held: false, now: 1_001)),
            LogicalHotbarRepeatDecisionKind.Released,
            reportPressed: false,
            owner: 0);
        Decision(
            engine.Observe(Observation(10, nativePressed: true, held: true, now: 1_002)),
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 10,
            freshPhysicalEdge: true);
        Equal(1L, engine.Snapshot.Counters.HoldsClaimed);
    }

    private static void OptionsAreNormalized()
    {
        var low = new LogicalHotbarRepeatEngine(new LogicalHotbarRepeatOptions(-50, -1));
        Equal(0, low.Options.InitialDelayMilliseconds);
        Equal(0, low.Options.RepeatIntervalMilliseconds);

        var high = new LogicalHotbarRepeatEngine(new LogicalHotbarRepeatOptions(10_000, 75));
        Equal(1_000, high.Options.InitialDelayMilliseconds);
        Equal(75, high.Options.RepeatIntervalMilliseconds);

        var largeInterval = new LogicalHotbarRepeatEngine(
            new LogicalHotbarRepeatOptions(50, int.MaxValue));
        Equal(1_000, largeInterval.Options.RepeatIntervalMilliseconds);
    }

    private static void InjectsExactlyOnCadence()
    {
        var engine = ReadyEngine(initialDelay: 100, interval: 60);
        Press(engine, id: 1, now: 1_000);

        Decision(engine.Observe(Observation(1, false, true, 1_099)), LogicalHotbarRepeatDecisionKind.None, false, 1);
        Decision(engine.Observe(Observation(1, false, true, 1_100)), LogicalHotbarRepeatDecisionKind.InjectedRepeat, true, 1);
        Decision(engine.Observe(Observation(1, false, true, 1_100)), LogicalHotbarRepeatDecisionKind.None, false, 1);
        Decision(engine.Observe(Observation(1, false, true, 1_159)), LogicalHotbarRepeatDecisionKind.None, false, 1);
        Decision(engine.Observe(Observation(1, false, true, 1_160)), LogicalHotbarRepeatDecisionKind.InjectedRepeat, true, 1);
        Equal(2L, engine.Snapshot.Counters.InjectedRepeats);
    }

    private static void ZeroInitialDelayWaitsOneInterval()
    {
        var engine = ReadyEngine(initialDelay: 0, interval: 80);
        Press(engine, id: 1, now: 500);

        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 500)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 579)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 580)).Kind);
    }

    private static void ZeroIntervalEmitsAtMostOncePerScan()
    {
        var engine = ReadyEngine(initialDelay: 0, interval: 0);
        Press(engine, id: 1, now: 500);

        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 500)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 501)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 501)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 502)).Kind);

        var racing = new ConcurrentBag<LogicalHotbarRepeatDecision>();
        Parallel.For(0, 128, _ =>
            racing.Add(engine.Observe(Observation(1, false, true, 503))));
        Equal(1, racing.Count(decision => decision.Kind == LogicalHotbarRepeatDecisionKind.InjectedRepeat));
        Equal(3L, engine.Snapshot.Counters.InjectedRepeats);
    }

    private static void NewestInputBecomesSoleOwner()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);

        var newest = engine.Observe(Observation(2, nativePressed: true, held: true, now: 102));
        Decision(
            newest,
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 2,
            freshPhysicalEdge: true);
        Equal(2L, engine.Snapshot.Counters.HoldsClaimed);
        Equal(1L, engine.Snapshot.Counters.HoldsPreempted);

        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(2, false, true, 162)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.SuppressedOlderHold, engine.Observe(Observation(1, false, true, 10_000)).Kind);
        Equal(1L, engine.Snapshot.Counters.InjectedRepeats);
    }

    private static void PreemptedHoldStaysSuppressedUntilRelease()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);
        Press(engine, id: 2, now: 102);

        var nativeOldRepeat = engine.Observe(Observation(1, nativePressed: true, held: true, now: 200));
        Decision(nativeOldRepeat, LogicalHotbarRepeatDecisionKind.SuppressedOlderHold, reportPressed: false, owner: 2);

        Release(engine, id: 2, now: 201);
        var stillOld = engine.Observe(Observation(1, nativePressed: false, held: true, now: 1_000));
        Decision(stillOld, LogicalHotbarRepeatDecisionKind.SuppressedOlderHold, reportPressed: false, owner: 0);

        Release(engine, id: 1, now: 1_001);
        Press(engine, id: 1, now: 1_002);
        Equal(1L, engine.Snapshot.OwnerLogicalInputId);
        Equal(3L, engine.Snapshot.Counters.HoldsClaimed);
    }

    private static void ExternalOwnerDelegatesRepeats()
    {
        var engine = ReadyEngine();
        var initial = engine.Observe(Observation(1, true, true, 100, externalOwner: true));
        Decision(initial, LogicalHotbarRepeatDecisionKind.PhysicalPress, true, 1, freshPhysicalEdge: true);

        Equal(
            LogicalHotbarRepeatDecisionKind.None,
            engine.Observe(Observation(1, false, true, 1_000, externalOwner: true)).Kind);
        var delegated = engine.Observe(Observation(1, true, true, 1_001, externalOwner: true));
        Decision(delegated, LogicalHotbarRepeatDecisionKind.DelegatedRepeat, true, 1);
        Equal(0L, delegated.Counters.InjectedRepeats);
        Equal(1L, delegated.Counters.DelegatedRepeats);

        Equal(
            LogicalHotbarRepeatDecisionKind.None,
            engine.Observe(Observation(1, false, true, 1_060, externalOwner: false)).Kind);
        Equal(
            LogicalHotbarRepeatDecisionKind.InjectedRepeat,
            engine.Observe(Observation(1, false, true, 1_061, externalOwner: false)).Kind);
    }

    private static void OldReleasePreservesNewestOwner()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);
        Press(engine, id: 2, now: 102);

        Decision(
            engine.Observe(Observation(1, nativePressed: false, held: false, now: 103)),
            LogicalHotbarRepeatDecisionKind.Released,
            reportPressed: false,
            owner: 2);
        Equal(
            LogicalHotbarRepeatDecisionKind.InjectedRepeat,
            engine.Observe(Observation(2, nativePressed: false, held: true, now: 162)).Kind);
    }

    private static void DisabledRepeatPassesNativeInput()
    {
        var engine = ReadyEngine();
        var initial = engine.Observe(Observation(1, true, true, 100, repeatEnabled: false));
        Decision(initial, LogicalHotbarRepeatDecisionKind.PhysicalPress, true, 1, freshPhysicalEdge: true);

        Equal(
            LogicalHotbarRepeatDecisionKind.None,
            engine.Observe(Observation(1, false, true, 10_000, repeatEnabled: false)).Kind);
        var laterNative = engine.Observe(Observation(1, true, true, 10_001, repeatEnabled: false));
        Decision(laterNative, LogicalHotbarRepeatDecisionKind.PhysicalPress, true, 1, freshPhysicalEdge: false);
        Equal(2L, laterNative.Counters.PhysicalPresses);
        Equal(0L, laterNative.Counters.InjectedRepeats);
    }

    private static void DisabledNewestInputStillPreempts()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);

        Decision(
            engine.Observe(Observation(2, true, true, 102, repeatEnabled: false)),
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 2,
            freshPhysicalEdge: true);
        Decision(
            engine.Observe(Observation(1, false, true, 10_000)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 2);
        Equal(
            LogicalHotbarRepeatDecisionKind.None,
            engine.Observe(Observation(2, false, true, 10_001, repeatEnabled: false)).Kind);
    }

    private static void ReleaseAndRepressCreatesFreshHold()
    {
        var engine = ReadyEngine();
        Release(engine, id: 7, now: 99);
        Press(engine, id: 7, now: 100);
        Decision(engine.Observe(Observation(7, false, false, 101)), LogicalHotbarRepeatDecisionKind.Released, false, 0);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(7, false, false, 102)).Kind);

        Press(engine, id: 7, now: 103);
        Equal(2L, engine.Snapshot.Counters.HoldsClaimed);
        Equal(7L, engine.Snapshot.OwnerLogicalInputId);
    }

    private static void ResetGatesCurrentOwnerUntilRelease()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);

        Equal(1, engine.CancelAndRequireRelease());
        False(engine.Snapshot.HasOwner);

        Decision(
            engine.Observe(Observation(1, nativePressed: false, held: true, now: 10_000)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 0);
        Decision(
            engine.Observe(Observation(1, nativePressed: true, held: true, now: 10_001)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 0);
        Equal(1L, engine.Snapshot.Counters.HoldsClaimed);
        Equal(0L, engine.Snapshot.Counters.InjectedRepeats);

        Release(engine, id: 1, now: 10_002);
        Press(engine, id: 1, now: 10_003);
        Equal(2L, engine.Snapshot.Counters.HoldsClaimed);
    }

    private static void ResetGatesEveryHeldInputIndependently()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);
        Press(engine, id: 2, now: 102);

        Equal(2, engine.CancelAndRequireRelease());
        False(engine.Snapshot.HasOwner);

        Release(engine, id: 2, now: 103);
        Press(engine, id: 2, now: 104);
        Equal(2L, engine.Snapshot.OwnerLogicalInputId);

        Decision(
            engine.Observe(Observation(1, nativePressed: true, held: true, now: 10_000)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 2);

        Release(engine, id: 1, now: 10_001);
        Press(engine, id: 1, now: 10_002);
        Equal(1L, engine.Snapshot.OwnerLogicalInputId);
    }

    private static void ResetPreservesReleasedInputEligibility()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Release(engine, id: 2, now: 101);

        Equal(1, engine.CancelAndRequireRelease());

        // Input 2 was already release-proven at reset time, so its next native
        // edge is genuine and may immediately become the new owner.
        Press(engine, id: 2, now: 102);
        Equal(2L, engine.Snapshot.OwnerLogicalInputId);
        Equal(2L, engine.Snapshot.Counters.HoldsClaimed);
    }

    private static void ResetResumesExternalDelegationAfterRelease()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 100);
        Equal(1, engine.CancelAndRequireRelease());

        Decision(
            engine.Observe(Observation(1, true, true, 200, externalOwner: true)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 0);
        Equal(0L, engine.Snapshot.Counters.DelegatedRepeats);

        Release(engine, id: 1, now: 201);
        Decision(
            engine.Observe(Observation(1, true, true, 202, externalOwner: true)),
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 1,
            freshPhysicalEdge: true);
        Decision(
            engine.Observe(Observation(1, true, true, 203, externalOwner: true)),
            LogicalHotbarRepeatDecisionKind.DelegatedRepeat,
            reportPressed: true,
            owner: 1);
        Equal(1L, engine.Snapshot.Counters.DelegatedRepeats);
    }

    private static void TimingResetPreservesHeldReleaseGate()
    {
        var engine = ReadyEngine(initialDelay: 100, interval: 60);
        Press(engine, id: 1, now: 100);

        Equal(
            1,
            engine.ReconfigureAndRequireRelease(
                new LogicalHotbarRepeatOptions(InitialDelayMilliseconds: 250, RepeatIntervalMilliseconds: 90)));
        Equal(250, engine.Options.InitialDelayMilliseconds);
        Equal(90, engine.Options.RepeatIntervalMilliseconds);
        False(engine.Snapshot.HasOwner);

        Decision(
            engine.Observe(Observation(1, true, true, 500, externalOwner: true)),
            LogicalHotbarRepeatDecisionKind.SuppressedOlderHold,
            reportPressed: false,
            owner: 0);
        Release(engine, id: 1, now: 501);
        Press(engine, id: 1, now: 502);
        Equal(752L, engine.Snapshot.NextRepeatAtMilliseconds);
    }

    private static void NoCatchUpBurst()
    {
        var engine = ReadyEngine(initialDelay: 60, interval: 60);
        Press(engine, id: 1, now: 0);

        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 60)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 60_000)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 60_000)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.None, engine.Observe(Observation(1, false, true, 60_059)).Kind);
        Equal(LogicalHotbarRepeatDecisionKind.InjectedRepeat, engine.Observe(Observation(1, false, true, 60_060)).Kind);
        Equal(3L, engine.Snapshot.Counters.InjectedRepeats);
    }

    private static void NativeTapIsFreshPhysicalEdge()
    {
        var engine = new LogicalHotbarRepeatEngine();
        var tap = engine.Observe(Observation(99, nativePressed: true, held: false, now: 0));
        Decision(
            tap,
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: 0,
            freshPhysicalEdge: true);
        Equal(1L, tap.Counters.PhysicalPresses);
        Equal(1L, tap.Counters.Releases);
    }

    private static void InvalidObservationsDoNotMutate()
    {
        var engine = new LogicalHotbarRepeatEngine();
        Throws<ArgumentOutOfRangeException>(() => engine.Observe(Observation(0, true, true, 0)));
        Throws<ArgumentOutOfRangeException>(() => engine.Observe(Observation(1, true, true, -1)));
        Equal(default(LogicalHotbarRepeatCounters), engine.Snapshot.Counters);
    }

    private static void RacingDueObservationsInjectOnce()
    {
        var engine = ReadyEngine();
        Press(engine, id: 1, now: 0);
        var decisions = new ConcurrentBag<LogicalHotbarRepeatDecision>();

        Parallel.For(0, 128, _ =>
            decisions.Add(engine.Observe(Observation(1, false, true, 60))));

        Equal(1, decisions.Count(decision => decision.Kind == LogicalHotbarRepeatDecisionKind.InjectedRepeat));
        Equal(1, decisions.Count(decision => decision.ShouldReportPressed));
        Equal(1L, engine.Snapshot.Counters.InjectedRepeats);
        Equal(130L, engine.Snapshot.Counters.Observations);
    }

    private static void RandomizedTracePreservesInvariants()
    {
        var random = new Random(0x51_2026);
        var engine = new LogicalHotbarRepeatEngine(
            new LogicalHotbarRepeatOptions(35, 67));
        var previousCounters = default(LogicalHotbarRepeatCounters);
        long now = 0;

        for (var step = 0; step < 50_000; step++)
        {
            now += random.Next(0, 250);
            var observation = Observation(
                id: random.Next(1, 9),
                nativePressed: random.Next(5) == 0,
                held: random.Next(4) != 0,
                now,
                repeatEnabled: random.Next(6) != 0,
                externalOwner: random.Next(7) == 0);
            var decision = engine.Observe(observation);

            Equal(step + 1L, decision.Counters.Observations);
            Monotonic(previousCounters, decision.Counters);
            Equal(decision.OwnerLogicalInputId, engine.Snapshot.OwnerLogicalInputId);
            True(decision.OwnerLogicalInputId >= 0);

            if (observation.NativePressed
                && decision.Kind != LogicalHotbarRepeatDecisionKind.SuppressedOlderHold)
            {
                True(decision.ShouldReportPressed, "Every genuine native press must pass through.");
            }

            if (decision.Kind == LogicalHotbarRepeatDecisionKind.SuppressedOlderHold)
            {
                False(decision.ShouldReportPressed);
            }

            if (decision.Kind == LogicalHotbarRepeatDecisionKind.InjectedRepeat)
            {
                False(observation.NativePressed);
                True(observation.Held);
                True(observation.RepeatEnabled);
                False(observation.ExternalRepeatOwnerActive);
                True(decision.OwnerLogicalInputId == observation.LogicalInputId);
                True(decision.ShouldReportPressed);
            }

            if (decision.Kind == LogicalHotbarRepeatDecisionKind.DelegatedRepeat)
            {
                True(observation.NativePressed);
                True(observation.ExternalRepeatOwnerActive);
                True(decision.OwnerLogicalInputId == observation.LogicalInputId);
            }

            if (decision.Kind == LogicalHotbarRepeatDecisionKind.Released)
            {
                False(observation.Held);
                False(observation.NativePressed);
                False(decision.ShouldReportPressed);
            }

            if (decision.IsFreshPhysicalEdge)
            {
                Equal(LogicalHotbarRepeatDecisionKind.PhysicalPress, decision.Kind);
                True(observation.NativePressed);
                True(
                    !observation.Held
                    || decision.Counters.HoldsClaimed > previousCounters.HoldsClaimed,
                    "A held fresh edge must claim ownership after a proven release.");
            }

            previousCounters = decision.Counters;
        }

        var counters = engine.Snapshot.Counters;
        Equal(50_000L, counters.Observations);
        True(counters.InjectedRepeats <= counters.Observations);
        True(counters.DelegatedRepeats <= counters.PhysicalPresses);
        True(counters.HoldsPreempted <= counters.HoldsClaimed);
        True(counters.SuppressedOlderHolds <= counters.Observations);
    }

    private static void Monotonic(
        LogicalHotbarRepeatCounters before,
        LogicalHotbarRepeatCounters after)
    {
        True(after.Observations >= before.Observations);
        True(after.PhysicalPresses >= before.PhysicalPresses);
        True(after.HoldsClaimed >= before.HoldsClaimed);
        True(after.HoldsPreempted >= before.HoldsPreempted);
        True(after.InjectedRepeats >= before.InjectedRepeats);
        True(after.DelegatedRepeats >= before.DelegatedRepeats);
        True(after.SuppressedOlderHolds >= before.SuppressedOlderHolds);
        True(after.Releases >= before.Releases);
    }

    private static LogicalHotbarRepeatEngine ReadyEngine(int initialDelay = 0, int interval = 60)
    {
        var engine = new LogicalHotbarRepeatEngine(
            new LogicalHotbarRepeatOptions(initialDelay, interval));
        Release(engine, id: 1, now: 0);
        return engine;
    }

    private static void Press(LogicalHotbarRepeatEngine engine, long id, long now)
    {
        var decision = engine.Observe(Observation(id, nativePressed: true, held: true, now));
        Decision(
            decision,
            LogicalHotbarRepeatDecisionKind.PhysicalPress,
            reportPressed: true,
            owner: id,
            freshPhysicalEdge: true);
    }

    private static void Release(LogicalHotbarRepeatEngine engine, long id, long now)
    {
        var decision = engine.Observe(Observation(id, nativePressed: false, held: false, now));
        Equal(LogicalHotbarRepeatDecisionKind.Released, decision.Kind);
        False(decision.ShouldReportPressed);
    }

    private static LogicalHotbarRepeatObservation Observation(
        long id,
        bool nativePressed,
        bool held,
        long now,
        bool repeatEnabled = true,
        bool externalOwner = false) =>
        new(id, nativePressed, held, now, repeatEnabled, externalOwner);

    private static void Decision(
        LogicalHotbarRepeatDecision decision,
        LogicalHotbarRepeatDecisionKind expectedKind,
        bool reportPressed,
        long owner,
        bool freshPhysicalEdge = false)
    {
        Equal(expectedKind, decision.Kind);
        Equal(reportPressed, decision.ShouldReportPressed);
        Equal(freshPhysicalEdge, decision.IsFreshPhysicalEdge);
        Equal(owner, decision.OwnerLogicalInputId);
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true, got false.");
        }
    }

    private static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, got true.");

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
