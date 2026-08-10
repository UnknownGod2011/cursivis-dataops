using System.Collections.Immutable;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;

namespace Cursivis.Domain.Interaction;

public enum InteractionMode
{
    Smart,
    Guided,
}

public enum SmartIntent
{
    Translate,
    Reply,
    Rewrite,
    Summarize,
    Explain,
    Debug,
    Extract,
    Identify,
    Answer,
    Other,
}

public enum SuggestedActionType
{
    None,
    ReplaceSelection,
    InsertText,
    FillForm,
    Copy,
    BrowserPlan,
}

public enum ConfirmationHint
{
    None,
    ContextDependent,
    Required,
}

public sealed record SuggestedAction(SuggestedActionType Type, string Summary)
{
    public static SuggestedAction None { get; } = new(SuggestedActionType.None, "No action suggested");

    public SuggestedAction Validate()
    {
        if (string.IsNullOrWhiteSpace(Summary))
        {
            throw new ArgumentException("A suggested action summary is required.", nameof(Summary));
        }

        return this;
    }

    public override string ToString() =>
        $"SuggestedAction(Type={Type}, Summary=<redacted>)";
}

public sealed record SmartResult
{
    public const int CurrentSchemaVersion = 1;

    public SmartResult(
        ContextKind contextType,
        SmartIntent intent,
        double confidence,
        string finalContent,
        SuggestedAction suggestedAction,
        RiskLevel riskHint,
        ConfirmationHint confirmationHint,
        string? operationLabel = null)
    {
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between zero and one.");
        }

        if (string.IsNullOrWhiteSpace(finalContent))
        {
            throw new ArgumentException("Smart Mode must return useful final content.", nameof(finalContent));
        }

        ArgumentNullException.ThrowIfNull(suggestedAction);
        if (operationLabel is not null &&
            (string.IsNullOrWhiteSpace(operationLabel) || operationLabel.Trim().Length > 40))
        {
            throw new ArgumentException(
                "The operation label must be a concise user-visible action.",
                nameof(operationLabel));
        }

        ContextType = contextType;
        Intent = intent;
        Confidence = confidence;
        FinalContent = finalContent;
        SuggestedAction = suggestedAction.Validate();
        RiskHint = riskHint;
        ConfirmationHint = confirmationHint;
        OperationLabel = operationLabel?.Trim();
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public ContextKind ContextType { get; }

    public SmartIntent Intent { get; }

    public double Confidence { get; }

    public string FinalContent { get; }

    public SuggestedAction SuggestedAction { get; }

    public RiskLevel RiskHint { get; }

    public ConfirmationHint ConfirmationHint { get; }

    public string? OperationLabel { get; }

    public override string ToString() =>
        $"SmartResult(Context={ContextType}, Intent={Intent}, Confidence={Confidence:F2}, Content=<redacted>, SuggestedAction={SuggestedAction.Type}, RiskHint={RiskHint})";
}

public enum GuidedOperation
{
    Summarize,
    Explain,
    Translate,
    Rewrite,
    MakeProfessional,
    DraftReply,
    ExtractKeyPoints,
    TurnIntoTasks,
    ImprovePrompt,
    AskQuestion,
    Debug,
    Optimize,
    Refactor,
    AddComments,
    FindSecurityIssues,
    GenerateTests,
    Describe,
    IdentifyObjects,
    ExtractText,
    ExtractTable,
    ExplainUserInterface,
    FindIssue,
    AnalyzeColors,
    CustomTask,
}

public static class GuidedOperationCatalog
{
    private static readonly ImmutableArray<GuidedOperation> TextOperations =
    [
        GuidedOperation.Summarize,
        GuidedOperation.Explain,
        GuidedOperation.Translate,
        GuidedOperation.Rewrite,
        GuidedOperation.MakeProfessional,
        GuidedOperation.DraftReply,
        GuidedOperation.ExtractKeyPoints,
        GuidedOperation.TurnIntoTasks,
        GuidedOperation.ImprovePrompt,
        GuidedOperation.AskQuestion,
        GuidedOperation.CustomTask,
    ];

    private static readonly ImmutableArray<GuidedOperation> CodeOperations =
    [
        GuidedOperation.Explain,
        GuidedOperation.Debug,
        GuidedOperation.Optimize,
        GuidedOperation.Refactor,
        GuidedOperation.AddComments,
        GuidedOperation.FindSecurityIssues,
        GuidedOperation.GenerateTests,
        GuidedOperation.CustomTask,
    ];

    private static readonly ImmutableArray<GuidedOperation> ImageOperations =
    [
        GuidedOperation.Describe,
        GuidedOperation.IdentifyObjects,
        GuidedOperation.ExtractText,
        GuidedOperation.ExtractTable,
        GuidedOperation.ExplainUserInterface,
        GuidedOperation.FindIssue,
        GuidedOperation.Translate,
        GuidedOperation.AnalyzeColors,
        GuidedOperation.CustomTask,
    ];

    public static ImmutableArray<GuidedOperation> For(ContextKind kind) => kind switch
    {
        ContextKind.Code => CodeOperations,
        ContextKind.Image or ContextKind.UserInterface => ImageOperations,
        _ => TextOperations,
    };
}
