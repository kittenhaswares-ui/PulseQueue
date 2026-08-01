using PulseQueue.Core;

return SelfTests.RunAll();

internal static class SelfTests
{
    private static readonly ActionRequest BaseAction = new(
        RequestedActionId: 10,
        ResolvedActionId: 11,
        TargetId: 100,
        TerritoryId: 200,
        InstanceId: 300);

    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    public static int RunAll()
    {
        var tests = new List<(string Name, Action Body)>
        {
            ("buffer starts idle", BufferStartsIdle),
            ("original attempt awaits outcome", OriginalAttemptAwaitsOutcome),
            ("GCD rejection can arm", GcdRejectionCanArm),
            ("animation-lock rejection can arm", AnimationLockRejectionCanArm),
            ("short local-cooldown rejection can arm", LocalCooldownRejectionCanArm),
            ("every non-transient rejection is terminal", NonTransientRejectionsAreTerminal),
            ("ineligible action cannot arm", IneligibleActionCannotArm),
            ("server rejection cannot arm", ServerRejectionCannotArm),
            ("new original attempt replaces pending", NewAttemptReplacesPending),
            ("stale rejection cannot affect replacement", StaleRejectionCannotAffectReplacement),
            ("stale acceptance cannot affect replacement", StaleAcceptanceCannotAffectReplacement),
            ("eligibility time is observed", EligibilityTimeIsObserved),
            ("runtime eligibility is required", RuntimeEligibilityIsRequired),
            ("dispatch happens exactly once", DispatchHappensExactlyOnce),
            ("dispatch preserves exact action and target", DispatchPreservesExactAction),
            ("expiry wins at the absolute boundary", ExpiryWinsAtAbsoluteBoundary),
            ("buffer expires after the absolute cap", BufferExpiresAfterAbsoluteCap),
            ("late rejection cannot arm", LateRejectionCannotArm),
            ("eligibility beyond cap cannot arm", EligibilityBeyondCapCannotArm),
            ("deadline is anchored to original press", DeadlineAnchoredToOriginalPress),
            ("disabled clears", () => SafetyConditionClears(s => s with { Enabled = false }, BufferClearReason.Disabled)),
            ("conflict clears", () => SafetyConditionClears(s => s with { ConflictDetected = true }, BufferClearReason.ConflictDetected)),
            ("logout clears", () => SafetyConditionClears(s => s with { LoggedIn = false }, BufferClearReason.LoggedOut)),
            ("death clears", () => SafetyConditionClears(s => s with { IsAlive = false }, BufferClearReason.Dead)),
            ("stun clears", () => SafetyConditionClears(s => s with { IsStunned = true }, BufferClearReason.Stunned)),
            ("knockback clears", () => SafetyConditionClears(s => s with { IsKnockbackActive = true }, BufferClearReason.Knockback)),
            ("territory change clears", () => SafetyConditionClears(s => s with { TerritoryId = s.TerritoryId + 1 }, BufferClearReason.TerritoryChanged)),
            ("instance change clears", () => SafetyConditionClears(s => s with { InstanceId = s.InstanceId + 1 }, BufferClearReason.InstanceChanged)),
            ("target change clears", () => SafetyConditionClears(s => s with { TargetId = s.TargetId + 1 }, BufferClearReason.TargetChanged)),
            ("resolved action change clears", () => SafetyConditionClears(s => s with { ResolvedActionId = s.ResolvedActionId + 1 }, BufferClearReason.ResolvedActionChanged)),
            ("unsafe original never becomes candidate", UnsafeOriginalNeverBecomesCandidate),
            ("server rejection clears armed action without retry", ServerRejectionClearsArmedAction),
            ("server rejection after dispatch cannot retry", ServerRejectionAfterDispatchCannotRetry),
            ("stale server rejection cannot clear new action", StaleServerRejectionCannotClearNewAction),
            ("accepted original clears candidate", AcceptedOriginalClearsCandidate),
            ("explicit cancellation is terminal", ExplicitCancellationIsTerminal),
            ("invalid action IDs fail closed", InvalidActionIdsFailClosed),
            ("estimator starts at configured minimum", EstimatorStartsAtMinimum),
            ("estimator first sample is deterministic", EstimatorFirstSampleIsDeterministic),
            ("estimator EWMA is deterministic", EstimatorEwmaIsDeterministic),
            ("invalid RTT samples are ignored", InvalidSamplesAreIgnored),
            ("single RTT outlier is ignored", SingleOutlierIsIgnored),
            ("ordinary jitter remains adaptive", OrdinaryJitterIsAccepted),
            ("consistent RTT shift rebases", ConsistentShiftRebases),
            ("inconsistent outliers do not rebase", InconsistentOutliersDoNotRebase),
            ("suggested hold clamps low", SuggestedHoldClampsLow),
            ("suggested hold honors configured maximum", SuggestedHoldHonorsConfiguredMaximum),
            ("suggested hold never exceeds absolute cap", SuggestedHoldNeverExceedsAbsoluteCap),
            ("estimator reset clears all state", EstimatorResetClearsState),
            ("invalid estimator options fail closed", InvalidEstimatorOptionsFailClosed),
            ("estimator clamp invariant across sample range", EstimatorClampInvariantAcrossRange),
            ("deterministic randomized trace preserves invariants", DeterministicRandomizedTracePreservesInvariants),
        };
        tests.AddRange(RuntimeCoreTests.All());
        tests.AddRange(HoldRepeatTests.All());
        tests.AddRange(LogicalHotbarRepeatTests.All());
        tests.AddRange(RepeatNativeQueueOwnershipTests.All());
        tests.AddRange(MacroSafetyTests.All());
        tests.AddRange(MacroTurboTranscriptTests.All());
        tests.AddRange(PhysicalHoldLatchTests.All());
        tests.AddRange(PluginConfigurationTests.All());

        var failures = new List<string>();
        foreach (var (name, body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures.Add($"FAIL  {name}: {exception.Message}");
                Console.Error.WriteLine(failures[^1]);
            }
        }

        Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} tests passed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void BufferStartsIdle()
    {
        var buffer = new OneShotActionBuffer();
        Equal(BufferLifecycleState.Idle, buffer.State);
        False(buffer.HasPending);
    }

