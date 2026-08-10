using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Application.QuickTasks;

public enum QuickTaskFinalizationStatus
{
    Completed,
    Rejected,
    ProviderFailed,
    MalformedResponse,
    Cancelled,
}

public sealed record QuickTaskDraft(
    string DisplayName,
    string FinalizedInstruction,
    QuickTaskContextType SupportedContext,
    QuickTaskOutputMode OutputMode,
    bool MayProposeAction,
    IReadOnlyList<string> SafetyConcerns);

public sealed record QuickTaskFinalizationResult(
    QuickTaskFinalizationStatus Status,
    QuickTaskDraft? Draft,
    OpenAiFailure? Failure,
    ModelIdentifier Model,
    TimeSpan Duration);

public interface IQuickTaskFinalizationService
{
    Task<QuickTaskFinalizationResult> FinalizeAsync(
        string requestedName,
        string roughDescription,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);
}

public sealed record QuickTaskSafetyIssue(string Code, string Message);
