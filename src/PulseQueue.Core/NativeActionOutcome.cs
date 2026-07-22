namespace PulseQueue.Core;

/// <summary>
/// The complete immutable identity of one player action invocation.
/// </summary>
public readonly record struct ExactActionTuple(
    uint ActionType,
    uint RequestedActionId,
    uint ResolvedActionId,
    ulong TargetId,
    uint Param,
    uint Mode,
    uint RouteId)
{
    public bool IsValid =>
        ActionType != 0
        && RequestedActionId != 0
        && ResolvedActionId != 0;
}

/// <summary>
/// A point-in-time copy of the native action queue fields.
/// </summary>
public readonly record struct NativeQueueSnapshot(
    bool IsQueued,
    uint ActionType,
    uint ActionId,
    ulong TargetId,
    uint Param,
    uint Mode,
    uint RouteId)
{
    public static NativeQueueSnapshot Empty => default;

    public bool Matches(ExactActionTuple attempted) =>
        IsQueued
        && attempted.IsValid
        && ActionType == attempted.ActionType
        && (ActionId == attempted.RequestedActionId || ActionId == attempted.ResolvedActionId)
        && TargetId == attempted.TargetId
        && Param == attempted.Param
        && Mode == attempted.Mode
        && RouteId == attempted.RouteId;
}

public enum NativeActionOutcome
{
    ImmediateAcceptance = 0,
    MatchingNewQueue,
    ForeignOrPreexistingQueue,
    Rejected,
}

public static class NativeActionOutcomeClassifier
{
    /// <summary>
    /// Classifies the synchronous result of one native action call. Queue identity is
    /// deliberately stricter than action ID alone, and an indistinguishable queue that
    /// already existed before the call is never credited to the new input.
    /// </summary>
    public static NativeActionOutcome Classify(
        bool originalReturned,
        NativeQueueSnapshot before,
        NativeQueueSnapshot after,
        ExactActionTuple attempted)
    {
        if (after.Matches(attempted) && !before.Matches(attempted))
        {
            return NativeActionOutcome.MatchingNewQueue;
        }

        if (before.IsQueued || after.IsQueued)
        {
            return NativeActionOutcome.ForeignOrPreexistingQueue;
        }

        return originalReturned
            ? NativeActionOutcome.ImmediateAcceptance
            : NativeActionOutcome.Rejected;
    }
}
