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
}

public readonly record struct SafeActionMacroProfile(
    string ContentFingerprint,
    int ActionCount)
{
    public bool IsValid =>
        ContentFingerprint.Length == 64
        && ActionCount > 0;
}

public readonly record struct MacroSafetyAnalysis(
    SafeActionMacroProfile Profile,
    MacroSafetyFailure Failure,
    int RejectedLine)
{
    public bool IsSafe => Failure == MacroSafetyFailure.None && Profile.IsValid;
}

/// <summary>
/// Accepts action-only macros from which the runtime can observe one or more native
/// action attempts. Empty lines and icon/error-display metadata are harmless. Every
/// other command is rejected so repeating the same physical control cannot replay a
/// target resolver, chat message, item, hotbar mutation, or other side effect.
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
        var actionCount = 0;
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
                actionCount++;
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

        if (actionCount == 0)
        {
            return Rejected(canonical, MacroSafetyFailure.MissingAction, 0);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new MacroSafetyAnalysis(
            new SafeActionMacroProfile(fingerprint, actionCount),
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
