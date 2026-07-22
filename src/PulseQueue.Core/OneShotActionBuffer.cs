namespace PulseQueue.Core;

public sealed class OneShotActionBuffer
{
    public static readonly TimeSpan AbsoluteHoldCap = TimeSpan.FromMilliseconds(180);

    private readonly object gate = new();
    private long nextAttemptValue;
    private Candidate? candidate;
    private Pending? pending;

    public BufferLifecycleState State
    {
        get
        {
            lock (gate)
            {
                return GetState();
            }
        }
    }

    public BufferClearReason LastClearReason { get; private set; }

    public bool HasPending => State == BufferLifecycleState.Buffered;

    public AttemptToken BeginOriginalAttempt(
        ActionRequest action,
        TimeSpan attemptedAt,
        BufferSafetyState safety)
    {
        ValidateAction(action);

        lock (gate)
        {
            if (candidate is not null || pending is not null)
            {
                ClearInternal(BufferClearReason.ReplacedByOriginalAttempt);
            }

            var token = NextToken();
            var blockingReason = GetBlockingReason(safety) ?? GetSnapshotChangeReason(action, safety);
            if (blockingReason is not null)
            {
                LastClearReason = blockingReason.Value;
                return token;
            }

            candidate = new Candidate(token, action, attemptedAt);
            return token;
        }
    }

    public ArmResult ReportOriginalRejection(
        AttemptToken attempt,
        ActionFailureKind failure,
        bool actionIsEligibleForBuffering,
        TimeSpan eligibleAt,
        TimeSpan observedAt,
        BufferSafetyState safety)
    {
        lock (gate)
        {
            ObserveSafetyInternal(safety);

            if (candidate is not { } current || current.Attempt != attempt)
            {
                return ArmResult.IgnoredStaleAttempt;
            }

            if (failure == ActionFailureKind.ServerRejected)
            {
                ClearInternal(BufferClearReason.ServerRejected);
                return ArmResult.RejectedServerFailure;
            }

            if (!IsEligibleTransientFailure(failure))
            {
                ClearInternal(BufferClearReason.NonTransientFailure);
                return ArmResult.RejectedNonTransientFailure;
            }

            if (!actionIsEligibleForBuffering)
            {
                ClearInternal(BufferClearReason.IneligibleAction);
                return ArmResult.RejectedIneligibleAction;
            }

            var unsafeReason = GetBlockingReason(safety) ?? GetSnapshotChangeReason(current.Action, safety);
            if (unsafeReason is not null)
            {
                ClearInternal(unsafeReason.Value);
                return ArmResult.RejectedUnsafe;
            }

            var expiresAt = SaturatingAdd(current.AttemptedAt, AbsoluteHoldCap);
            if (observedAt >= expiresAt)
            {
                ClearInternal(BufferClearReason.Expired);
                return ArmResult.RejectedExpired;
            }

            var normalizedEligibleAt = eligibleAt < observedAt ? observedAt : eligibleAt;
            if (normalizedEligibleAt >= expiresAt)
            {
                ClearInternal(BufferClearReason.BeyondAbsoluteHoldCap);
                return ArmResult.RejectedBeyondAbsoluteHoldCap;
            }

            pending = new Pending(
                current.Attempt,
                current.Action,
                current.AttemptedAt,
                normalizedEligibleAt,
                expiresAt);
            candidate = null;
            return ArmResult.Armed;
        }
    }

    public bool TryTakeDispatch(
        TimeSpan now,
        BufferSafetyState safety,
        bool actionIsCurrentlyExecutable,
        out BufferDispatch dispatch)
    {
        lock (gate)
        {
            dispatch = default;
            ObserveSafetyInternal(safety);

            if (pending is not { } current)
            {
                return false;
            }

            if (now >= current.ExpiresAt)
            {
                ClearInternal(BufferClearReason.Expired);
                return false;
            }

            if (!actionIsCurrentlyExecutable || now < current.EligibleAt)
            {
                return false;
            }

            dispatch = new BufferDispatch(
                current.Attempt,
                current.Action,
                current.AttemptedAt,
                now);

            // Clear before returning so a caller-triggered callback cannot take it twice.
            candidate = null;
            pending = null;
            LastClearReason = BufferClearReason.Dispatched;
            return true;
        }
    }

    public void ObserveSafety(BufferSafetyState safety)
    {
        lock (gate)
        {
            ObserveSafetyInternal(safety);
        }
    }

