namespace PulseQueue.Core;

/// <summary>
/// Tracks only native queue state that was proven to have been created by a
/// certified hotbar generation. A later generation may consume that ownership
/// exactly once; foreign, changed, or already-sent queue state never matches.
/// </summary>
public sealed class NativeQueueOwnership
{
    private readonly object gate = new();
    private OwnedQueue? owned;

    public bool HasOwnership
    {
        get
        {
            lock (gate)
            {
                return owned is not null;
            }
        }
    }

    public bool TryClaimNewQueue(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot before,
        NativeQueueSnapshot after,
        ExactActionTuple attempted)
    {
        lock (gate)
        {
            if (generation <= 0
                || !after.Matches(attempted)
                || before.Matches(attempted))
            {
                return false;
            }

            owned = new OwnedQueue(generation, sequenceMarker, after, attempted);
            return true;
        }
    }

    public bool TryTakeForNewerInput(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        out NativeQueueSnapshot replaceable)
    {
        lock (gate)
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
    }

    /// <summary>
    /// Authorizes one native invocation as the drain of the exact queue entry
    /// previously claimed by this generation. A successful authorization consumes
    /// ownership, so the same queue can never authorize a second invocation.
    /// Stale callers and foreign action tuples leave valid ownership intact; a
    /// changed queue snapshot or sequence marker invalidates it immediately.
    /// </summary>
    public bool TryAuthorizeExactDrain(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        ExactActionTuple attempted)
    {
        lock (gate)
        {
            if (owned is not { } value)
            {
                return false;
            }

            if (!current.Equals(value.Snapshot) || sequenceMarker != value.SequenceMarker)
            {
                owned = null;
                return false;
            }

            if (generation != value.Generation
                || attempted != value.Attempted
                || !current.Matches(attempted))
            {
                return false;
            }

            owned = null;
            return true;
        }
    }

    public void Reconcile(uint sequenceMarker, NativeQueueSnapshot current)
    {
        lock (gate)
        {
            if (owned is { } value
                && (!current.Equals(value.Snapshot) || sequenceMarker != value.SequenceMarker))
            {
                owned = null;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            owned = null;
        }
    }

    private sealed record OwnedQueue(
        long Generation,
        uint SequenceMarker,
        NativeQueueSnapshot Snapshot,
        ExactActionTuple Attempted);
}
