using PulseQueue.Core;

internal static class HoldRepeatTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("hold repeat requires release and certified fresh press", RequiresReleaseAndCertifiedFreshPress);
        yield return ("hold repeat keeps exactly one newest active hold", NewestFreshPressReplacesActiveHold);
        yield return ("hold repeat respects delay interval and no catch-up", DelayIntervalAndNoCatchUp);
        yield return ("hold repeat options enforce hard timing bounds", OptionsEnforceHardBounds);
        yield return ("hold repeat defaults use immediate bounded cadence", DefaultsUseImmediateBoundedCadence);
        yield return ("hold repeat permits zero initial delay without bypassing interval", ZeroInitialDelayStillHonorsInterval);
        yield return ("racing hold ticks issue exactly one pulse token", RacingTicksIssueExactlyOnePulse);
        yield return ("release prevents every later pulse", ReleasePreventsLaterPulse);
        yield return ("cancel and replacement invalidate stale tokens", CancellationAndReplacementInvalidateTokens);
        yield return ("rejected pulse ends hold without retry", RejectedPulseEndsHoldWithoutRetry);
        yield return ("hold repeat cancels for every safety boundary", EverySafetyBoundaryCancels);
        yield return ("hold repeat maximum duration is terminal", MaximumDurationIsTerminal);
        yield return ("hold repeat randomized trace preserves invariants", RandomizedTracePreservesInvariants);
    }

    private static void RequiresReleaseAndCertifiedFreshPress()
    {
        var engine = new HoldRepeatEngine();
        Equal(HoldRepeatState.NeedsRelease, engine.Snapshot.State);
        Equal(
            HoldRepeatStartResult.RejectedNeedsRelease,
            engine.TryStart(Request(1), nowMilliseconds: 100));

        False(engine.ObserveRelease());
        Equal(HoldRepeatState.Idle, engine.Snapshot.State);
        Equal(
            HoldRepeatStartResult.RejectedUncertified,
            engine.TryStart(Request(2) with { IsCertifiedFreshPress = false }, nowMilliseconds: 101));
        Equal(
            HoldRepeatStartResult.Started,
            engine.TryStart(Request(2), nowMilliseconds: 101));
        True(engine.Snapshot.HasActiveHold);

        var counters = engine.Snapshot.Counters;
        Equal(3L, counters.StartAttempts);
        Equal(1L, counters.HoldsStarted);
        Equal(2L, counters.RejectedStarts);
        Equal(1L, counters.ReleaseObservations);
    }

    private static void NewestFreshPressReplacesActiveHold()
    {
        var engine = ReadyEngine();
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 1_000));
        var olderHoldId = engine.Snapshot.HoldId;
        var olderPulse = PulseAt(engine, 1_180);

        Equal(HoldRepeatStartResult.Replaced, engine.TryStart(Request(2), 1_181));
        var replacement = engine.Snapshot;
        True(replacement.HasActiveHold);
        True(replacement.HoldId > olderHoldId);
        Equal(2L, replacement.PressId);
        Equal(2L, replacement.InputGeneration);
        False(engine.IsTokenCurrent(olderPulse));

        Equal(HoldRepeatStartResult.RejectedStale, engine.TryStart(Request(2), 1_182));
        Equal(2L, engine.Snapshot.PressId);
        Equal(2L, engine.Snapshot.Counters.HoldsStarted);
        Equal(1L, engine.Snapshot.Counters.HoldsReplaced);
    }

    private static void DelayIntervalAndNoCatchUp()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(
            InitialDelayMilliseconds: 100,
            IntervalMilliseconds: 60,
            MaximumHoldMilliseconds: 30_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 1_000));

        Equal(HoldRepeatDecisionKind.None, engine.Tick(1_099, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        var first = PulseAt(engine, 1_100);
        Equal(1L, first.Ordinal);
        Equal(HoldRepeatDecisionKind.None, engine.Tick(1_100, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        Equal(HoldRepeatDecisionKind.None, engine.Tick(1_159, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        var second = PulseAt(engine, 1_160);
        Equal(2L, second.Ordinal);

        // A large missed interval produces one pulse now and schedules from now.
        var afterGap = PulseAt(engine, 5_000);
        Equal(3L, afterGap.Ordinal);
        Equal(HoldRepeatDecisionKind.None, engine.Tick(5_000, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        Equal(HoldRepeatDecisionKind.None, engine.Tick(5_059, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        var afterNewInterval = PulseAt(engine, 5_060);
        Equal(4L, afterNewInterval.Ordinal);

        Equal(4L, engine.Snapshot.Counters.PulsesIssued);
        Equal(4L, engine.Snapshot.LastIssuedOrdinal);
    }

    private static void OptionsEnforceHardBounds()
    {
        var low = new HoldRepeatEngine(new HoldRepeatOptions(
            InitialDelayMilliseconds: int.MinValue,
            IntervalMilliseconds: -1,
            MaximumHoldMilliseconds: int.MaxValue));
        Equal(0, low.Options.InitialDelayMilliseconds);
        Equal(HoldRepeatOptions.MinimumTimingMilliseconds, low.Options.IntervalMilliseconds);
        Equal(HoldRepeatOptions.AbsoluteMaximumHoldMilliseconds, low.Options.MaximumHoldMilliseconds);

        var shortHold = new HoldRepeatEngine(new HoldRepeatOptions(
            InitialDelayMilliseconds: 5_000,
            IntervalMilliseconds: int.MaxValue,
            MaximumHoldMilliseconds: 100));
        Equal(HoldRepeatOptions.MaximumInitialDelayMilliseconds, shortHold.Options.InitialDelayMilliseconds);
        Equal(HoldRepeatOptions.MaximumIntervalMilliseconds, shortHold.Options.IntervalMilliseconds);
        Equal(HoldRepeatOptions.MinimumHoldDurationMilliseconds, shortHold.Options.MaximumHoldMilliseconds);
    }

    private static void DefaultsUseImmediateBoundedCadence()
    {
        var defaults = HoldRepeatOptions.Default;
        Equal(0, defaults.InitialDelayMilliseconds);
        Equal(HoldRepeatOptions.MinimumTimingMilliseconds, defaults.IntervalMilliseconds);
        Equal(HoldRepeatOptions.AbsoluteMaximumHoldMilliseconds, defaults.MaximumHoldMilliseconds);
    }

    private static void ZeroInitialDelayStillHonorsInterval()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(
            InitialDelayMilliseconds: 0,
            IntervalMilliseconds: 60,
            MaximumHoldMilliseconds: 1_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 500));
        var immediate = PulseAt(engine, 500);
        Equal(1L, immediate.Ordinal);
        Equal(HoldRepeatDecisionKind.None, engine.Tick(559, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        Equal(2L, PulseAt(engine, 560).Ordinal);
    }

    private static void RacingTicksIssueExactlyOnePulse()
    {
        const int contenderCount = 32;
        var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 30_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 2_000));

        using var start = new ManualResetEventSlim(initialState: false);
        var contenders = Enumerable.Range(0, contenderCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return engine.Tick(2_060, HoldRepeatSafetyState.SafeHeld, actionReady: true);
            }))
            .ToArray();

        start.Set();
        Task.WaitAll(contenders);

        var decisions = contenders.Select(task => task.Result).ToArray();
        Equal(1, decisions.Count(decision => decision.Kind == HoldRepeatDecisionKind.Pulse));
        Equal(contenderCount - 1, decisions.Count(decision => decision.Kind == HoldRepeatDecisionKind.None));
        var token = decisions.Single(decision => decision.Kind == HoldRepeatDecisionKind.Pulse).Pulse;
        Equal(1L, token.Ordinal);
        True(engine.IsTokenCurrent(token));
        Equal(1L, engine.Snapshot.Counters.PulsesIssued);
    }

    private static void ReleasePreventsLaterPulse()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 30_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 0));
        var token = PulseAt(engine, 60);
        True(engine.ObserveRelease());

        Equal(HoldRepeatState.Idle, engine.Snapshot.State);
        False(engine.Snapshot.HasActiveHold);
        False(engine.IsTokenCurrent(token));
        Equal(HoldRepeatCancelReason.Released, engine.Snapshot.LastCancelReason);
        for (var now = 61; now <= 1_000; now += 47)
        {
            Equal(HoldRepeatDecisionKind.None, engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        }

        Equal(1L, engine.Snapshot.Counters.PulsesIssued);
    }

    private static void CancellationAndReplacementInvalidateTokens()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 30_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 100));
        var first = PulseAt(engine, 160);
        True(engine.Cancel(HoldRepeatCancelReason.Fault));
        False(engine.IsTokenCurrent(first));
        Equal(HoldRepeatState.NeedsRelease, engine.Snapshot.State);
        Equal(HoldRepeatStartResult.RejectedNeedsRelease, engine.TryStart(Request(2), 161));

        engine.ObserveRelease();
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(3), 200));
        var second = PulseAt(engine, 260);
        Equal(HoldRepeatStartResult.Replaced, engine.TryStart(Request(4), 261));
        False(engine.IsTokenCurrent(second));
        Equal(4L, engine.Snapshot.InputGeneration);
    }

    private static void RejectedPulseEndsHoldWithoutRetry()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 30_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 100));
        var rejected = PulseAt(engine, 160);

        True(engine.Cancel(HoldRepeatCancelReason.PulseRejected));
        False(engine.IsTokenCurrent(rejected));
        False(engine.Snapshot.HasActiveHold);
        Equal(HoldRepeatState.NeedsRelease, engine.Snapshot.State);
        Equal(HoldRepeatCancelReason.PulseRejected, engine.Snapshot.LastCancelReason);
        for (var now = 161; now <= 1_000; now += 53)
        {
            Equal(HoldRepeatDecisionKind.None, engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
        }

        Equal(HoldRepeatStartResult.RejectedNeedsRelease, engine.TryStart(Request(2), 1_001));
        False(engine.ObserveRelease());
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(3), 1_002));
    }

    private static void EverySafetyBoundaryCancels()
    {
        var cases = new (HoldRepeatCancelReason Expected, Func<HoldRepeatSafetyState, HoldRepeatSafetyState> Mutate)[]
        {
            (HoldRepeatCancelReason.Released, value => value with { ReleaseObserved = true }),
            (HoldRepeatCancelReason.Released, value => value with { PhysicalControlDown = false }),
            (HoldRepeatCancelReason.Fault, value => value with { Faulted = true }),
            (HoldRepeatCancelReason.Disabled, value => value with { Enabled = false }),
            (HoldRepeatCancelReason.Conflict, value => value with { ConflictDetected = true }),
            (HoldRepeatCancelReason.Logout, value => value with { LoggedIn = false }),
            (HoldRepeatCancelReason.Death, value => value with { IsAlive = false }),
            (HoldRepeatCancelReason.Mounted, value => value with { IsMounted = true }),
            (HoldRepeatCancelReason.Stun, value => value with { IsStunned = true }),
            (HoldRepeatCancelReason.Knockback, value => value with { IsKnockbackActive = true }),
            (HoldRepeatCancelReason.PluginChange, value => value with { PluginStateMatches = false }),
            (HoldRepeatCancelReason.TerritoryChange, value => value with { TerritoryMatches = false }),
            (HoldRepeatCancelReason.InstanceChange, value => value with { InstanceMatches = false }),
            (HoldRepeatCancelReason.TargetChange, value => value with { TargetMatches = false }),
            (HoldRepeatCancelReason.ResolvedActionChange, value => value with { ResolvedActionMatches = false }),
            (HoldRepeatCancelReason.BindingChange, value => value with { BindingMatches = false }),
        };

        var press = 0L;
        foreach (var (expected, mutate) in cases)
        {
            var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 30_000));
            Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(++press), 1_000));
            var decision = engine.Tick(1_001, mutate(HoldRepeatSafetyState.SafeHeld), actionReady: true);
            Equal(HoldRepeatDecisionKind.Cancelled, decision.Kind);
            Equal(expected, decision.CancelReason);
            False(engine.Snapshot.HasActiveHold);
            Equal(
                expected == HoldRepeatCancelReason.Released ? HoldRepeatState.Idle : HoldRepeatState.NeedsRelease,
                engine.Snapshot.State);
        }
    }

    private static void MaximumDurationIsTerminal()
    {
        var engine = ReadyEngine(new HoldRepeatOptions(60, 60, 1_000));
        Equal(HoldRepeatStartResult.Started, engine.TryStart(Request(1), 1_000));
        var token = PulseAt(engine, 1_999);
        var terminal = engine.Tick(2_000, HoldRepeatSafetyState.SafeHeld, actionReady: true);

        Equal(HoldRepeatDecisionKind.Cancelled, terminal.Kind);
        Equal(HoldRepeatCancelReason.MaximumDuration, terminal.CancelReason);
        Equal(HoldRepeatState.NeedsRelease, engine.Snapshot.State);
        False(engine.IsTokenCurrent(token));
        Equal(HoldRepeatStartResult.RejectedNeedsRelease, engine.TryStart(Request(2), 2_001));
    }

    private static void RandomizedTracePreservesInvariants()
    {
        const int steps = 10_000;
        var options = new HoldRepeatOptions(60, 60, 1_000);
        var engine = new HoldRepeatEngine(options);
        var random = new Random(0x50514);
        var seenTokens = new HashSet<(long HoldId, long Ordinal)>();
        var lastOrdinalByHold = new Dictionary<long, long>();
        var lastPulseAtByHold = new Dictionary<long, long>();
        var latestTokenByHold = new Dictionary<long, HoldRepeatPulseToken>();
        var now = 0L;
        var nextPress = 0L;

        for (var step = 0; step < steps; step++)
        {
            now += random.Next(0, 76);
            var operation = random.Next(8);
            HoldRepeatDecision firstDecision = default;
            switch (operation)
            {
                case 0:
                    engine.ObserveRelease();
                    break;
                case 1:
                    nextPress++;
                    engine.TryStart(Request(nextPress), now);
                    break;
                case 2:
                    firstDecision = engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true);
                    Record(firstDecision);
                    // A second evaluation at the exact same time can never add a pulse.
                    var secondDecision = engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true);
                    NotEqual(HoldRepeatDecisionKind.Pulse, secondDecision.Kind);
                    break;
                case 3:
                    firstDecision = engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: false);
                    break;
                case 4:
                    firstDecision = engine.Tick(
                        now,
                        HoldRepeatSafetyState.SafeHeld with { TargetMatches = false },
                        actionReady: true);
                    break;
                case 5:
                    firstDecision = engine.Tick(
                        now,
                        HoldRepeatSafetyState.SafeHeld with { PluginStateMatches = false },
                        actionReady: true);
                    break;
                case 6:
                    engine.Cancel(HoldRepeatCancelReason.Fault);
                    break;
                case 7:
                    firstDecision = engine.Tick(
                        now,
                        HoldRepeatSafetyState.SafeHeld with { ReleaseObserved = true },
                        actionReady: true);
                    break;
            }

            var snapshot = engine.Snapshot;
            Equal(snapshot.State == HoldRepeatState.Active, snapshot.HasActiveHold);
            if (snapshot.HasActiveHold)
            {
                True(snapshot.HoldId > 0);
                True(snapshot.PressId > 0);
                True(snapshot.InputGeneration > 0);
                True(snapshot.NextPulseAtMilliseconds >= snapshot.StartedAtMilliseconds);
                True(now < snapshot.StartedAtMilliseconds + engine.Options.MaximumHoldMilliseconds);
            }
            else
            {
                Equal(0L, snapshot.HoldId);
                Equal(0L, snapshot.LastIssuedOrdinal);
                Equal(HoldRepeatDecisionKind.None, engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true).Kind);
            }

            Equal((long)seenTokens.Count, snapshot.Counters.PulsesIssued);
            if (firstDecision.Kind == HoldRepeatDecisionKind.Cancelled)
            {
                foreach (var token in latestTokenByHold.Values)
                {
                    if (token.HoldId == snapshot.HoldId) continue;
                    False(engine.IsTokenCurrent(token));
                }
            }
        }

        engine.Cancel(HoldRepeatCancelReason.Fault);
        foreach (var token in latestTokenByHold.Values)
        {
            False(engine.IsTokenCurrent(token));
        }

        void Record(HoldRepeatDecision decision)
        {
            if (decision.Kind != HoldRepeatDecisionKind.Pulse) return;
            var token = decision.Pulse;
            True(token.IsValid);
            True(seenTokens.Add((token.HoldId, token.Ordinal)));
            var previousOrdinal = lastOrdinalByHold.GetValueOrDefault(token.HoldId);
            Equal(previousOrdinal + 1, token.Ordinal);
            if (lastPulseAtByHold.TryGetValue(token.HoldId, out var previousAt))
            {
                True(token.IssuedAtMilliseconds - previousAt >= engine.Options.IntervalMilliseconds);
            }

            lastOrdinalByHold[token.HoldId] = token.Ordinal;
            lastPulseAtByHold[token.HoldId] = token.IssuedAtMilliseconds;
            latestTokenByHold[token.HoldId] = token;
            True(engine.IsTokenCurrent(token));
        }
    }

    private static HoldRepeatEngine ReadyEngine(HoldRepeatOptions? options = null)
    {
        var engine = options is { } value ? new HoldRepeatEngine(value) : new HoldRepeatEngine();
        engine.ObserveRelease();
        return engine;
    }

    private static HoldRepeatStartRequest Request(long id) => new(
        PressId: id,
        InputGeneration: id,
        ControlFingerprint: (ulong)(1_000 + id),
        IntentFingerprint: (ulong)(2_000 + id),
        IsCertifiedFreshPress: true);

    private static HoldRepeatPulseToken PulseAt(HoldRepeatEngine engine, long now)
    {
        var decision = engine.Tick(now, HoldRepeatSafetyState.SafeHeld, actionReady: true);
        Equal(HoldRepeatDecisionKind.Pulse, decision.Kind);
        True(decision.Pulse.IsValid);
        True(engine.IsTokenCurrent(decision.Pulse));
        return decision.Pulse;
    }

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

    private static void NotEqual<T>(T unexpected, T actual)
        where T : notnull
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new InvalidOperationException($"Did not expect {unexpected}.");
        }
    }
}
