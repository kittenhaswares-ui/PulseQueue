using PulseQueue.Core;

internal static class RepeatNativeQueueOwnershipTests
{
    private static readonly ExactActionTuple RepeatedAction = new(
        ActionType: 1,
        RequestedActionId: 100,
        ResolvedActionId: 101,
        TargetId: 200,
        Param: 3,
        Mode: 0,
        RouteId: 4);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("repeat queue ownership claims only an exact new queue delta", ClaimsOnlyExactNewQueueDelta);
        yield return ("newer physical generation takes an exact repeat-owned queue once", NewerGenerationTakesExactQueueOnce);
        yield return ("same or older generation cannot take a repeat-owned queue", SameOrOlderGenerationCannotTakeQueue);
        yield return ("sequence mismatch cannot clear a repeat-owned queue", SequenceMismatchCannotClearQueue);
        yield return ("changed native snapshot cannot clear a repeat-owned queue", ChangedSnapshotCannotClearQueue);
        yield return ("repeat queue ownership is separate from smart queue ownership", RepeatOwnershipIsSeparate);
        yield return ("repeat queue ownership exposes no replay or drain behavior", ExposesNoReplayBehavior);
    }

    private static void ClaimsOnlyExactNewQueueDelta()
    {
        const long generation = 10;
        const uint sequence = 20;
        var queue = SnapshotFor(RepeatedAction.ResolvedActionId);

        var exact = new RepeatNativeQueueOwnership();
        True(exact.TryClaimFromObservedDelta(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            queue,
            RepeatedAction));
        True(exact.HasOwnership);

        var preexisting = new RepeatNativeQueueOwnership();
        False(preexisting.TryClaimFromObservedDelta(
            generation,
            sequence,
            queue,
            queue,
            RepeatedAction));
        False(preexisting.HasOwnership);

        var foreign = new RepeatNativeQueueOwnership();
        False(foreign.TryClaimFromObservedDelta(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            SnapshotFor(actionId: 999),
            RepeatedAction));
        False(foreign.HasOwnership);
    }

    private static void NewerGenerationTakesExactQueueOnce()
    {
        const long repeatGeneration = 30;
        const uint sequence = 40;
        var queue = SnapshotFor(RepeatedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(repeatGeneration, sequence, queue);

        True(ownership.TryTakeForNewerInput(
            repeatGeneration + 1,
            sequence,
            queue,
            out var replaceable));
        Equal(queue, replaceable);
        False(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(
            repeatGeneration + 2,
            sequence,
            queue,
            out _));
    }

    private static void SameOrOlderGenerationCannotTakeQueue()
    {
        const long repeatGeneration = 50;
        const uint sequence = 60;
        var queue = SnapshotFor(RepeatedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(repeatGeneration, sequence, queue);

        False(ownership.TryTakeForNewerInput(
            repeatGeneration - 1,
            sequence,
            queue,
            out _));
        True(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(
            repeatGeneration,
            sequence,
            queue,
            out _));
        True(ownership.HasOwnership);
        True(ownership.TryTakeForNewerInput(
            repeatGeneration + 1,
            sequence,
            queue,
            out _));
    }

    private static void SequenceMismatchCannotClearQueue()
    {
        const long repeatGeneration = 70;
        const uint sequence = 80;
        var queue = SnapshotFor(RepeatedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(repeatGeneration, sequence, queue);

        False(ownership.TryTakeForNewerInput(
            repeatGeneration + 1,
            sequence + 1,
            queue,
            out _));
        False(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(
            repeatGeneration + 2,
            sequence,
            queue,
            out _));
    }

    private static void ChangedSnapshotCannotClearQueue()
    {
        const long repeatGeneration = 90;
        const uint sequence = 100;
        var queue = SnapshotFor(RepeatedAction.ResolvedActionId);
        var ownership = ClaimedOwnership(repeatGeneration, sequence, queue);
        var changed = queue with { TargetId = queue.TargetId + 1 };

        False(ownership.TryTakeForNewerInput(
            repeatGeneration + 1,
            sequence,
            changed,
            out _));
        False(ownership.HasOwnership);
        False(ownership.TryTakeForNewerInput(
            repeatGeneration + 2,
            sequence,
            queue,
            out _));
    }

    private static void RepeatOwnershipIsSeparate()
    {
        const long generation = 110;
        const uint sequence = 120;
        var repeatQueue = SnapshotFor(RepeatedAction.ResolvedActionId);
        var smartAction = RepeatedAction with
        {
            RequestedActionId = 300,
            ResolvedActionId = 301,
            TargetId = 400,
        };
        var smartQueue = SnapshotFor(smartAction.ResolvedActionId, smartAction);
        var repeat = ClaimedOwnership(generation, sequence, repeatQueue);
        var smart = new NativeQueueOwnership();
        True(smart.TryClaimNewQueue(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            smartQueue,
            smartAction));

        True(repeat.TryTakeForNewerInput(generation + 1, sequence, repeatQueue, out _));
        False(repeat.HasOwnership);
        True(smart.HasOwnership);
        True(smart.TryTakeForNewerInput(generation + 1, sequence, smartQueue, out var untouched));
        Equal(smartQueue, untouched);
    }

    private static void ExposesNoReplayBehavior()
    {
        var publicMethods = typeof(RepeatNativeQueueOwnership)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        False(publicMethods.Any(name => name.Contains("Authorize", StringComparison.Ordinal)));
        False(publicMethods.Any(name => name.Contains("Drain", StringComparison.Ordinal)));
        False(publicMethods.Any(name => name.Contains("Dispatch", StringComparison.Ordinal)));
        False(publicMethods.Any(name => name.Contains("Replay", StringComparison.Ordinal)));
        False(publicMethods.Contains(nameof(NativeQueueOwnership.TryTakeExactCurrent), StringComparer.Ordinal));
    }

    private static RepeatNativeQueueOwnership ClaimedOwnership(
        long generation,
        uint sequence,
        NativeQueueSnapshot queue)
    {
        var ownership = new RepeatNativeQueueOwnership();
        True(ownership.TryClaimFromObservedDelta(
            generation,
            sequence,
            NativeQueueSnapshot.Empty,
            queue,
            RepeatedAction));
        return ownership;
    }

    private static NativeQueueSnapshot SnapshotFor(uint actionId, ExactActionTuple? action = null)
    {
        var value = action ?? RepeatedAction;
        return new NativeQueueSnapshot(
            IsQueued: true,
            ActionType: value.ActionType,
            ActionId: actionId,
            TargetId: value.TargetId,
            Param: value.Param,
            Mode: value.Mode,
            RouteId: value.RouteId);
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
}
