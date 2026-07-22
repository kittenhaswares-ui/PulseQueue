using PulseQueue.Core;

internal static class MacroSafetyTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("single action macro is eligible", SingleActionIsEligible);
        yield return ("PvP action macro with metadata is eligible", PvpActionWithMetadataIsEligible);
        yield return ("multiple action lines are eligible", MultipleActionsAreEligible);
        yield return ("all action aliases count", AllActionAliasesCount);
        yield return ("wait directive rejects macro", WaitRejects);
        yield return ("assist and side-effect commands reject macro", SideEffectCommandsReject);
        yield return ("metadata-only macro has no action", MetadataOnlyIsMissingAction);
        yield return ("macro fingerprint changes with exact action text", FingerprintTracksContent);
    }

    private static void SingleActionIsEligible()
    {
        var value = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <me>"]);
        True(value.IsSafe);
        Equal(MacroSafetyFailure.None, value.Failure);
        Equal(1, value.Profile.ActionCount);
        Equal(64, value.Profile.ContentFingerprint.Length);
    }

    private static void PvpActionWithMetadataIsEligible()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/micon \"Purify\" pvpaction",
            "/merror off",
            "/pvpaction \"Purify\" <me>",
        ]);
        True(value.IsSafe);
        Equal(1, value.Profile.ActionCount);
    }

    private static void MultipleActionsAreEligible()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/micon \"Purify\" pvpaction",
            "/pvpaction \"Purify\" <mo>",
            "/pvpaction \"Guard\" <me>",
            "/ac \"Recuperate\" <me>",
        ]);
        True(value.IsSafe);
        Equal(MacroSafetyFailure.None, value.Failure);
        Equal(3, value.Profile.ActionCount);
    }

    private static void AllActionAliasesCount()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/ac \"Guard\" <me>",
            "/ACTION \"Guard\" <me>",
            "/pvpac \"Purify\" <me>",
            "/PvPaCtIoN \"Purify\" <me>",
        ]);
        True(value.IsSafe);
        Equal(4, value.Profile.ActionCount);
    }

    private static void WaitRejects()
    {
        var value = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <me> <wait.1>"]);
        False(value.IsSafe);
        Equal(MacroSafetyFailure.WaitDirective, value.Failure);
    }

    private static void SideEffectCommandsReject()
    {
        foreach (var command in new[]
                 {
                     "/assist <f>",
                     "/target <2>",
                     "/p hello",
                     "/item \"Potion\" <me>",
                     "/gearset change 1",
                     "/hotbar copy 1 2",
                     "/unknown value",
                 })
        {
            var value = MacroSafetyAnalyzer.Analyze([command, "/ac \"Guard\" <me>"]);
            False(value.IsSafe);
            Equal(MacroSafetyFailure.UnsupportedCommand, value.Failure);
            Equal(1, value.RejectedLine);
        }
    }

    private static void MetadataOnlyIsMissingAction()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/micon \"Guard\" pvpaction",
            "/macroerror off",
        ]);
        False(value.IsSafe);
        Equal(MacroSafetyFailure.MissingAction, value.Failure);
    }

    private static void FingerprintTracksContent()
    {
        var first = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <me>"]);
        var same = MacroSafetyAnalyzer.Analyze(["  /ac \"Guard\" <me>  "]);
        var changed = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <t>"]);
        Equal(first.Profile.ContentFingerprint, same.Profile.ContentFingerprint);
        NotEqual(first.Profile.ContentFingerprint, changed.Profile.ContentFingerprint);
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void NotEqual<T>(T left, T right)
        where T : notnull
    {
        if (EqualityComparer<T>.Default.Equals(left, right))
        {
            throw new InvalidOperationException($"Expected values to differ, got {left}.");
        }
    }
}
