using System.Collections.Immutable;

namespace Cursivis.Domain.Common;

public enum ValidationSeverity
{
    Error,
    Warning,
}

public sealed record ValidationIssue(
    string Path,
    string Code,
    string Message,
    ValidationSeverity Severity = ValidationSeverity.Error);

public class ValidationResult
{
    public static ValidationResult Success { get; } = new([]);

    public ValidationResult(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues.ToImmutableArray();
    }

    public ImmutableArray<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);
}
