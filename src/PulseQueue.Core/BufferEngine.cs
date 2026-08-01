namespace PulseQueue.Core;

public readonly record struct BufferIntent(
    ActionRequest Action,
    ActionFailureKind OriginalFailure,
    bool IsEligibleForBuffering);

public readonly record struct BufferContext(
    BufferSafetyState Safety,
    bool ActionIsExecutable);

public enum CancelReason
{
    None = 0,
    Replaced,
    Explicit,
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
    Ineligible,
    NonTransientFailure,
    ServerRejected,
    Expired,
    Dispatched,
}

public enum BufferDecisionKind
{
    None = 0,
    Dispatch,
    Cancelled,
    Expired,
}

public readonly record struct BufferDecision(
    BufferDecisionKind Kind,
    BufferIntent? Intent,
    CancelReason Reason)
{
    public static BufferDecision None => new(BufferDecisionKind.None, null, CancelReason.None);
}

public sealed class BufferEngine
{
    public const int AbsoluteHoldCapMilliseconds = 350;

    private readonly object gate = new();
    private ArmedIntent? pending;

    public BufferIntent? Pending
    {
        get
        {
            lock (gate)
            {
                return pending?.Intent;
            }
        }
    }

    public CancelReason LastCancelReason { get; private set; }

    public bool Arm(BufferIntent intent, long originalAttemptAtMilliseconds, int holdMilliseconds)
    {
        ValidateIntent(intent);

        lock (gate)
        {
            if (pending is not null)
            {
                pending = null;
                LastCancelReason = CancelReason.Replaced;
            }

            if (!intent.IsEligibleForBuffering)
            {
                LastCancelReason = CancelReason.Ineligible;
                return false;
            }

            if (intent.OriginalFailure == ActionFailureKind.ServerRejected)
            {
                LastCancelReason = CancelReason.ServerRejected;
                return false;
            }

            if (intent.OriginalFailure is not ActionFailureKind.GlobalCooldown
                and not ActionFailureKind.AnimationLock
                and not ActionFailureKind.Cooldown)
            {
                LastCancelReason = CancelReason.NonTransientFailure;
                return false;
            }

            var clampedHold = Math.Clamp(holdMilliseconds, 0, AbsoluteHoldCapMilliseconds);
            if (clampedHold == 0)
            {
                LastCancelReason = CancelReason.Expired;
                return false;
            }

            pending = new ArmedIntent(
                intent,
                SaturatingAdd(originalAttemptAtMilliseconds, clampedHold));
            return true;
        }
    }

    public void Cancel(CancelReason reason)
    {
        if (reason == CancelReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        lock (gate)
        {
            pending = null;
            LastCancelReason = reason;
        }
    }

    public BufferDecision Evaluate(BufferContext context, long nowMilliseconds)
    {
        lock (gate)
        {
            if (pending is not { } current)
            {
                return BufferDecision.None;
            }

            var cancellation = GetCancellationReason(current.Intent.Action, context.Safety);
            if (cancellation != CancelReason.None)
            {
                pending = null;
                LastCancelReason = cancellation;
                return new BufferDecision(BufferDecisionKind.Cancelled, null, cancellation);
            }

            if (nowMilliseconds >= current.ExpiresAtMilliseconds)
            {
                pending = null;
                LastCancelReason = CancelReason.Expired;
                return new BufferDecision(BufferDecisionKind.Expired, null, CancelReason.Expired);
            }

            if (!context.ActionIsExecutable)
            {
                return BufferDecision.None;
            }

            // Copy, then clear before returning. Re-entrant callers cannot dispatch twice.
            var intent = current.Intent;
            pending = null;
            LastCancelReason = CancelReason.Dispatched;
            return new BufferDecision(BufferDecisionKind.Dispatch, intent, CancelReason.Dispatched);
        }
    }

    private static CancelReason GetCancellationReason(
        ActionRequest action,
        BufferSafetyState safety)
    {
        if (!safety.Enabled)
        {
            return CancelReason.Disabled;
        }

        if (safety.ConflictDetected)
        {
            return CancelReason.Conflict;
        }

        if (!safety.LoggedIn)
        {
            return CancelReason.Logout;
        }

        if (!safety.IsAlive)
        {
            return CancelReason.Death;
        }

        if (safety.IsMounted)
        {
            return CancelReason.Mounted;
        }

        if (safety.IsStunned)
        {
            return CancelReason.Stun;
        }

        if (safety.IsKnockbackActive)
        {
            return CancelReason.Knockback;
        }

        if (safety.TerritoryId != action.TerritoryId)
        {
            return CancelReason.TerritoryChange;
        }

        if (safety.InstanceId != action.InstanceId)
        {
            return CancelReason.InstanceChange;
        }

        if (safety.TargetId != action.TargetId)
        {
            return CancelReason.TargetChange;
        }

        if (safety.ResolvedActionId != action.ResolvedActionId)
        {
            return CancelReason.ResolvedActionChange;
        }

        return CancelReason.None;
    }

    private static void ValidateIntent(BufferIntent intent)
    {
        if (intent.Action.RequestedActionId == 0 || intent.Action.ResolvedActionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }
    }

    private static long SaturatingAdd(long value, int delta)
    {
        if (value > long.MaxValue - delta)
        {
            return long.MaxValue;
        }

        return value + delta;
    }

    private sealed record ArmedIntent(BufferIntent Intent, long ExpiresAtMilliseconds);
}
