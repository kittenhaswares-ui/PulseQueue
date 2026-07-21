namespace PulseQueue.Core;

public readonly record struct AttemptToken(long Value)
{
    public bool IsValid => Value > 0;
}

public readonly record struct ActionRequest(
    uint RequestedActionId,
    uint ResolvedActionId,
    ulong TargetId,
    uint TerritoryId,
    ulong InstanceId);

public readonly record struct BufferSafetyState(
    bool Enabled,
    bool ConflictDetected,
    bool LoggedIn,
    bool IsAlive,
    bool IsStunned,
    bool IsKnockbackActive,
    uint TerritoryId,
    ulong InstanceId,
    ulong TargetId,
    uint ResolvedActionId)
{
    public static BufferSafetyState SafeFor(ActionRequest request) => new(
        Enabled: true,
        ConflictDetected: false,
        LoggedIn: true,
        IsAlive: true,
        IsStunned: false,
        IsKnockbackActive: false,
        request.TerritoryId,
        request.InstanceId,
        request.TargetId,
        request.ResolvedActionId);
}

public enum ActionFailureKind
{
    Unknown = 0,
    GlobalCooldown,
    AnimationLock,
    Cooldown,
    InvalidTarget,
    OutOfRange,
    InsufficientResource,
    NotLearned,
    ServerRejected,
}

public enum BufferLifecycleState
{
    Idle = 0,
    AwaitingOriginalOutcome,
    Buffered,
}

public enum BufferClearReason
{
    None = 0,
    ReplacedByOriginalAttempt,
    ExplicitCancellation,
    Accepted,
    Dispatched,
    Expired,
    Disabled,
    ConflictDetected,
    LoggedOut,
    Dead,
    Stunned,
    Knockback,
    TerritoryChanged,
    InstanceChanged,
    TargetChanged,
    ResolvedActionChanged,
    IneligibleAction,
    NonTransientFailure,
    ServerRejected,
    BeyondAbsoluteHoldCap,
}

public enum ArmResult
{
    Armed = 0,
    IgnoredStaleAttempt,
    RejectedUnsafe,
    RejectedIneligibleAction,
    RejectedNonTransientFailure,
    RejectedServerFailure,
    RejectedExpired,
    RejectedBeyondAbsoluteHoldCap,
}

public readonly record struct BufferDispatch(
    AttemptToken Attempt,
    ActionRequest Action,
    TimeSpan OriginalAttemptedAt,
    TimeSpan DispatchedAt);
