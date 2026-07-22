using PulseQueue.Core;

internal static class RuntimeCoreTests
{
    private static readonly ExactActionTuple AttemptedAction = new(
        ActionType: 1,
        RequestedActionId: 10,
        ResolvedActionId: 11,
        TargetId: 100,
        Param: 7,
        Mode: 0,
        RouteId: 8);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("native outcome accepts an immediate action", ImmediateActionIsAccepted);
        yield return ("native outcome accepts a matching requested or resolved queue", MatchingRequestedOrResolvedQueueIsAccepted);
        yield return ("native outcome blocks a foreign or preexisting queue", ForeignOrPreexistingQueueIsBlocked);
        yield return ("native queue matching requires the complete immutable tuple", QueueMatchingRequiresCompleteTuple);
        yield return ("native outcome rejects a failed action without a queue", FailedActionWithoutQueueIsRejected);
        yield return ("only a newer generation can take an owned native queue", OnlyNewerGenerationCanTakeOwnedQueue);
        yield return ("changed or sent native queues lose ownership", ChangedOrSentQueueLosesOwnership);
        yield return ("exact owned native queue drain is authorized once", ExactOwnedQueueDrainIsOneShot);
        yield return ("queue drain invocation mode stays separate from stored queue mode", QueueDrainInvocationModeIsSeparate);
        yield return ("stale and foreign drains cannot consume valid ownership", StaleAndForeignDrainsPreserveOwnership);
        yield return ("changed drain identity invalidates queue ownership", ChangedDrainIdentityInvalidatesOwnership);
        yield return ("racing exact queue drains authorize exactly once", RacingExactQueueDrainsAuthorizeOnce);
        yield return ("charge timing uses the next charge boundary", ChargeTimingUsesNextChargeBoundary);
        yield return ("newer input generation invalidates older work", NewerGenerationInvalidatesOlderWork);
        yield return ("explicit generation invalidation cancels pending work", ExplicitInvalidationCancelsPendingWork);
        yield return ("new buffer intent replaces the older intent", NewIntentReplacesOlderIntent);
        yield return ("mounted state cancels the runtime buffer", MountedStateCancelsRuntimeBuffer);
        yield return ("racing buffer evaluations dispatch exactly once", RacingEvaluationsDispatchExactlyOnce);
        yield return ("Turbo immediate acknowledgement matches exact action identity", TurboImmediateAcknowledgementMatchesExactIdentity);
        yield return ("Turbo queued acknowledgement uses wrap-safe sequence ordering", TurboQueuedAcknowledgementUsesWrapSafeOrdering);
        yield return ("Turbo acknowledgement rejects mismatched action identity", TurboAcknowledgementRejectsMismatchedActionIdentity);
        yield return ("Turbo acknowledgement fails closed for missing or invalid fields", TurboAcknowledgementFailsClosedForInvalidFields);
    }

    private static void ImmediateActionIsAccepted()
    {
        var outcome = NativeActionOutcomeClassifier.Classify(
            originalReturned: true,
            NativeQueueSnapshot.Empty,
            NativeQueueSnapshot.Empty,
            AttemptedAction);

        Equal(NativeActionOutcome.ImmediateAcceptance, outcome);
    }

    private static void MatchingRequestedOrResolvedQueueIsAccepted()
    {
        var requestedQueue = SnapshotFor(AttemptedAction.RequestedActionId);
        var resolvedQueue = SnapshotFor(AttemptedAction.ResolvedActionId);

        Equal(
            NativeActionOutcome.MatchingNewQueue,
            NativeActionOutcomeClassifier.Classify(
                originalReturned: false,
                NativeQueueSnapshot.Empty,
                requestedQueue,
                AttemptedAction));
        Equal(
            NativeActionOutcome.MatchingNewQueue,
            NativeActionOutcomeClassifier.Classify(
                originalReturned: false,
                NativeQueueSnapshot.Empty,
                resolvedQueue,
                AttemptedAction));
    }

    private static void ForeignOrPreexistingQueueIsBlocked()
    {
        var foreignQueue = SnapshotFor(actionId: 999);
        Equal(
            NativeActionOutcome.ForeignOrPreexistingQueue,
            NativeActionOutcomeClassifier.Classify(
                originalReturned: false,
                foreignQueue,
                foreignQueue,
                AttemptedAction));

        var preexistingMatchingQueue = SnapshotFor(AttemptedAction.ResolvedActionId);
        Equal(
            NativeActionOutcome.ForeignOrPreexistingQueue,
            NativeActionOutcomeClassifier.Classify(
                originalReturned: true,
                preexistingMatchingQueue,
                preexistingMatchingQueue,
                AttemptedAction));
    }

    private static void FailedActionWithoutQueueIsRejected()
    {
        var outcome = NativeActionOutcomeClassifier.Classify(
            originalReturned: false,
            NativeQueueSnapshot.Empty,
            NativeQueueSnapshot.Empty,
            AttemptedAction);

        Equal(NativeActionOutcome.Rejected, outcome);
    }

    private static void QueueMatchingRequiresCompleteTuple()
    {
        var matching = SnapshotFor(AttemptedAction.ResolvedActionId);
        var mismatches = new[]
        {
            matching with { ActionType = matching.ActionType + 1 },
            matching with { TargetId = matching.TargetId + 1 },
            matching with { Param = matching.Param + 1 },
            matching with { Mode = matching.Mode + 1 },
            matching with { RouteId = matching.RouteId + 1 },
        };

        foreach (var mismatch in mismatches)
        {
            False(mismatch.Matches(AttemptedAction));
            Equal(
                NativeActionOutcome.ForeignOrPreexistingQueue,
                NativeActionOutcomeClassifier.Classify(
                    originalReturned: false,
                    NativeQueueSnapshot.Empty,
                    mismatch,
                    AttemptedAction));
        }
    }

    private static void NewerGenerationInvalidatesOlderWork()
    {
        var gate = new InputGenerationGate();
        var older = gate.Begin();
        var newer = gate.Begin();

        False(gate.IsCurrent(older));
        True(gate.IsCurrent(newer));
        Equal(newer, gate.Current);
    }

    private static void OnlyNewerGenerationCanTakeOwnedQueue()
    {
        var ownership = new NativeQueueOwnership();
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);

        True(ownership.TryClaimNewQueue(
            generation: 7,
            sequenceMarker: 42,
            NativeQueueSnapshot.Empty,
            queue,
            AttemptedAction));
        False(ownership.TryTakeForNewerInput(8, 42, NativeQueueSnapshot.Empty, out _));
        True(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(7, 42, queue, out _));
        True(ownership.TryTakeForNewerInput(8, 42, queue, out var replaceable));
        Equal(queue, replaceable);
        False(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(9, 42, queue, out _));
    }

    private static void ChangedOrSentQueueLosesOwnership()
    {
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);
        var ownership = new NativeQueueOwnership();
        True(ownership.TryClaimNewQueue(3, 12, NativeQueueSnapshot.Empty, queue, AttemptedAction));
        ownership.Reconcile(13, queue);
        False(ownership.HasOwnership);

        True(ownership.TryClaimNewQueue(4, 14, NativeQueueSnapshot.Empty, queue, AttemptedAction));
        ownership.Reconcile(14, queue with { TargetId = queue.TargetId + 1 });
        False(ownership.HasOwnership);

        False(ownership.TryClaimNewQueue(
            5,
            15,
            queue,
            queue,
            AttemptedAction));
    }

    private static void ExactOwnedQueueDrainIsOneShot()
    {
        const long generation = 17;
        const uint sequence = 91;
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(generation, sequence, queue);

        True(ownership.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            AttemptedAction));
        False(ownership.HasOwnership);
        False(ownership.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            AttemptedAction));
    }

    private static void QueueDrainInvocationModeIsSeparate()
    {
        const long generation = 19;
        const uint sequence = 97;
        const uint queueDrainInvocationMode = 1;
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(generation, sequence, queue);

        // The native invocation that drains an entry is Queue mode, while the
        // queue snapshot retains the mode stored when the entry was claimed.
        // Mixing those two identities must fail without consuming ownership.
        False(ownership.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            AttemptedAction with { Mode = queueDrainInvocationMode }));
        True(ownership.HasOwnership);

        var ownedQueueTuple = AttemptedAction with { Mode = queue.Mode };
        True(ownership.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            ownedQueueTuple));
        False(ownership.HasOwnership);
    }

    private static void StaleAndForeignDrainsPreserveOwnership()
    {
        const long generation = 23;
        const uint sequence = 107;
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(generation, sequence, queue);

        False(ownership.TryAuthorizeExactDrain(
            generation - 1,
            sequence,
            queue,
            AttemptedAction));
        True(ownership.HasOwnership);
        False(ownership.TryAuthorizeExactDrain(
            generation + 1,
            sequence,
            queue,
            AttemptedAction));
        True(ownership.HasOwnership);

        var tupleMismatches = new[]
        {
            AttemptedAction with { ActionType = AttemptedAction.ActionType + 1 },
            AttemptedAction with { RequestedActionId = AttemptedAction.RequestedActionId + 1 },
            AttemptedAction with { ResolvedActionId = AttemptedAction.ResolvedActionId + 1 },
            AttemptedAction with { TargetId = AttemptedAction.TargetId + 1 },
            AttemptedAction with { Param = AttemptedAction.Param + 1 },
            AttemptedAction with { Mode = AttemptedAction.Mode + 1 },
            AttemptedAction with { RouteId = AttemptedAction.RouteId + 1 },
        };
        foreach (var mismatch in tupleMismatches)
        {
            False(ownership.TryAuthorizeExactDrain(
                generation,
                sequence,
                queue,
                mismatch));
            True(ownership.HasOwnership);
        }

        True(ownership.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            AttemptedAction));
    }

    private static void ChangedDrainIdentityInvalidatesOwnership()
    {
        const long generation = 29;
        const uint sequence = 131;
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);

        var changedSequence = ClaimedOwnership(generation, sequence, queue);
        False(changedSequence.TryAuthorizeExactDrain(
            generation,
            sequence + 1,
            queue,
            AttemptedAction));
        False(changedSequence.HasOwnership);
        False(changedSequence.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue,
            AttemptedAction));

        var changedSnapshot = ClaimedOwnership(generation, sequence, queue);
        False(changedSnapshot.TryAuthorizeExactDrain(
            generation,
            sequence,
            queue with { TargetId = queue.TargetId + 1 },
            AttemptedAction));
        False(changedSnapshot.HasOwnership);

        var sentQueue = ClaimedOwnership(generation, sequence, queue);
        False(sentQueue.TryAuthorizeExactDrain(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            AttemptedAction));
        False(sentQueue.HasOwnership);
    }

    private static void RacingExactQueueDrainsAuthorizeOnce()
    {
        const int contenderCount = 32;
        const long generation = 31;
        const uint sequence = 149;
        var queue = SnapshotFor(AttemptedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(generation, sequence, queue);
        using var start = new ManualResetEventSlim(initialState: false);
        var contenders = Enumerable.Range(0, contenderCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return ownership.TryAuthorizeExactDrain(
                    generation,
                    sequence,
                    queue,
                    AttemptedAction);
            }))
            .ToArray();

        start.Set();
        Task.WaitAll(contenders);

        Equal(1, contenders.Count(task => task.Result));
        False(ownership.HasOwnership);
    }

    private static void ChargeTimingUsesNextChargeBoundary()
    {
        Equal(
            150.0,
            CooldownTiming.GetNextChargeRemainingMilliseconds(
                totalSeconds: 120,
                elapsedSeconds: 59.85,
                maximumCharges: 2),
            tolerance: 0.0001);
        Equal(
            60_150.0,
            CooldownTiming.GetNextChargeRemainingMilliseconds(
                totalSeconds: 120,
                elapsedSeconds: 59.85,
                maximumCharges: 1),
            tolerance: 0.0001);
        True(double.IsPositiveInfinity(
            CooldownTiming.GetNextChargeRemainingMilliseconds(120, 0, maximumCharges: 0)));
    }

    private static void ExplicitInvalidationCancelsPendingWork()
    {
        var gate = new InputGenerationGate();
        var pending = gate.Begin();
        var invalidatedAt = gate.Invalidate();

        False(gate.IsCurrent(pending));
        True(gate.IsCurrent(invalidatedAt));
        Equal(invalidatedAt, gate.Current);
    }

    private static void NewIntentReplacesOlderIntent()
    {
        var engine = new BufferEngine();
        var older = IntentFor(requestedActionId: 101, resolvedActionId: 102, targetId: 1001);
        var newer = IntentFor(requestedActionId: 201, resolvedActionId: 202, targetId: 2001);

        True(engine.Arm(older, originalAttemptAtMilliseconds: 1_000, holdMilliseconds: 180));
        True(engine.Arm(newer, originalAttemptAtMilliseconds: 1_001, holdMilliseconds: 180));
        Equal(newer, engine.Pending.GetValueOrDefault());

        var decision = engine.Evaluate(
            new BufferContext(BufferSafetyState.SafeFor(newer.Action), ActionIsExecutable: true),
            nowMilliseconds: 1_002);

        Equal(BufferDecisionKind.Dispatch, decision.Kind);
        Equal(newer, decision.Intent.GetValueOrDefault());
        False(engine.Pending.HasValue);
        Equal(
            BufferDecisionKind.None,
            engine.Evaluate(
                new BufferContext(BufferSafetyState.SafeFor(newer.Action), ActionIsExecutable: true),
                nowMilliseconds: 1_003).Kind);
    }

    private static void RacingEvaluationsDispatchExactlyOnce()
    {
        const int contenderCount = 32;
        var engine = new BufferEngine();
        var intent = IntentFor(requestedActionId: 301, resolvedActionId: 302, targetId: 3001);
        var context = new BufferContext(
            BufferSafetyState.SafeFor(intent.Action),
            ActionIsExecutable: true);

        True(engine.Arm(intent, originalAttemptAtMilliseconds: 2_000, holdMilliseconds: 180));

        using var start = new ManualResetEventSlim(initialState: false);
        var contenders = Enumerable.Range(0, contenderCount)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return engine.Evaluate(context, nowMilliseconds: 2_001);
            }))
            .ToArray();

        start.Set();
        Task.WaitAll(contenders);

        var decisions = contenders.Select(task => task.Result).ToArray();
        Equal(1, decisions.Count(decision => decision.Kind == BufferDecisionKind.Dispatch));
        Equal(contenderCount - 1, decisions.Count(decision => decision.Kind == BufferDecisionKind.None));
        Equal(
            intent,
            decisions.Single(decision => decision.Kind == BufferDecisionKind.Dispatch)
                .Intent.GetValueOrDefault());
        False(engine.Pending.HasValue);
    }

    private static void MountedStateCancelsRuntimeBuffer()
    {
        var engine = new BufferEngine();
        var intent = IntentFor(requestedActionId: 251, resolvedActionId: 252, targetId: 2501);
        True(engine.Arm(intent, originalAttemptAtMilliseconds: 1_500, holdMilliseconds: 180));

        var decision = engine.Evaluate(
            new BufferContext(
                BufferSafetyState.SafeFor(intent.Action) with { IsMounted = true },
                ActionIsExecutable: true),
            nowMilliseconds: 1_501);

        Equal(BufferDecisionKind.Cancelled, decision.Kind);
        Equal(CancelReason.Mounted, decision.Reason);
        False(engine.Pending.HasValue);
    }

    private static void TurboImmediateAcknowledgementMatchesExactIdentity()
    {
        var expectation = ImmediateTurboExpectation(sequence: 42);

        True(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            new TurboActionEffectObservation(
                AttemptedAction.ActionType,
                AttemptedAction.RequestedActionId,
                SourceSequence: 42)));
        True(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            new TurboActionEffectObservation(
                AttemptedAction.ActionType,
                AttemptedAction.ResolvedActionId,
                SourceSequence: 42)));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            new TurboActionEffectObservation(
                AttemptedAction.ActionType,
                AttemptedAction.ResolvedActionId,
                SourceSequence: 43)));
    }

    private static void TurboQueuedAcknowledgementUsesWrapSafeOrdering()
    {
        var expectation = QueuedTurboExpectation(baseline: 65_534);

        True(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 65_535)));
        True(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 1)));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 65_534)));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 32_766)));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 65_533)));

        True(TurboActionEffectAcknowledgementMatcher.IsWrapSafeNewer(1, 65_534));
        False(TurboActionEffectAcknowledgementMatcher.IsWrapSafeNewer(32_769, 1));
    }

    private static void TurboAcknowledgementRejectsMismatchedActionIdentity()
    {
        var expectation = ImmediateTurboExpectation(sequence: 17);

        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 17) with
            {
                ActionType = AttemptedAction.ActionType + 1,
            }));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            TurboObservation(sourceSequence: 17) with { ActionId = 999 }));
    }

    private static void TurboAcknowledgementFailsClosedForInvalidFields()
    {
        var expectation = ImmediateTurboExpectation(sequence: 21);
        var observation = TurboObservation(sourceSequence: 21);

        False(TurboActionEffectAcknowledgementMatcher.Matches(null, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(expectation, null));
        False(TurboActionEffectAcknowledgementMatcher.Matches(default, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(expectation, default));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation with { ActionType = 0 }, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation with { RequestedActionId = 0 }, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation with { ResolvedActionId = 0 }, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation with { SequenceMarker = 0 }, observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation with { SequenceMode = (TurboAcknowledgementSequenceMode)99 },
            observation));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            observation with { ActionType = 0 }));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            observation with { ActionId = 0 }));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            expectation,
            observation with { SourceSequence = 0 }));
        False(TurboActionEffectAcknowledgementMatcher.Matches(
            QueuedTurboExpectation(baseline: 0), observation));
        False(TurboActionEffectAcknowledgementMatcher.IsWrapSafeNewer(1, 0));
        False(TurboActionEffectAcknowledgementMatcher.IsWrapSafeNewer(0, 1));
    }

    private static TurboActionEffectExpectation ImmediateTurboExpectation(ushort sequence) => new(
        AttemptedAction.ActionType,
        AttemptedAction.RequestedActionId,
        AttemptedAction.ResolvedActionId,
        TurboAcknowledgementSequenceMode.ImmediateExact,
        sequence);

    private static TurboActionEffectExpectation QueuedTurboExpectation(ushort baseline) => new(
        AttemptedAction.ActionType,
        AttemptedAction.RequestedActionId,
        AttemptedAction.ResolvedActionId,
        TurboAcknowledgementSequenceMode.QueuedAfterBaseline,
        baseline);

    private static TurboActionEffectObservation TurboObservation(ushort sourceSequence) => new(
        AttemptedAction.ActionType,
        AttemptedAction.ResolvedActionId,
        sourceSequence);

    private static NativeQueueSnapshot SnapshotFor(uint actionId) => new(
        IsQueued: true,
        AttemptedAction.ActionType,
        actionId,
        AttemptedAction.TargetId,
        AttemptedAction.Param,
        AttemptedAction.Mode,
        AttemptedAction.RouteId);

    private static NativeQueueOwnership ClaimedOwnership(
        long generation,
        uint sequence,
        NativeQueueSnapshot queue)
    {
        var ownership = new NativeQueueOwnership();
        True(ownership.TryClaimNewQueue(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            queue,
            AttemptedAction));
        return ownership;
    }

    private static BufferIntent IntentFor(uint requestedActionId, uint resolvedActionId, ulong targetId)
    {
        var action = new ActionRequest(
            requestedActionId,
            resolvedActionId,
            targetId,
            TerritoryId: 7,
            InstanceId: 9);
        return new BufferIntent(
            action,
            ActionFailureKind.AnimationLock,
            IsEligibleForBuffering: true);
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true, got false.");
        }
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

    private static void Equal(double expected, double actual, double tolerance)
    {
        if (!double.IsFinite(expected)
            || !double.IsFinite(actual)
            || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }
}
