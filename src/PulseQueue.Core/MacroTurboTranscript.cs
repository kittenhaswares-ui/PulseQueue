namespace PulseQueue.Core;

/// <summary>
/// Result of reserving one already live-validated action call in a synthetic
/// same-slot macro execution.
/// </summary>
public enum MacroTurboActionObservationResult
{
    Allowed = 0,
    ActionLimitExceeded,
    AcceptedOutcomeAlreadyMarked,
    Closed,
}

/// <summary>
/// Result of reporting that the original native action call was accepted or
/// queued.
/// </summary>
public enum MacroTurboAcceptedOutcomeMarkResult
{
    Marked = 0,
    NoObservedAction,
    AlreadyMarked,
    Closed,
}

/// <summary>
/// Terminal result for one synthetic same-slot macro execution.
/// </summary>
public enum MacroTurboExecutionBudgetResult
{
    Complete = 0,
    ActionLimitExceeded,
    ActionAfterAcceptedOutcome,
    AcceptedOutcomeWithoutAction,
    MultipleAcceptedOutcomes,
}

/// <summary>
/// Fail-closed call budget for one synthetic execution of the same certified
/// macro slot. The caller must live-validate every macro action before calling
/// <see cref="ObserveAction"/>. An allowed observation reserves exactly one
/// call to the native original. If that original reports an accepted or queued
/// outcome, the caller must immediately call <see cref="MarkAcceptedOutcome"/>.
/// </summary>
/// <remarks>
/// This type deliberately records only bounded execution cardinality. It does
/// not capture or compare an action transcript, so duplicate macro action lines
/// are counted independently. Once an accepted outcome is marked, every later
/// action is blocked before the native original can run.
/// </remarks>
public sealed class MacroTurboExecutionBudget
{
    private readonly int maxActionCalls;
    private MacroTurboExecutionBudgetResult? terminalResult;
    private int observedActionCalls;
    private int acceptedOutcomeCount;

    public MacroTurboExecutionBudget(int maxActionCalls)
    {
        if (maxActionCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxActionCalls),
                "A macro execution budget must allow at least one action call.");
        }

        this.maxActionCalls = maxActionCalls;
    }

    public int MaxActionCalls => maxActionCalls;

    /// <summary>
    /// Number of action calls that were allowed to proceed to the native
    /// original. A blocked call never increments this value.
    /// </summary>
    public int ObservedActionCalls => observedActionCalls;

    public int AcceptedOutcomeCount => acceptedOutcomeCount;

    public bool IsTerminal => terminalResult is not null;

    public MacroTurboExecutionBudgetResult? TerminalResult => terminalResult;

    /// <summary>
    /// Reserves one call to the native original after the caller has completed
    /// all live action validation.
    /// </summary>
    public MacroTurboActionObservationResult ObserveAction()
    {
        if (terminalResult is not null)
        {
            return MacroTurboActionObservationResult.Closed;
        }

        if (acceptedOutcomeCount != 0)
        {
            terminalResult = MacroTurboExecutionBudgetResult.ActionAfterAcceptedOutcome;
            return MacroTurboActionObservationResult.AcceptedOutcomeAlreadyMarked;
        }

        if (observedActionCalls >= maxActionCalls)
        {
            terminalResult = MacroTurboExecutionBudgetResult.ActionLimitExceeded;
            return MacroTurboActionObservationResult.ActionLimitExceeded;
        }

        observedActionCalls++;
        return MacroTurboActionObservationResult.Allowed;
    }

    /// <summary>
    /// Marks the single accepted or queued outcome produced by an allowed
    /// native original action call.
    /// </summary>
    public MacroTurboAcceptedOutcomeMarkResult MarkAcceptedOutcome()
    {
        if (terminalResult is not null)
        {
            return MacroTurboAcceptedOutcomeMarkResult.Closed;
        }

        if (observedActionCalls == 0)
        {
            terminalResult = MacroTurboExecutionBudgetResult.AcceptedOutcomeWithoutAction;
            return MacroTurboAcceptedOutcomeMarkResult.NoObservedAction;
        }

        if (acceptedOutcomeCount != 0)
        {
            terminalResult = MacroTurboExecutionBudgetResult.MultipleAcceptedOutcomes;
            return MacroTurboAcceptedOutcomeMarkResult.AlreadyMarked;
        }

        acceptedOutcomeCount = 1;
        return MacroTurboAcceptedOutcomeMarkResult.Marked;
    }

    /// <summary>
    /// Closes the execution. Any active execution with zero through the
    /// configured maximum action calls and no more than one accepted outcome is
    /// complete. Existing failures remain terminal.
    /// </summary>
    public MacroTurboExecutionBudgetResult Finish()
    {
        if (terminalResult is { } terminal)
        {
            return terminal;
        }

        terminalResult = MacroTurboExecutionBudgetResult.Complete;
        return terminalResult.Value;
    }
}
