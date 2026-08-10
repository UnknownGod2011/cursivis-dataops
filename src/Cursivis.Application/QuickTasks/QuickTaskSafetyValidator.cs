using System.Text.RegularExpressions;

namespace Cursivis.Application.QuickTasks;

public static partial class QuickTaskSafetyValidator
{
    private static readonly (string Code, string Message, Regex Pattern)[] Rules =
    [
        ("policy-bypass", "The task attempts to weaken a safety, permission, or confirmation policy.", PolicyBypassPattern()),
        ("secret-access", "The task attempts to access credentials, API keys, or secrets.", SecretAccessPattern()),
        ("arbitrary-execution", "The task attempts to run shell commands or generated code.", ArbitraryExecutionPattern()),
        ("silent-capture", "The task attempts to inspect the screen without an explicit capture action.", SilentCapturePattern()),
        ("unsafe-external-action", "The task attempts an external action without the normal confirmation flow.", UnsafeExternalActionPattern()),
        ("mode-escalation", "The task attempts to activate a higher-privilege interaction mode.", ModeEscalationPattern()),
    ];

    public static IReadOnlyList<QuickTaskSafetyIssue> Validate(string instruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        return Rules
            .Where(rule => rule.Pattern.IsMatch(instruction))
            .Select(rule => new QuickTaskSafetyIssue(rule.Code, rule.Message))
            .ToArray();
    }

    [GeneratedRegex(@"(?<!do not )\b(?:bypass|disable|ignore|override)\b.{0,48}\b(?:safety|policy|permission|confirmation|risk classification)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyBypassPattern();

    [GeneratedRegex(@"\b(?:read|reveal|expose|extract|steal|use)\b.{0,48}\b(?:api key|credential|password|secret|token)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAccessPattern();

    [GeneratedRegex(@"\b(?:run|execute|launch)\b.{0,48}\b(?:shell command|powershell|cmd\.exe|generated code|arbitrary code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArbitraryExecutionPattern();

    [GeneratedRegex(@"\b(?:silently|automatically)\b.{0,48}\b(?:inspect|capture|scan)\b.{0,32}\b(?:entire screen|full screen|desktop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SilentCapturePattern();

    [GeneratedRegex(@"\b(?:submit|send|purchase|delete|post)\b.{0,48}\b(?:without confirmation|automatically|without asking)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeExternalActionPattern();

    [GeneratedRegex(@"\b(?:activate|start|enable)\b.{0,48}\b(?:navigation guidance|live mode|realtime mode|talk mode)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModeEscalationPattern();
}
