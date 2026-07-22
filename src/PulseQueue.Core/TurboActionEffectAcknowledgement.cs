namespace PulseQueue.Core;

/// <summary>
/// Selects how a Turbo pulse is correlated with the source sequence in a local
/// action-effect packet.
/// </summary>
public enum TurboAcknowledgementSequenceMode
{
    Invalid = 0,
    ImmediateExact,
    QueuedAfterBaseline,
}

/// <summary>
/// The immutable identity expected from the action effect of one Turbo pulse.
/// SequenceMarker is the exact sequence for an immediate send and the sequence
/// observed before queueing for a queued send.
/// </summary>
public readonly record struct TurboActionEffectExpectation(
    uint ActionType,
    uint RequestedActionId,
    uint ResolvedActionId,
    TurboAcknowledgementSequenceMode SequenceMode,
    ushort SequenceMarker)
{
    public bool IsValid =>
        ActionType != 0
        && RequestedActionId != 0
        && ResolvedActionId != 0
        && SequenceMarker != 0
        && SequenceMode is TurboAcknowledgementSequenceMode.ImmediateExact
            or TurboAcknowledgementSequenceMode.QueuedAfterBaseline;
}

/// <summary>
/// The correlation fields read from one local-player action-effect packet.
/// </summary>
public readonly record struct TurboActionEffectObservation(
    uint ActionType,
    uint ActionId,
    ushort SourceSequence)
{
    public bool IsValid => ActionType != 0 && ActionId != 0 && SourceSequence != 0;
}

/// <summary>
/// Deterministically proves whether an action effect acknowledges one Turbo
/// pulse. No field is inferred or substituted when identity is incomplete.
/// </summary>
public static class TurboActionEffectAcknowledgementMatcher
{
    public static bool Matches(
        TurboActionEffectExpectation? expectation,
        TurboActionEffectObservation? observation)
    {
        if (!expectation.HasValue || !observation.HasValue)
        {
            return false;
        }

        var expected = expectation.GetValueOrDefault();
        var observed = observation.GetValueOrDefault();
        if (!expected.IsValid
            || !observed.IsValid
            || observed.ActionType != expected.ActionType
            || (observed.ActionId != expected.RequestedActionId
                && observed.ActionId != expected.ResolvedActionId))
        {
            return false;
        }

        return expected.SequenceMode switch
        {
            TurboAcknowledgementSequenceMode.ImmediateExact =>
                observed.SourceSequence == expected.SequenceMarker,
            TurboAcknowledgementSequenceMode.QueuedAfterBaseline =>
                IsWrapSafeNewer(observed.SourceSequence, expected.SequenceMarker),
            _ => false,
        };
    }

    /// <summary>
    /// Compares two non-zero 16-bit sequence values using serial-number
    /// arithmetic. An equal, older, or exactly half-range value is not newer.
    /// </summary>
    public static bool IsWrapSafeNewer(ushort candidate, ushort baseline)
    {
        if (candidate == 0 || baseline == 0)
        {
            return false;
        }

        var distance = (ushort)(candidate - baseline);
        return distance is > 0 and < 0x8000;
    }
}
