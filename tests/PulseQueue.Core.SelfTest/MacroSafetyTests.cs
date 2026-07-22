using PulseQueue.Core;

internal static class MacroSafetyTests
{
    public static IEnumerable<(string Name, Action Body)> All()
    {
        yield return ("single action macro is eligible", SingleActionIsEligible);
        yield return ("PvP action macro with metadata is eligible", PvpActionWithMetadataIsEligible);
        yield return ("one original-only assist resolver is eligible", AssistBeforeActionIsEligible);
        yield return ("second action line rejects macro", SecondActionRejects);
        yield return ("wait directive rejects macro", WaitRejects);
        yield return ("target and chat commands reject macro", SideEffectCommandsReject);
        yield return ("macro fingerprint changes with exact action text", FingerprintTracksContent);
    }

    private static void SingleActionIsEligible()
    {
        var value = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <me>"]);
        True(value.IsSafe);
        Equal(MacroSafetyFailure.None, value.Failure);
        Equal("/ac \"Guard\" <me>", value.Profile.ActionCommand);
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
    }

    private static void AssistBeforeActionIsEligible()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/micon \"Blendga\" pvpaction",
            "/assist <f>",
            "/pvpaction \"Blendga\" <t>",
        ]);
        True(value.IsSafe);
        True(value.Profile.HasOneShotTargetResolver);

        var resolverAfterAction = MacroSafetyAnalyzer.Analyze([
            "/pvpaction \"Blendga\" <t>",
            "/assist <f>",
        ]);
        False(resolverAfterAction.IsSafe);
    }

    private static void SecondActionRejects()
    {
        var value = MacroSafetyAnalyzer.Analyze([
            "/ac \"Guard\" <me>",
            "/pvpaction \"Purify\" <me>",
        ]);
        False(value.IsSafe);
        Equal(MacroSafetyFailure.MultipleActions, value.Failure);
        Equal(2, value.RejectedLine);
    }

    private static void WaitRejects()
    {
        var value = MacroSafetyAnalyzer.Analyze(["/ac \"Guard\" <me> <wait.1>"]);
        False(value.IsSafe);
        Equal(MacroSafetyFailure.WaitDirective, value.Failure);
    }

    private static void SideEffectCommandsReject()
    {
        foreach (var command in new[] { "/target <2>", "/p hello", "/gearset change 1", "/hotbar copy 1 2" })
        {
            var value = MacroSafetyAnalyzer.Analyze([command, "/ac \"Guard\" <me>"]);
            False(value.IsSafe);
            Equal(MacroSafetyFailure.UnsupportedCommand, value.Failure);
        }
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