    public bool ReportAccepted(AttemptToken attempt)
    {
        lock (gate)
        {
            if (!MatchesActiveAttempt(attempt))
            {
                return false;
            }

            ClearInternal(BufferClearReason.Accepted);
            return true;
        }
    }

    public bool ReportServerRejection(AttemptToken attempt)
    {
        lock (gate)
        {
            if (!MatchesActiveAttempt(attempt))
            {
                return false;
            }

            // A server rejection is terminal. It can clear, but can never arm or retry.
            ClearInternal(BufferClearReason.ServerRejected);
            return true;
        }
    }

    public bool Cancel()
    {
        lock (gate)
        {
            if (candidate is null && pending is null)
            {
                return false;
            }

            ClearInternal(BufferClearReason.ExplicitCancellation);
            return true;
        }
    }

    private static bool IsEligibleTransientFailure(ActionFailureKind failure) =>
        failure is ActionFailureKind.GlobalCooldown
            or ActionFailureKind.AnimationLock
            or ActionFailureKind.Cooldown;

    private void ObserveSafetyInternal(BufferSafetyState safety)
    {
        var action = pending?.Action ?? candidate?.Action;
        if (action is null)
        {
            return;
        }

        var reason = GetBlockingReason(safety) ?? GetSnapshotChangeReason(action.Value, safety);
        if (reason is not null)
        {
            ClearInternal(reason.Value);
        }
    }

    private static BufferClearReason? GetBlockingReason(BufferSafetyState safety)
    {
        if (!safety.Enabled)
        {
            return BufferClearReason.Disabled;
        }

        if (safety.ConflictDetected)
        {
            return BufferClearReason.ConflictDetected;
        }

        if (!safety.LoggedIn)
        {
            return BufferClearReason.LoggedOut;
        }

        if (!safety.IsAlive)
        {
            return BufferClearReason.Dead;
        }

        if (safety.IsMounted)
        {
            return BufferClearReason.Mounted;
        }

        if (safety.IsStunned)
        {
            return BufferClearReason.Stunned;
        }

        if (safety.IsKnockbackActive)
        {
            return BufferClearReason.Knockback;
        }

        return null;
    }

    private static BufferClearReason? GetSnapshotChangeReason(
        ActionRequest action,
        BufferSafetyState safety)
    {
        if (safety.TerritoryId != action.TerritoryId)
        {
            return BufferClearReason.TerritoryChanged;
        }

        if (safety.InstanceId != action.InstanceId)
        {
            return BufferClearReason.InstanceChanged;
        }

        if (safety.TargetId != action.TargetId)
        {
            return BufferClearReason.TargetChanged;
        }

        if (safety.ResolvedActionId != action.ResolvedActionId)
        {
            return BufferClearReason.ResolvedActionChanged;
        }

        return null;
    }

    private static void ValidateAction(ActionRequest action)
    {
        if (action.RequestedActionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Requested action ID must be non-zero.");
        }

        if (action.ResolvedActionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(action), "Resolved action ID must be non-zero.");
        }
    }

    private AttemptToken NextToken()
    {
        unchecked
        {
            nextAttemptValue++;
            if (nextAttemptValue <= 0)
            {
                nextAttemptValue = 1;
            }
        }

        return new AttemptToken(nextAttemptValue);
    }

    private bool MatchesActiveAttempt(AttemptToken attempt) =>
        candidate?.Attempt == attempt || pending?.Attempt == attempt;

    private BufferLifecycleState GetState()
    {
        if (pending is not null)
        {
            return BufferLifecycleState.Buffered;
        }

        return candidate is not null
            ? BufferLifecycleState.AwaitingOriginalOutcome
            : BufferLifecycleState.Idle;
    }

    private void ClearInternal(BufferClearReason reason)
    {
        candidate = null;
        pending = null;
        LastClearReason = reason;
    }

    private static TimeSpan SaturatingAdd(TimeSpan value, TimeSpan delta)
    {
        if (value.Ticks > TimeSpan.MaxValue.Ticks - delta.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return value + delta;
    }

    private sealed record Candidate(
        AttemptToken Attempt,
        ActionRequest Action,
        TimeSpan AttemptedAt);

    private sealed record Pending(
        AttemptToken Attempt,
        ActionRequest Action,
        TimeSpan AttemptedAt,
        TimeSpan EligibleAt,
        TimeSpan ExpiresAt);
}
