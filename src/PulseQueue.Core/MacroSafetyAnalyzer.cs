using System.Security.Cryptography;
using System.Text;

namespace PulseQueue.Core;

public enum MacroSafetyFailure
{
    None,
    Empty,
    UnsupportedCommand,
    WaitDirective,
    MissingAction,
    MultipleActions,
}

public readonly record struct SafeActionMacroProfile(
    string ContentFingerprint,
    string ActionCommand,
    bool HasOneShotTargetResolver)
{
    public bool IsValid =>
        ContentFingerprint.Length == 64
        && !string.IsNullOrWhiteSpace(ActionCommand);
}

public readonly record struct MacroSafetyAnalysis(
    SafeActionMacroProfile Profile,
    MacroSafetyFailure Failure,
    int RejectedLine)
{
    public bool IsSafe => Failure == MacroSafetyFailure.None && Profile.IsValid;
}

/// <summary>
/// Accepts only macros from which one exact native action tuple can be captured.
/// Empty lines and icon/error-display metadata are harmless. One /assist resolver
/// may precede the action because it runs only on the player's original physical
/// press; synthetic repeats never replay the macro or its resolver.
/// </summary>
public static class MacroSafetyAnalyzer
{
    private static readonly HashSet<string> ActionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/ac",
        "/action",
        "/pvpac",
        "/pvpaction",
    };

    private static readonly HashSet<string> HarmlessMetadataCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/micon",
        "/macroicon",
        "/merror",
        "/macroerror",
    };

    public static MacroSafetyAnalysis Analyze(IEnumerable<string?> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var canonical = new StringBuilder();
        string? actionLine = null;
        var hasResolver = false;
        var lineNumber = 0;
        var anyContent = false;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw?.Trim() ?? string.Empty;
            canonical.Append(line).Append('\n');
            if (line.Length == 0) continue;
            anyContent = true;

            if (ContainsWaitDirective(line))
            {
                return Rejected(canonical, MacroSafetyFailure.WaitDirective, lineNumber);
            }

            var command = ReadCommand(line);
            if (ActionCommands.Contains(command))
            {
                if (actionLine is not null)
                {
                    return Rejected(canonical, MacroSafetyFailure.MultipleActions, lineNumber);
                }

                actionLine = line;
                continue;
            }

            if (command.Equals("/assist", StringComparison.OrdinalIgnoreCase)
                && actionLine is null
                && !hasResolver)
            {
                hasResolver = true;
                continue;
            }

            if (!HarmlessMetadataCommands.Contains(command))
            {
                return Rejected(canonical, MacroSafetyFailure.UnsupportedCommand, lineNumber);
            }
        }

        if (!anyContent)
        {
            return Rejected(canonical, MacroSafetyFailure.Empty, 0);
        }

        if (actionLine is null)
        {
            return Rejected(canonical, MacroSafetyFailure.MissingAction, 0);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new MacroSafetyAnalysis(
            new SafeActionMacroProfile(fingerprint, actionLine, hasResolver),
            MacroSafetyFailure.None,
            0);
    }

    private static MacroSafetyAnalysis Rejected(
        StringBuilder canonical,
        MacroSafetyFailure failure,
        int line)
    {
        _ = canonical;
        return new MacroSafetyAnalysis(default, failure, line);
    }

    private static string ReadCommand(string line)
    {
        var split = line.IndexOfAny([' ', '\t']);
        return split < 0 ? line : line[..split];
    }

    private static bool ContainsWaitDirective(string line) =>
        line.Contains("<wait", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase);
}
