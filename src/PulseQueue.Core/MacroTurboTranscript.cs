namespace PulseQueue.Core;

/// <summary>
/// One ordered action line observed while executing an action-only macro. The
/// resolved ID is retained for diagnostics and runtime eligibility checks, but
/// it is deliberately not part of semantic transcript matching: combo or level
/// adjustment may produce a different resolved ID on a later execution.
/// </summary>
public readonly record struct MacroTurboTranscriptEntry(
    uint ActionType,
    uint RequestedActionId,
    uint ResolvedActionId,
    ulong TargetId,
    uint ExtraParam,
    uint RouteId,
    ulong ResolverFingerprint)
{
    public bool IsValid =>
        ActionType != 0
        && RequestedActionId != 0
        && ResolvedActionId != 0;

    public bool SemanticallyMatches(MacroTurboTranscriptEntry observed) =>
        IsValid
        && observed.IsValid
        && ActionType == observed.ActionType
        && RequestedActionId == observed.RequestedActionId
        && TargetId == observed.TargetId
        && ExtraParam == observed.ExtraParam
        && RouteId == observed.RouteId
        && ResolverFingerprint == observed.ResolverFingerprint;
}

public enum MacroTurboBuildStepResult
{
    Appended = 0,
    InvalidEntry,
    ExtraEntry,
    Closed,
    Faulted,
}

public enum MacroTurboFreezeResult
{
    Frozen = 0,
    Incomplete,
    InvalidEntry,
    ExtraEntry,
    AlreadyClosed,
}

/// <summary>
/// Builds the immutable baseline transcript for one macro. Freeze is a terminal
/// operation and succeeds only when the observed entry count exactly equals the
/// statically analyzed action-line count.
/// </summary>
public sealed class MacroTurboTranscriptBuilder
{
    private readonly int expectedActionCount;
    private readonly List<MacroTurboTranscriptEntry> entries;
    private MacroTurboFreezeResult? failure;
    private bool closed;

    public MacroTurboTranscriptBuilder(int expectedActionCount)
    {
        if (expectedActionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedActionCount),
                "A macro transcript must expect at least one action line.");
        }

        this.expectedActionCount = expectedActionCount;
        entries = new List<MacroTurboTranscriptEntry>(expectedActionCount);
    }

    public int ExpectedActionCount => expectedActionCount;

    public int ObservedActionCount => entries.Count;

    public MacroTurboBuildStepResult Append(MacroTurboTranscriptEntry entry)
    {
        if (closed) return MacroTurboBuildStepResult.Closed;
        if (failure is not null) return MacroTurboBuildStepResult.Faulted;
        if (!entry.IsValid)
        {
            failure = MacroTurboFreezeResult.InvalidEntry;
            return MacroTurboBuildStepResult.InvalidEntry;
        }

        if (entries.Count >= expectedActionCount)
        {
            failure = MacroTurboFreezeResult.ExtraEntry;
            return MacroTurboBuildStepResult.ExtraEntry;
        }

        entries.Add(entry);
        return MacroTurboBuildStepResult.Appended;
    }

    public MacroTurboFreezeResult Freeze(out MacroTurboTranscript? transcript)
    {
        transcript = null;
        if (closed) return MacroTurboFreezeResult.AlreadyClosed;
        closed = true;

        if (failure is { } failed) return failed;
        if (entries.Count != expectedActionCount)
        {
            return MacroTurboFreezeResult.Incomplete;
        }

        transcript = new MacroTurboTranscript(expectedActionCount, entries.ToArray());
        return MacroTurboFreezeResult.Frozen;
    }
}

/// <summary>
/// Immutable ordered action-line identity captured from one complete macro
/// execution. Duplicate entries are preserved.
/// </summary>
public sealed class MacroTurboTranscript
{
    private readonly MacroTurboTranscriptEntry[] entries;

    internal MacroTurboTranscript(
        int expectedActionCount,
        MacroTurboTranscriptEntry[] entries)
    {
        if (expectedActionCount <= 0 || entries.Length != expectedActionCount)
        {
            throw new ArgumentException("A frozen macro transcript must be complete.", nameof(entries));
        }

        ExpectedActionCount = expectedActionCount;
        this.entries = (MacroTurboTranscriptEntry[])entries.Clone();
    }

    public int ExpectedActionCount { get; }

    public int Count => entries.Length;

    public MacroTurboTranscriptEntry this[int index] => entries[index];

    public MacroTurboExecutionCursor StartExecution() => new(this);
}

public enum MacroTurboExecutionAcceptResult
{
    Accepted = 0,
    Mismatch,
    Extra,
    Closed,
}

public enum MacroTurboExecutionResult
{
    Complete = 0,
    Incomplete,
    Mismatch,
    Extra,
}

/// <summary>
/// Ordered, fail-closed validator for one later execution of a frozen macro.
/// Each observation may match only the next baseline entry.
/// </summary>
public sealed class MacroTurboExecutionCursor
{
    private readonly MacroTurboTranscript transcript;
    private MacroTurboExecutionResult? terminalResult;
    private int acceptedCount;

    internal MacroTurboExecutionCursor(MacroTurboTranscript transcript)
    {
        this.transcript = transcript;
    }

    public int AcceptedCount => acceptedCount;

    public int ExpectedActionCount => transcript.ExpectedActionCount;

    public bool IsTerminal => terminalResult is not null;

    public MacroTurboExecutionResult? TerminalResult => terminalResult;

    public MacroTurboExecutionAcceptResult Accept(MacroTurboTranscriptEntry observed)
    {
        if (terminalResult is not null) return MacroTurboExecutionAcceptResult.Closed;
        if (acceptedCount >= transcript.Count)
        {
            terminalResult = MacroTurboExecutionResult.Extra;
            return MacroTurboExecutionAcceptResult.Extra;
        }

        if (!transcript[acceptedCount].SemanticallyMatches(observed))
        {
            terminalResult = MacroTurboExecutionResult.Mismatch;
            return MacroTurboExecutionAcceptResult.Mismatch;
        }

        acceptedCount++;
        return MacroTurboExecutionAcceptResult.Accepted;
    }

    public MacroTurboExecutionResult Finish()
    {
        if (terminalResult is { } terminal) return terminal;

        terminalResult = acceptedCount == transcript.Count
            ? MacroTurboExecutionResult.Complete
            : MacroTurboExecutionResult.Incomplete;
        return terminalResult.Value;
    }
}
