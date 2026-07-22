using PulseQueue.Core;

internal static class MacroTurboTranscriptTests
{
    private static readonly MacroTurboTranscriptEntry First = new(
        ActionType: 1,
        RequestedActionId: 100,
        ResolvedActionId: 101,
        TargetId: 200,
        ExtraParam: 3,
        RouteId: 4,
        ResolverFingerprint: 500);

    private static readonly MacroTurboTranscriptEntry Second = new(
        ActionType: 14,
        RequestedActionId: 300,
        ResolvedActionId: 301,
        TargetId: 400,
        ExtraParam: 5,
        RouteId: 6,
        ResolverFingerprint: 700);

    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("macro transcript freezes exact expected count", ExactCountFreezes);
        yield return ("macro transcript rejects incomplete build", IncompleteBuildRejects);
        yield return ("macro transcript rejects extra build entry", ExtraBuildEntryRejects);
        yield return ("macro transcript rejects invalid build entry", InvalidBuildEntryRejects);
        yield return ("macro transcript preserves duplicate ordered entries", DuplicateEntriesArePreserved);
        yield return ("macro transcript cursor requires semantic order", CursorRequiresOrder);
        yield return ("macro transcript cursor detects every semantic mismatch", CursorDetectsSemanticMismatch);
        yield return ("macro transcript cursor detects incomplete execution", CursorDetectsIncompleteExecution);
        yield return ("macro transcript cursor detects extra execution entry", CursorDetectsExtraExecutionEntry);
        yield return ("macro transcript permits dynamic resolved action IDs", DynamicResolvedIdsArePermitted);
        yield return ("macro transcript cursors are independent", ExecutionCursorsAreIndependent);
        yield return ("macro transcript terminal failure cannot recover", TerminalFailureCannotRecover);
    }

    private static void ExactCountFreezes()
    {
        var builder = new MacroTurboTranscriptBuilder(expectedActionCount: 2);
        Equal(2, builder.ExpectedActionCount);
        Equal(MacroTurboBuildStepResult.Appended, builder.Append(First));
        Equal(MacroTurboBuildStepResult.Appended, builder.Append(Second));
        Equal(2, builder.ObservedActionCount);
        Equal(MacroTurboFreezeResult.Frozen, builder.Freeze(out var transcript));
        NotNull(transcript);
        Equal(2, transcript!.ExpectedActionCount);
        Equal(2, transcript.Count);
        Equal(First, transcript[0]);
        Equal(Second, transcript[1]);
        Equal(MacroTurboFreezeResult.AlreadyClosed, builder.Freeze(out _));
        Equal(MacroTurboBuildStepResult.Closed, builder.Append(First));
    }

    private static void IncompleteBuildRejects()
    {
        var builder = new MacroTurboTranscriptBuilder(expectedActionCount: 2);
        Equal(MacroTurboBuildStepResult.Appended, builder.Append(First));
        Equal(MacroTurboFreezeResult.Incomplete, builder.Freeze(out var transcript));
        Null(transcript);
        Equal(MacroTurboBuildStepResult.Closed, builder.Append(Second));
    }

    private static void ExtraBuildEntryRejects()
    {
        var builder = new MacroTurboTranscriptBuilder(expectedActionCount: 1);
        Equal(MacroTurboBuildStepResult.Appended, builder.Append(First));
        Equal(MacroTurboBuildStepResult.ExtraEntry, builder.Append(Second));
        Equal(MacroTurboFreezeResult.ExtraEntry, builder.Freeze(out var transcript));
        Null(transcript);
    }

    private static void InvalidBuildEntryRejects()
    {
        Throws<ArgumentOutOfRangeException>(() => new MacroTurboTranscriptBuilder(0));

        var builder = new MacroTurboTranscriptBuilder(expectedActionCount: 1);
        Equal(
            MacroTurboBuildStepResult.InvalidEntry,
            builder.Append(First with { RequestedActionId = 0 }));
        Equal(MacroTurboBuildStepResult.Faulted, builder.Append(First));
        Equal(MacroTurboFreezeResult.InvalidEntry, builder.Freeze(out var transcript));
        Null(transcript);
    }

    private static void DuplicateEntriesArePreserved()
    {
        var transcript = Freeze(First, First);
        Equal(First, transcript[0]);
        Equal(First, transcript[1]);

        var cursor = transcript.StartExecution();
        Equal(MacroTurboExecutionAcceptResult.Accepted, cursor.Accept(First));
        Equal(MacroTurboExecutionAcceptResult.Accepted, cursor.Accept(First));
        Equal(MacroTurboExecutionResult.Complete, cursor.Finish());
    }

    private static void CursorRequiresOrder()
    {
        var cursor = Freeze(First, Second).StartExecution();
        Equal(MacroTurboExecutionAcceptResult.Mismatch, cursor.Accept(Second));
        Equal(MacroTurboExecutionResult.Mismatch, cursor.Finish());
        Equal(0, cursor.AcceptedCount);
    }

    private static void CursorDetectsSemanticMismatch()
    {
        var mismatches = new[]
        {
            First with { ActionType = 2 },
            First with { RequestedActionId = 999 },
            First with { TargetId = 999 },
            First with { ExtraParam = 999 },
            First with { RouteId = 999 },
            First with { ResolverFingerprint = 999 },
        };

        foreach (var mismatch in mismatches)
        {
            var cursor = Freeze(First).StartExecution();
            Equal(MacroTurboExecutionAcceptResult.Mismatch, cursor.Accept(mismatch));
            Equal(MacroTurboExecutionResult.Mismatch, cursor.TerminalResult!.Value);
        }
    }

    private static void CursorDetectsIncompleteExecution()
    {
        var cursor = Freeze(First, Second).StartExecution();
        Equal(MacroTurboExecutionAcceptResult.Accepted, cursor.Accept(First));
        Equal(MacroTurboExecutionResult.Incomplete, cursor.Finish());
        Equal(MacroTurboExecutionResult.Incomplete, cursor.TerminalResult!.Value);
        True(cursor.IsTerminal);
    }

    private static void CursorDetectsExtraExecutionEntry()
    {
        var cursor = Freeze(First).StartExecution();
        Equal(MacroTurboExecutionAcceptResult.Accepted, cursor.Accept(First));
        Equal(MacroTurboExecutionAcceptResult.Extra, cursor.Accept(Second));
        Equal(MacroTurboExecutionResult.Extra, cursor.Finish());
    }

    private static void DynamicResolvedIdsArePermitted()
    {
        var transcript = Freeze(First);
        Equal(First.ResolvedActionId, transcript[0].ResolvedActionId);

        var cursor = transcript.StartExecution();
        Equal(
            MacroTurboExecutionAcceptResult.Accepted,
            cursor.Accept(First with { ResolvedActionId = 9_999 }));
        Equal(MacroTurboExecutionResult.Complete, cursor.Finish());
    }

    private static void ExecutionCursorsAreIndependent()
    {
        var transcript = Freeze(First, Second);
        var firstExecution = transcript.StartExecution();
        var secondExecution = transcript.StartExecution();

        Equal(MacroTurboExecutionAcceptResult.Accepted, firstExecution.Accept(First));
        Equal(MacroTurboExecutionResult.Incomplete, firstExecution.Finish());
        Equal(MacroTurboExecutionAcceptResult.Accepted, secondExecution.Accept(First));
        Equal(MacroTurboExecutionAcceptResult.Accepted, secondExecution.Accept(Second));
        Equal(MacroTurboExecutionResult.Complete, secondExecution.Finish());
    }

    private static void TerminalFailureCannotRecover()
    {
        var cursor = Freeze(First, Second).StartExecution();
        Equal(MacroTurboExecutionAcceptResult.Mismatch, cursor.Accept(Second));
        Equal(MacroTurboExecutionAcceptResult.Closed, cursor.Accept(First));
        Equal(MacroTurboExecutionResult.Mismatch, cursor.Finish());
    }

    private static MacroTurboTranscript Freeze(params MacroTurboTranscriptEntry[] entries)
    {
        var builder = new MacroTurboTranscriptBuilder(entries.Length);
        foreach (var entry in entries)
        {
            Equal(MacroTurboBuildStepResult.Appended, builder.Append(entry));
        }

        Equal(MacroTurboFreezeResult.Frozen, builder.Freeze(out var transcript));
        NotNull(transcript);
        return transcript!;
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void NotNull<T>(T? value)
        where T : class
    {
        if (value is null) throw new InvalidOperationException("Expected non-null value.");
    }

    private static void Null<T>(T? value)
        where T : class
    {
        if (value is not null) throw new InvalidOperationException("Expected null value.");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
