using PulseQueue.Core;

internal static class MacroTurboTranscriptTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("macro execution budget requires a positive limit", PositiveLimitIsRequired);
        yield return ("macro execution budget accepts zero action calls", ZeroActionCallsComplete);
        yield return ("macro execution budget accepts one action call", OneActionCallCompletes);
        yield return ("macro execution budget accepts its maximum action count", MaximumActionCallsComplete);
        yield return ("macro execution budget accepts one native outcome", OneAcceptedOutcomeCompletes);
        yield return ("macro execution budget rejects action N plus one", ActionBeyondMaximumFails);
        yield return ("macro execution budget blocks action after accepted outcome", ActionAfterAcceptedOutcomeIsBlocked);
        yield return ("macro execution budget preserves duplicate call count", DuplicateCallsAreCounted);
        yield return ("macro execution budget rejects outcome without action", OutcomeWithoutActionFails);
        yield return ("macro execution budget rejects a second accepted outcome", SecondAcceptedOutcomeFails);
        yield return ("macro execution budgets are independent", BudgetsAreIndependent);
        yield return ("completed macro execution budget stays closed", CompletedBudgetCannotRevive);
        yield return ("failed macro execution budget stays terminal", TerminalFailureCannotRecover);
    }

    private static void PositiveLimitIsRequired()
    {
        Throws<ArgumentOutOfRangeException>(() => new MacroTurboExecutionBudget(0));
        Throws<ArgumentOutOfRangeException>(() => new MacroTurboExecutionBudget(-1));
    }

    private static void ZeroActionCallsComplete()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 3);

        Equal(3, budget.MaxActionCalls);
        Equal(0, budget.ObservedActionCalls);
        Equal(0, budget.AcceptedOutcomeCount);
        False(budget.IsTerminal);
        Null(budget.TerminalResult);
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.TerminalResult!.Value);
        True(budget.IsTerminal);
    }

    private static void OneActionCallCompletes()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 3);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(1, budget.ObservedActionCalls);
        Equal(0, budget.AcceptedOutcomeCount);
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
    }

    private static void MaximumActionCallsComplete()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 3);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(3, budget.ObservedActionCalls);
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
    }

    private static void OneAcceptedOutcomeCompletes()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 2);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboAcceptedOutcomeMarkResult.Marked, budget.MarkAcceptedOutcome());
        Equal(1, budget.AcceptedOutcomeCount);
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
    }

    private static void ActionBeyondMaximumFails()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 2);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.ActionLimitExceeded, budget.ObserveAction());
        Equal(2, budget.ObservedActionCalls);
        Equal(MacroTurboExecutionBudgetResult.ActionLimitExceeded, budget.TerminalResult!.Value);
        Equal(MacroTurboExecutionBudgetResult.ActionLimitExceeded, budget.Finish());
    }

    private static void ActionAfterAcceptedOutcomeIsBlocked()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 3);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboAcceptedOutcomeMarkResult.Marked, budget.MarkAcceptedOutcome());

        // ObserveAction is called before the native original. Keeping the count
        // at one proves that the second original must remain blocked.
        Equal(
            MacroTurboActionObservationResult.AcceptedOutcomeAlreadyMarked,
            budget.ObserveAction());
        Equal(1, budget.ObservedActionCalls);
        Equal(1, budget.AcceptedOutcomeCount);
        Equal(
            MacroTurboExecutionBudgetResult.ActionAfterAcceptedOutcome,
            budget.TerminalResult!.Value);
    }

    private static void DuplicateCallsAreCounted()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 4);

        // There is intentionally no action-identity deduplication: repeated
        // identical macro lines each consume one budget entry.
        for (var index = 0; index < 4; index++)
        {
            Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        }

        Equal(4, budget.ObservedActionCalls);
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
    }

    private static void OutcomeWithoutActionFails()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 1);

        Equal(MacroTurboAcceptedOutcomeMarkResult.NoObservedAction, budget.MarkAcceptedOutcome());
        Equal(0, budget.AcceptedOutcomeCount);
        Equal(
            MacroTurboExecutionBudgetResult.AcceptedOutcomeWithoutAction,
            budget.TerminalResult!.Value);
    }

    private static void SecondAcceptedOutcomeFails()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 2);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboAcceptedOutcomeMarkResult.Marked, budget.MarkAcceptedOutcome());
        Equal(MacroTurboAcceptedOutcomeMarkResult.AlreadyMarked, budget.MarkAcceptedOutcome());
        Equal(1, budget.AcceptedOutcomeCount);
        Equal(
            MacroTurboExecutionBudgetResult.MultipleAcceptedOutcomes,
            budget.TerminalResult!.Value);
    }

    private static void BudgetsAreIndependent()
    {
        var first = new MacroTurboExecutionBudget(maxActionCalls: 1);
        var second = new MacroTurboExecutionBudget(maxActionCalls: 2);

        Equal(MacroTurboActionObservationResult.Allowed, first.ObserveAction());
        Equal(MacroTurboActionObservationResult.ActionLimitExceeded, first.ObserveAction());

        Equal(MacroTurboActionObservationResult.Allowed, second.ObserveAction());
        Equal(MacroTurboActionObservationResult.Allowed, second.ObserveAction());
        Equal(MacroTurboExecutionBudgetResult.Complete, second.Finish());

        Equal(MacroTurboExecutionBudgetResult.ActionLimitExceeded, first.Finish());
        Equal(2, second.ObservedActionCalls);
    }

    private static void CompletedBudgetCannotRevive()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 1);

        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
        Equal(MacroTurboActionObservationResult.Closed, budget.ObserveAction());
        Equal(MacroTurboAcceptedOutcomeMarkResult.Closed, budget.MarkAcceptedOutcome());
        Equal(MacroTurboExecutionBudgetResult.Complete, budget.Finish());
        Equal(0, budget.ObservedActionCalls);
        Equal(0, budget.AcceptedOutcomeCount);
    }

    private static void TerminalFailureCannotRecover()
    {
        var budget = new MacroTurboExecutionBudget(maxActionCalls: 1);

        Equal(MacroTurboActionObservationResult.Allowed, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.ActionLimitExceeded, budget.ObserveAction());
        Equal(MacroTurboActionObservationResult.Closed, budget.ObserveAction());
        Equal(MacroTurboAcceptedOutcomeMarkResult.Closed, budget.MarkAcceptedOutcome());
        Equal(MacroTurboExecutionBudgetResult.ActionLimitExceeded, budget.Finish());
        Equal(MacroTurboExecutionBudgetResult.ActionLimitExceeded, budget.TerminalResult!.Value);
        Equal(1, budget.ObservedActionCalls);
        Equal(0, budget.AcceptedOutcomeCount);
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void Null<T>(T? value)
        where T : struct
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