    private static void OriginalAttemptAwaitsOutcome()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        True(token.IsValid);
        Equal(BufferLifecycleState.AwaitingOriginalOutcome, buffer.State);
        False(buffer.HasPending);
    }

    private static void GcdRejectionCanArm()
    {
        var buffer = ArmedBuffer(ActionFailureKind.GlobalCooldown);
        Equal(BufferLifecycleState.Buffered, buffer.State);
    }

    private static void AnimationLockRejectionCanArm()
    {
        var buffer = ArmedBuffer(ActionFailureKind.AnimationLock);
        Equal(BufferLifecycleState.Buffered, buffer.State);
    }

    private static void LocalCooldownRejectionCanArm()
    {
        var buffer = ArmedBuffer(ActionFailureKind.Cooldown);
        Equal(BufferLifecycleState.Buffered, buffer.State);
    }

    private static void NonTransientRejectionsAreTerminal()
    {
        var failures = Enum.GetValues<ActionFailureKind>()
            .Where(f => f is not ActionFailureKind.GlobalCooldown
                and not ActionFailureKind.AnimationLock
                and not ActionFailureKind.Cooldown
                and not ActionFailureKind.ServerRejected);

        foreach (var failure in failures)
        {
            var buffer = new OneShotActionBuffer();
            var token = Begin(buffer);
            Equal(
                ArmResult.RejectedNonTransientFailure,
                Reject(buffer, token, failure, T0 + Ms(50)));
            Equal(BufferLifecycleState.Idle, buffer.State);
        }
    }

    private static void IneligibleActionCannotArm()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(
            ArmResult.RejectedIneligibleAction,
            Reject(buffer, token, ActionFailureKind.GlobalCooldown, T0 + Ms(50), eligible: false));
        Equal(BufferLifecycleState.Idle, buffer.State);
    }

    private static void ServerRejectionCannotArm()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(
            ArmResult.RejectedServerFailure,
            Reject(buffer, token, ActionFailureKind.ServerRejected, T0 + Ms(50)));
        False(Take(buffer, T0 + Ms(50)));
    }

    private static void NewAttemptReplacesPending()
    {
        var buffer = ArmedBuffer();
        var replacement = BaseAction with { RequestedActionId = 20, ResolvedActionId = 21 };
        var token = buffer.BeginOriginalAttempt(replacement, T0 + Ms(20), BufferSafetyState.SafeFor(replacement));
        True(token.IsValid);
        Equal(BufferLifecycleState.AwaitingOriginalOutcome, buffer.State);
        Equal(BufferClearReason.ReplacedByOriginalAttempt, buffer.LastClearReason);
        False(Take(buffer, T0 + Ms(50), BufferSafetyState.SafeFor(replacement)));
    }

    private static void StaleRejectionCannotAffectReplacement()
    {
        var buffer = new OneShotActionBuffer();
        var oldToken = Begin(buffer);
        var newToken = Begin(buffer, T0 + Ms(5));

        Equal(
            ArmResult.IgnoredStaleAttempt,
            Reject(buffer, oldToken, ActionFailureKind.GlobalCooldown, T0 + Ms(20)));
        Equal(BufferLifecycleState.AwaitingOriginalOutcome, buffer.State);
        Equal(ArmResult.Armed, Reject(buffer, newToken, ActionFailureKind.GlobalCooldown, T0 + Ms(20)));
    }

    private static void StaleAcceptanceCannotAffectReplacement()
    {
        var buffer = new OneShotActionBuffer();
        var oldToken = Begin(buffer);
        _ = Begin(buffer, T0 + Ms(5));
        False(buffer.ReportAccepted(oldToken));
        Equal(BufferLifecycleState.AwaitingOriginalOutcome, buffer.State);
    }

    private static void EligibilityTimeIsObserved()
    {
        var buffer = ArmedBuffer(eligibleAt: T0 + Ms(80));
        False(Take(buffer, T0 + Ms(79)));
        True(Take(buffer, T0 + Ms(80)));
    }

    private static void RuntimeEligibilityIsRequired()
    {
        var buffer = ArmedBuffer(eligibleAt: T0 + Ms(40));
        False(Take(buffer, T0 + Ms(50), executable: false));
        True(buffer.HasPending);
        True(Take(buffer, T0 + Ms(60), executable: true));
    }

    private static void DispatchHappensExactlyOnce()
    {
        var buffer = ArmedBuffer();
        True(Take(buffer, T0 + Ms(50)));
        Equal(BufferClearReason.Dispatched, buffer.LastClearReason);
        False(Take(buffer, T0 + Ms(50)));
        False(Take(buffer, T0 + Ms(51)));
    }

    private static void DispatchPreservesExactAction()
    {
        var action = BaseAction with { RequestedActionId = 501, ResolvedActionId = 502, TargetId = 999 };
        var buffer = new OneShotActionBuffer();
        var token = buffer.BeginOriginalAttempt(action, T0, BufferSafetyState.SafeFor(action));
        Equal(
            ArmResult.Armed,
            buffer.ReportOriginalRejection(
                token,
                ActionFailureKind.AnimationLock,
                true,
                T0 + Ms(20),
                T0 + Ms(5),
                BufferSafetyState.SafeFor(action)));
        True(buffer.TryTakeDispatch(T0 + Ms(20), BufferSafetyState.SafeFor(action), true, out var dispatch));
        Equal(token, dispatch.Attempt);
        Equal(action, dispatch.Action);
        Equal(T0, dispatch.OriginalAttemptedAt);
    }

    private static void ExpiryWinsAtAbsoluteBoundary()
    {
        var buffer = ArmedBuffer(eligibleAt: T0 + OneShotActionBuffer.AbsoluteHoldCap - Ms(1));
        False(Take(buffer, T0 + OneShotActionBuffer.AbsoluteHoldCap));
        Equal(BufferClearReason.Expired, buffer.LastClearReason);

        var second = new OneShotActionBuffer();
        var token = Begin(second);
        Equal(
            ArmResult.RejectedBeyondAbsoluteHoldCap,
            Reject(second, token, ActionFailureKind.GlobalCooldown, T0 + OneShotActionBuffer.AbsoluteHoldCap));
    }

    private static void BufferExpiresAfterAbsoluteCap()
    {
        var buffer = ArmedBuffer();
        False(Take(buffer, T0 + OneShotActionBuffer.AbsoluteHoldCap + Ms(1)));
        Equal(BufferLifecycleState.Idle, buffer.State);
        Equal(BufferClearReason.Expired, buffer.LastClearReason);
    }

    private static void LateRejectionCannotArm()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(
            ArmResult.RejectedExpired,
            Reject(
                buffer,
                token,
                ActionFailureKind.GlobalCooldown,
                T0 + OneShotActionBuffer.AbsoluteHoldCap + Ms(1),
                observedAt: T0 + OneShotActionBuffer.AbsoluteHoldCap + Ms(1)));
    }

    private static void EligibilityBeyondCapCannotArm()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(
            ArmResult.RejectedBeyondAbsoluteHoldCap,
            Reject(
                buffer,
                token,
                ActionFailureKind.GlobalCooldown,
                T0 + OneShotActionBuffer.AbsoluteHoldCap + Ms(1)));
    }

    private static void DeadlineAnchoredToOriginalPress()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(
            ArmResult.Armed,
            Reject(
                buffer,
                token,
                ActionFailureKind.GlobalCooldown,
                T0 + OneShotActionBuffer.AbsoluteHoldCap - Ms(1),
                observedAt: T0 + OneShotActionBuffer.AbsoluteHoldCap - Ms(10)));
        False(Take(buffer, T0 + OneShotActionBuffer.AbsoluteHoldCap));
    }

    private static void SafetyConditionClears(
        Func<BufferSafetyState, BufferSafetyState> mutate,
        BufferClearReason expectedReason)
    {
        var buffer = ArmedBuffer(eligibleAt: T0 + Ms(100));
        buffer.ObserveSafety(mutate(BufferSafetyState.SafeFor(BaseAction)));
        Equal(BufferLifecycleState.Idle, buffer.State);
        Equal(expectedReason, buffer.LastClearReason);
        False(Take(buffer, T0 + Ms(100)));
    }

    private static void UnsafeOriginalNeverBecomesCandidate()
    {
        var buffer = new OneShotActionBuffer();
        var unsafeState = BufferSafetyState.SafeFor(BaseAction) with { ConflictDetected = true };
        _ = buffer.BeginOriginalAttempt(BaseAction, T0, unsafeState);
        Equal(BufferLifecycleState.Idle, buffer.State);
        Equal(BufferClearReason.ConflictDetected, buffer.LastClearReason);
    }

    private static void ServerRejectionClearsArmedAction()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(ArmResult.Armed, Reject(buffer, token, ActionFailureKind.GlobalCooldown, T0 + Ms(50)));
        True(buffer.ReportServerRejection(token));
        False(Take(buffer, T0 + Ms(50)));
        Equal(BufferClearReason.ServerRejected, buffer.LastClearReason);
    }

    private static void ServerRejectionAfterDispatchCannotRetry()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(ArmResult.Armed, Reject(buffer, token, ActionFailureKind.GlobalCooldown, T0 + Ms(50)));
        True(Take(buffer, T0 + Ms(50)));
        False(buffer.ReportServerRejection(token));
        False(Take(buffer, T0 + Ms(60)));
    }

    private static void StaleServerRejectionCannotClearNewAction()
    {
        var buffer = new OneShotActionBuffer();
        var oldToken = Begin(buffer);
        var newToken = Begin(buffer, T0 + Ms(5));
        False(buffer.ReportServerRejection(oldToken));
        Equal(BufferLifecycleState.AwaitingOriginalOutcome, buffer.State);
        Equal(ArmResult.Armed, Reject(buffer, newToken, ActionFailureKind.GlobalCooldown, T0 + Ms(30)));
    }

    private static void AcceptedOriginalClearsCandidate()
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        True(buffer.ReportAccepted(token));
        Equal(BufferLifecycleState.Idle, buffer.State);
        Equal(BufferClearReason.Accepted, buffer.LastClearReason);
    }

    private static void ExplicitCancellationIsTerminal()
    {
        var buffer = ArmedBuffer();
        True(buffer.Cancel());
        False(buffer.Cancel());
        False(Take(buffer, T0 + Ms(50)));
        Equal(BufferClearReason.ExplicitCancellation, buffer.LastClearReason);
    }

    private static void InvalidActionIdsFailClosed()
    {
        var buffer = new OneShotActionBuffer();
        Throws<ArgumentOutOfRangeException>(() =>
            buffer.BeginOriginalAttempt(BaseAction with { RequestedActionId = 0 }, T0, BufferSafetyState.SafeFor(BaseAction)));
        Throws<ArgumentOutOfRangeException>(() =>
            buffer.BeginOriginalAttempt(BaseAction with { ResolvedActionId = 0 }, T0, BufferSafetyState.SafeFor(BaseAction)));
        Equal(BufferLifecycleState.Idle, buffer.State);
    }

    private static void EstimatorStartsAtMinimum()
    {
        var estimator = new AdaptiveRttEstimator();
        False(estimator.HasEstimate);
        Equal(Ms(20), estimator.SuggestedHold);
    }

    private static void EstimatorFirstSampleIsDeterministic()
    {
        var estimator = new AdaptiveRttEstimator();
        Equal(RttSampleResult.Accepted, estimator.AddSample(Ms(80)));
        Equal(Ms(80), estimator.EstimatedRtt);
        Equal(Ms(40), estimator.EstimatedVariation);
        Equal(Ms(165), estimator.SuggestedHold);
    }

    private static void EstimatorEwmaIsDeterministic()
    {
        var estimator = new AdaptiveRttEstimator();
        _ = estimator.AddSample(Ms(80));
        _ = estimator.AddSample(Ms(96));
        Equal(Ms(82), estimator.EstimatedRtt);
        Equal(Ms(34), estimator.EstimatedVariation);
        Equal(Ms(155), estimator.SuggestedHold);
    }

    private static void InvalidSamplesAreIgnored()
    {
        var estimator = new AdaptiveRttEstimator();
        Equal(RttSampleResult.IgnoredInvalid, estimator.AddSample(TimeSpan.Zero));
        Equal(RttSampleResult.IgnoredInvalid, estimator.AddSample(Ms(-1)));
        Equal(RttSampleResult.IgnoredInvalid, estimator.AddSample(TimeSpan.FromMilliseconds(2001)));
        False(estimator.HasEstimate);
        Equal(3, estimator.ObservedSampleCount);
        Equal(3, estimator.IgnoredInvalidCount);
        Equal(0, estimator.AcceptedSampleCount);
    }

    private static void SingleOutlierIsIgnored()
    {
        var estimator = SeedStableEstimator(50);
        var beforeRtt = estimator.EstimatedRtt;
        var beforeVariation = estimator.EstimatedVariation;
        Equal(RttSampleResult.IgnoredOutlier, estimator.AddSample(Ms(250)));
        Equal(beforeRtt, estimator.EstimatedRtt);
        Equal(beforeVariation, estimator.EstimatedVariation);
        Equal(1, estimator.IgnoredOutlierCount);
    }

    private static void OrdinaryJitterIsAccepted()
    {
        var estimator = SeedStableEstimator(50);
        Equal(RttSampleResult.Accepted, estimator.AddSample(Ms(60)));
        True(estimator.EstimatedRtt > Ms(50));
        True(estimator.EstimatedRtt < Ms(60));
    }

    private static void ConsistentShiftRebases()
    {
        var estimator = SeedStableEstimator(50);
        Equal(RttSampleResult.IgnoredOutlier, estimator.AddSample(Ms(100)));
        Equal(RttSampleResult.IgnoredOutlier, estimator.AddSample(Ms(103)));
        Equal(RttSampleResult.AcceptedRebase, estimator.AddSample(Ms(98)));
        InRange(estimator.EstimatedRtt, Ms(100), Ms(101));
        Equal(RttSampleResult.Accepted, estimator.AddSample(Ms(101)));
    }

    private static void InconsistentOutliersDoNotRebase()
    {
        var estimator = SeedStableEstimator(50);
        _ = estimator.AddSample(Ms(100));
        _ = estimator.AddSample(Ms(180));
        _ = estimator.AddSample(Ms(100));
        Equal(Ms(50), estimator.EstimatedRtt);
    }

    private static void SuggestedHoldClampsLow()
    {
        var estimator = new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            MinimumSuggestedHold = Ms(30),
            SafetyMargin = TimeSpan.Zero,
            SuggestedHoldVariationMultiplier = 0,
        });
        _ = estimator.AddSample(Ms(5));
        Equal(Ms(30), estimator.SuggestedHold);
    }

    private static void SuggestedHoldHonorsConfiguredMaximum()
    {
        var estimator = new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            MaximumSuggestedHold = Ms(90),
        });
        _ = estimator.AddSample(Ms(100));
        Equal(Ms(90), estimator.SuggestedHold);
    }

    private static void SuggestedHoldNeverExceedsAbsoluteCap()
    {
        var estimator = new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            MaximumSuggestedHold = Ms(500),
            MaximumAcceptedSample = TimeSpan.FromSeconds(5),
        });
        _ = estimator.AddSample(Ms(1000));
        Equal(OneShotActionBuffer.AbsoluteHoldCap, estimator.SuggestedHold);
    }

    private static void EstimatorResetClearsState()
    {
        var estimator = SeedStableEstimator(50);
        _ = estimator.AddSample(Ms(500));
        estimator.Reset();
        False(estimator.HasEstimate);
        Equal(0, estimator.ObservedSampleCount);
        Equal(0, estimator.AcceptedSampleCount);
        Equal(0, estimator.IgnoredInvalidCount);
        Equal(0, estimator.IgnoredOutlierCount);
        Equal(TimeSpan.Zero, estimator.EstimatedRtt);
        Equal(Ms(20), estimator.SuggestedHold);
    }

    private static void InvalidEstimatorOptionsFailClosed()
    {
        Throws<ArgumentOutOfRangeException>(() => new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            MinimumSuggestedHold = Ms(100),
            MaximumSuggestedHold = Ms(50),
        }));
        Throws<ArgumentOutOfRangeException>(() => new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            SmoothingFactor = 0,
        }));
        Throws<ArgumentOutOfRangeException>(() => new AdaptiveRttEstimator(new AdaptiveRttOptions
        {
            ConsistentOutliersToRebase = 1,
        }));
    }

    private static void EstimatorClampInvariantAcrossRange()
    {
        var estimator = new AdaptiveRttEstimator();
        for (var milliseconds = 1; milliseconds <= 2000; milliseconds += 7)
        {
            _ = estimator.AddSample(Ms(milliseconds));
            InRange(estimator.SuggestedHold, Ms(20), OneShotActionBuffer.AbsoluteHoldCap);
        }
    }

    private static void DeterministicRandomizedTracePreservesInvariants()
    {
        var random = new Random(0x50514);
        var engine = new BufferEngine();
        var dispatchedActionIds = new HashSet<uint>();
        long now = 0;
        uint nextActionId = 1;

        for (var step = 0; step < 10_000; step++)
        {
            now += random.Next(0, 8);
            switch (random.Next(5))
            {
                case 0:
                case 1:
                    {
                        var action = new ActionRequest(
                            nextActionId++,
                            nextActionId++,
                            (ulong)random.Next(0, 5),
                            7,
                            9);
                        var intent = new BufferIntent(
                            action,
                            random.Next(2) == 0
                                ? ActionFailureKind.GlobalCooldown
                                : ActionFailureKind.AnimationLock,
                            IsEligibleForBuffering: true);
                        True(engine.Arm(intent, now, random.Next(1, 260)));
                        Equal(intent, engine.Pending!.Value);
                        break;
                    }

                case 2:
                    engine.Cancel(CancelReason.Explicit);
                    False(engine.Pending.HasValue);
                    break;

                default:
                    {
                        var pendingBefore = engine.Pending;
                        var safety = pendingBefore.HasValue
                            ? BufferSafetyState.SafeFor(pendingBefore.Value.Action)
                            : BufferSafetyState.SafeFor(BaseAction);
                        var decision = engine.Evaluate(
                            new BufferContext(safety, ActionIsExecutable: random.Next(2) == 0),
                            now);

                        if (decision.Kind == BufferDecisionKind.Dispatch)
                        {
                            True(pendingBefore.HasValue);
                            True(decision.Intent.HasValue);
                            var expectedIntent = pendingBefore.GetValueOrDefault();
                            var dispatchedIntent = decision.Intent.GetValueOrDefault();
                            Equal(expectedIntent, dispatchedIntent);
                            True(dispatchedActionIds.Add(dispatchedIntent.Action.RequestedActionId),
                                "An original press dispatched more than once.");
                            False(engine.Pending.HasValue);
                            Equal(
                                BufferDecisionKind.None,
                                engine.Evaluate(new BufferContext(safety, true), now).Kind);
                        }

                        break;
                    }
            }
        }

        // Expiry has priority over executability at the exact deadline.
        var finalIntent = new BufferIntent(
            BaseAction,
            ActionFailureKind.GlobalCooldown,
            IsEligibleForBuffering: true);
        True(engine.Arm(finalIntent, now, BufferEngine.AbsoluteHoldCapMilliseconds));
        Equal(
            BufferDecisionKind.Expired,
            engine.Evaluate(
                new BufferContext(BufferSafetyState.SafeFor(BaseAction), ActionIsExecutable: true),
                now + BufferEngine.AbsoluteHoldCapMilliseconds).Kind);
        False(engine.Pending.HasValue);
    }

    private static OneShotActionBuffer ArmedBuffer(
        ActionFailureKind failure = ActionFailureKind.GlobalCooldown,
        TimeSpan? eligibleAt = null)
    {
        var buffer = new OneShotActionBuffer();
        var token = Begin(buffer);
        Equal(ArmResult.Armed, Reject(buffer, token, failure, eligibleAt ?? T0 + Ms(50)));
        return buffer;
    }

    private static AttemptToken Begin(OneShotActionBuffer buffer, TimeSpan? at = null) =>
        buffer.BeginOriginalAttempt(BaseAction, at ?? T0, BufferSafetyState.SafeFor(BaseAction));

    private static ArmResult Reject(
        OneShotActionBuffer buffer,
        AttemptToken token,
        ActionFailureKind failure,
        TimeSpan eligibleAt,
        bool eligible = true,
        TimeSpan? observedAt = null) =>
        buffer.ReportOriginalRejection(
            token,
            failure,
            eligible,
            eligibleAt,
            observedAt ?? T0 + Ms(10),
            BufferSafetyState.SafeFor(BaseAction));

    private static bool Take(
        OneShotActionBuffer buffer,
        TimeSpan at,
        BufferSafetyState? safety = null,
        bool executable = true) =>
        buffer.TryTakeDispatch(
            at,
            safety ?? BufferSafetyState.SafeFor(BaseAction),
            executable,
            out _);

    private static AdaptiveRttEstimator SeedStableEstimator(int milliseconds)
    {
        var estimator = new AdaptiveRttEstimator();
        for (var index = 0; index < 8; index++)
        {
            Equal(RttSampleResult.Accepted, estimator.AddSample(Ms(milliseconds)));
        }

        return estimator;
    }

    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    private static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true, got false.");
        }
    }

    private static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false, got true.");

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
        }
    }

    private static void InRange<T>(T actual, T minimum, T maximum)
        where T : IComparable<T>
    {
        if (actual.CompareTo(minimum) < 0 || actual.CompareTo(maximum) > 0)
        {
            throw new InvalidOperationException($"Expected {actual} in [{minimum}, {maximum}].");
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
