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
    private ActiveDrainLease? activeDrainLease;
    private ulong nextDrainLeaseId;

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

            // A newly proven queue supersedes any in-flight proof for the old
            // entry. Its stale completion must not be able to mutate this owner.
            activeDrainLease = null;
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

            if (activeDrainLease is not null)
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
    /// Consumes ownership of the exact currently visible native queue entry once,
    /// when it belongs to or predates the supplied terminal generation cutoff.
    /// A temporarily hidden queue preserves ownership so an outer compatibility
    /// hook may restore it. Any visible queue whose snapshot or sequence differs
    /// invalidates ownership; an exact newer-generation queue remains owned.
    /// </summary>
    public bool TryTakeExactCurrent(
        long maximumGeneration,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        out NativeQueueSnapshot replaceable)
    {
        lock (gate)
        {
            replaceable = default;
            if (maximumGeneration <= 0 || owned is not { } value)
            {
                return false;
            }

            if (activeDrainLease is not null)
            {
                return false;
            }

            // Compatibility hooks may temporarily clear ActionQueued around an
            // inner call, then restore the exact entry after returning.
            if (!current.IsQueued)
            {
                return false;
            }

            if (!current.Equals(value.Snapshot) || sequenceMarker != value.SequenceMarker)
            {
                owned = null;
                return false;
            }

            if (value.Generation > maximumGeneration)
            {
                return false;
            }

            replaceable = value.Snapshot;
            owned = null;
            return true;
        }
    }

    /// <summary>
    /// Leases the exact currently visible owned queue entry for one native drain
    /// invocation without consuming ownership before that invocation returns.
    /// This lets an outer compatibility hook temporarily hide the entry and
    /// restore it when its inner invocation rejects. Only one lease may be active;
    /// duplicate or re-entrant drain attempts fail without changing ownership.
    /// </summary>
    public bool TryBeginExactDrain(
        long generation,
        uint sequenceMarker,
        NativeQueueSnapshot current,
        ExactActionTuple attempted,
        out NativeQueueDrainLease lease)
    {
        lock (gate)
        {
            lease = default;
            if (activeDrainLease is not null || owned is not { } value)
            {
                return false;
            }

            // An outer compatibility hook may already have hidden the exact
            // entry before this inner detour runs. Decline without destroying
            // the proof; the same hook may restore it after returning.
            if (!current.IsQueued)
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

            nextDrainLeaseId++;
            if (nextDrainLeaseId == 0)
            {
                nextDrainLeaseId++;
            }

            lease = new NativeQueueDrainLease(nextDrainLeaseId);
            activeDrainLease = new ActiveDrainLease(nextDrainLeaseId, value);
            return true;
        }
    }

    /// <summary>
    /// Confirms that a temporarily hidden drain invocation still names the one
    /// exact owner and that no other drain lease is in flight. Callers may use
    /// this only when native queue visibility is absent; it does not authorize a
    /// mutation or consume ownership.
    /// </summary>
    public bool CanDeferExactHiddenDrain(
        long generation,
        ExactActionTuple attempted)
    {
        lock (gate)
        {
            return activeDrainLease is null
                && owned is { } value
                && generation == value.Generation
                && attempted == value.Attempted;
        }
    }

    /// <summary>
    /// Finalizes a previously leased exact drain using the stable native queue
    /// state observed after the invocation returns. The exact same snapshot and
    /// sequence retain ownership (the rejected entry was restored); an empty
    /// queue consumes it; and any visible or sequence identity change invalidates
    /// it. A stale, duplicate, revoked, or forged lease changes nothing.
    /// </summary>
    public NativeQueueDrainFinalizeResult CompleteExactDrain(
        NativeQueueDrainLease lease,
        uint sequenceMarker,
        NativeQueueSnapshot current)
    {
        lock (gate)
        {
            if (!lease.IsValid
                || activeDrainLease is not { } active
                || active.LeaseId != lease.LeaseId)
            {
                return NativeQueueDrainFinalizeResult.InvalidLease;
            }

            activeDrainLease = null;
            if (owned is not { } value || !ReferenceEquals(value, active.Owner))
            {
                return NativeQueueDrainFinalizeResult.InvalidLease;
            }

            if (sequenceMarker == value.SequenceMarker && current.Equals(value.Snapshot))
            {
                return NativeQueueDrainFinalizeResult.OwnershipRetained;
            }

            owned = null;
            return current.IsQueued
                ? NativeQueueDrainFinalizeResult.OwnershipInvalidated
                : NativeQueueDrainFinalizeResult.OwnershipConsumed;
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
            if (activeDrainLease is not null || owned is not { } value)
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
                activeDrainLease = null;
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            owned = null;
            activeDrainLease = null;
        }
    }

    private sealed record OwnedQueue(
        long Generation,
        uint SequenceMarker,
        NativeQueueSnapshot Snapshot,
        ExactActionTuple Attempted);

    private sealed record ActiveDrainLease(ulong LeaseId, OwnedQueue Owner);
}

/// <summary>
/// Opaque proof that one exact owned native queue drain is in flight.
/// </summary>
public readonly struct NativeQueueDrainLease
{
    internal NativeQueueDrainLease(ulong leaseId) => LeaseId = leaseId;

    internal ulong LeaseId { get; }

    public bool IsValid => LeaseId != 0;
}

public enum NativeQueueDrainFinalizeResult
{
    InvalidLease = 0,
    OwnershipRetained = 1,
    OwnershipConsumed = 2,
    OwnershipInvalidated = 3,
}
