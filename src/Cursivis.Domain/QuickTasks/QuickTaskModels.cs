using Cursivis.Domain.Context;

namespace Cursivis.Domain.QuickTasks;

public readonly record struct QuickTaskId
{
    public QuickTaskId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Quick Task identifier is required.", nameof(value));
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64 ||
            normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException(
                "A Quick Task identifier may contain only ASCII letters, digits, and hyphens, up to 64 characters.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

[Flags]
public enum QuickTaskContextType
{
    None = 0,
    Text = 1,
    Image = 2,
    TextAndImage = Text | Image,
}

public enum QuickTaskOutputMode
{
    ReplacementText,
    Analysis,
    StructuredData,
    SuggestedAction,
}

public sealed class QuickTaskDefinition
{
    public const int CurrentSchemaVersion = 1;

    public QuickTaskDefinition(
        QuickTaskId id,
        string displayName,
        string finalizedInstruction,
        QuickTaskContextType supportedContext,
        QuickTaskOutputMode outputMode,
        bool mayProposeAction,
        bool isExplicitlyApproved)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A Quick Task identifier is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A Quick Task display name is required.", nameof(displayName));
        }

        if (displayName.Trim().Length > 64)
        {
            throw new ArgumentException("A Quick Task display name cannot exceed 64 characters.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(finalizedInstruction))
        {
            throw new ArgumentException("A finalized Quick Task instruction is required.", nameof(finalizedInstruction));
        }

        if (finalizedInstruction.Trim().Length > 8_000)
        {
            throw new ArgumentException("A finalized Quick Task instruction cannot exceed 8,000 characters.", nameof(finalizedInstruction));
        }

        if (supportedContext is QuickTaskContextType.None ||
            (supportedContext & ~QuickTaskContextType.TextAndImage) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(supportedContext), "A Quick Task must support text, image, or both.");
        }


        if (!Enum.IsDefined(outputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        }

        Id = id;
        DisplayName = displayName.Trim();
        FinalizedInstruction = finalizedInstruction.Trim();
        SupportedContext = supportedContext;
        OutputMode = outputMode;
        MayProposeAction = mayProposeAction;
        IsExplicitlyApproved = isExplicitlyApproved;
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public QuickTaskId Id { get; }

    public string DisplayName { get; }

    public string FinalizedInstruction { get; }

    public QuickTaskContextType SupportedContext { get; }

    public QuickTaskOutputMode OutputMode { get; }

    public bool MayProposeAction { get; }

    public bool IsExplicitlyApproved { get; }

    public bool Supports(ContextKind contextKind) => contextKind == ContextKind.Image
        ? SupportedContext.HasFlag(QuickTaskContextType.Image)
        : SupportedContext.HasFlag(QuickTaskContextType.Text);

    public override string ToString() =>
        $"QuickTaskDefinition(Id={Id}, Name={DisplayName}, Instruction=<redacted>, Context={SupportedContext}, Output={OutputMode}, MayProposeAction={MayProposeAction}, Approved={IsExplicitlyApproved})";
}

/// <summary>
/// The single saved Quick Task for this milestone. The definition has a stable identity so storage APIs can
/// evolve into named collections without changing the definition contract.
/// </summary>
public sealed record QuickTaskConfiguration
{
    public QuickTaskConfiguration(QuickTaskDefinition current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current;
    }

    public QuickTaskDefinition Current { get; init; }

    public static QuickTaskConfiguration CreateDefault() => new(QuickTaskDefaults.PromptOptimizer);
}

public static class QuickTaskDefaults
{
    public const string PromptOptimizerName = "Prompt Optimizer";

    public static readonly QuickTaskId PromptOptimizerId = new("prompt-optimizer");

    public const string PromptOptimizerInstruction =
        "Rewrite the selected rough prompt into a precise, structured, implementation-ready prompt while preserving every original requirement, constraint, named entity, link, deadline, contradiction, and requested output. Remove unnecessary repetition, filler, and ambiguity only where safely possible. Do not invent missing details or silently discard or resolve requirements. If requirements genuinely conflict, preserve and clearly mark the conflict. Output only the optimized prompt unless the user explicitly requests an explanation.";

    public static QuickTaskDefinition PromptOptimizer { get; } = new(
        PromptOptimizerId,
        PromptOptimizerName,
        PromptOptimizerInstruction,
        QuickTaskContextType.Text,
        QuickTaskOutputMode.ReplacementText,
        mayProposeAction: false,
        isExplicitlyApproved: true);
}
