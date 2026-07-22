namespace PulseQueue.Core;

/// <summary>
/// Tracks a native queue entry proven to have been created by one logical
/// hotbar repeat. This deliberately exposes replacement only: repeat-created
/// queues may be removed by a newer physical input, but are never replayed or
/// drained by this component.
/// </summary>
public sealed class RepeatNativeQueueOwnership
{
    private readonly NativeQueueOwnership ownership = new();

    public bool HasOwnership => ownership.HasOwnership;

    /// <summary>
    /// Claims only a queue entry that appeared as an exact result of the repeat
    /// invocation. A matching entry that was already present before the call is
    /// not owned.
    /// </summary>
    public bool TryClaimFromObservedDelta(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot before,
        NativeQueueSnapshot after,
        ExactActionTuple attempted) =>
        ownership.TryClaimNewQueue(generation, sequenceMarker, before, after, attempted);

    /// <summary>
    /// Transfers the exact currently visible owned entry to a strictly newer
    /// physical input generation. The caller may then clear that one entry.
    /// </summary>
    public bool TryTakeForNewerInput(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        out NativeQueueSnapshot replaceable) =>
        ownership.TryTakeForNewerInput(generation, sequenceMarker, current, out replaceable);

    public void Reconcile(uint sequenceMarker, NativeQueueSnapshot current) =>
        ownership.Reconcile(sequenceMarker, current);

    public void Clear() => ownership.Clear();
}
