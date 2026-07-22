namespace PulseQueue.Core;

/// <summary>
/// Tracks only native queue state that was proven to have been created by a
/// certified hotbar generation. A later generation may consume that ownership
/// exactly once; foreign, changed, or already-sent queue state never matches.
/// </summary>
public sealed class NativeQueueOwnership
{
    private OwnedQueue? owned;

    public bool HasOwnership => owned is not null;

    public bool TryClaimNewQueue(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot before,
        NativeQueueSnapshot after,
        ExactActionTuple attempted)
    {
        if (generation <= 0
            || !after.Matches(attempted)
            || before.Matches(attempted))
        {
            return false;
        }

        owned = new OwnedQueue(generation, sequenceMarker, after);
        return true;
    }

    public bool TryTakeForNewerInput(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        out NativeQueueSnapshot replaceable)
    {
        replaceable = default;
        if (owned is not { } value)
        {
            return false;
        }

        // An outer compatibility hook may hide a queue temporarily and restore
        // it after an inner action call. Reconcile() clears a stably empty queue
        // on the framework boundary; replacement preserves ownership meanwhile.
        if (!current.IsQueued)
        {
            return false;
        }

        if (!current.Equals(value.Snapshot) || sequenceMarker != value.SequenceMarker)
        {
            owned = null;
            return false;
        }

        if (generation <= value.Generation)
        {
            return false;
        }

        replaceable = value.Snapshot;
        owned = null;
        return true;
    }

    public void Reconcile(uint sequenceMarker, NativeQueueSnapshot current)
    {
        if (owned is { } value
            && (!current.Equals(value.Snapshot) || sequenceMarker != value.SequenceMarker))
        {
            owned = null;
        }
    }

    public void Clear() => owned = null;

    private sealed record OwnedQueue(
        long Generation,
        uint SequenceMarker,
        NativeQueueSnapshot Snapshot);
}
