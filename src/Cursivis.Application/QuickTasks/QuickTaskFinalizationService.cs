using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Application.QuickTasks;

public sealed class QuickTaskFinalizationService : IQuickTaskFinalizationService
{
    private const string SchemaResourceName = "Cursivis.Schemas.QuickTaskDraft";
    private const string SchemaName = "cursivis_quick_task_draft";
    private const string SystemInstruction =
        "Turn the user's rough Quick Task description into one concise, production-ready Cursivis task definition. " +
        "Preserve the intended transformation without inventing destinations, credentials, permissions, tools, or product decisions. " +
        "The instruction may transform explicit text or image context and may propose a typed action only when requested. " +
        "It can never weaken permission policy, risk classification, confirmation, secret handling, screen-capture consent, browser security, or trusted tools. " +
        "List every safety concern. Do not resolve a concerning request by silently removing it.";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly Lazy<string> DraftSchema = new(LoadSchema);
    private readonly IResponsesGateway _responsesGateway;

    public QuickTaskFinalizationService(IResponsesGateway responsesGateway)
    {
        _responsesGateway = responsesGateway ?? throw new ArgumentNullException(nameof(responsesGateway));
    }

    public async Task<QuickTaskFinalizationResult> FinalizeAsync(
        string requestedName,
        string roughDescription,
        ModelIdentifier model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(roughDescription);
        string name = requestedName.Trim();
        string description = roughDescription.Trim();
        if (name.Length > 64)
        {
            throw new ArgumentException("The Quick Task name exceeds 64 characters.", nameof(requestedName));
        }

        if (description.Length is < 10 or > 2_000)
        {
            throw new ArgumentException("The rough description must contain 10 to 2,000 characters.", nameof(roughDescription));
        }

        ModelDescriptor descriptor = ModelCatalog.Find(model)
            ?? throw new ArgumentException("The selected model is not in the Cursivis model registry.", nameof(model));
        if (descriptor.Role != ModelRole.Responses ||
            !descriptor.Capabilities.HasFlag(ModelCapabilities.StructuredOutputs))
        {
            throw new ArgumentException("Quick Task finalization requires structured Responses output.", nameof(model));
        }

        long started = Stopwatch.GetTimestamp();
        StructuredResponseResult response = await _responsesGateway.CreateStructuredResponseAsync(
            new StructuredResponseRequest(
                model.Value,
                SystemInstruction,
                $"Requested display name: {name}\n\n<rough_task_description>\n{description}\n</rough_task_description>",
                SchemaName,
                DraftSchema.Value,
                TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);
        TimeSpan duration = Stopwatch.GetElapsedTime(started);

        if (!response.Succeeded)
        {
            OpenAiFailure failure = response.Failure ?? new OpenAiFailure(
                OpenAiFailureKind.Unknown,
                "OpenAI failed to finalize the Quick Task.",
                false);
            return new QuickTaskFinalizationResult(
                failure.Kind == OpenAiFailureKind.Cancelled
                    ? QuickTaskFinalizationStatus.Cancelled
                    : QuickTaskFinalizationStatus.ProviderFailed,
                null,
                failure,
                model,
                duration);
        }

        if (!TryParse(response.Json, out QuickTaskDraft? draft) || draft is null)
        {
            return new QuickTaskFinalizationResult(
                QuickTaskFinalizationStatus.MalformedResponse,
                null,
                new OpenAiFailure(
                    OpenAiFailureKind.MalformedResponse,
                    "OpenAI returned a malformed Quick Task definition.",
                    false,
                    response.ResponseId),
                model,
                duration);
        }

        IReadOnlyList<QuickTaskSafetyIssue> localIssues = QuickTaskSafetyValidator.Validate(draft.FinalizedInstruction);
        bool rejected = draft.SafetyConcerns.Count > 0 || localIssues.Count > 0;
        if (localIssues.Count > 0)
        {
            draft = draft with
            {
                SafetyConcerns = draft.SafetyConcerns
                    .Concat(localIssues.Select(static issue => issue.Message))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        return new QuickTaskFinalizationResult(
            rejected ? QuickTaskFinalizationStatus.Rejected : QuickTaskFinalizationStatus.Completed,
            draft,
            null,
            model,
            duration);
    }

    private static bool TryParse(string? json, out QuickTaskDraft? draft)
    {
        draft = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            QuickTaskDraftDocument? document = JsonSerializer.Deserialize<QuickTaskDraftDocument>(json, SerializerOptions);
            if (document is null || document.SchemaVersion != 1 ||
                document.DisplayName is null || document.FinalizedInstruction is null ||
                document.SupportedContext is null || document.OutputMode is null ||
                document.SafetyConcerns is null ||
                document.DisplayName.Trim().Length is < 1 or > 64 ||
                document.FinalizedInstruction.Trim().Length is < 40 or > 8_000 ||
                document.SafetyConcerns.Count > 10)
            {
                return false;
            }

            draft = new QuickTaskDraft(
                document.DisplayName.Trim(),
                document.FinalizedInstruction.Trim(),
                ParseContext(document.SupportedContext),
                ParseOutputMode(document.OutputMode),
                document.MayProposeAction,
                document.SafetyConcerns.Select(static concern => concern.Trim()).ToArray());
            return draft.SafetyConcerns.All(static concern =>
                concern is not null && concern.Length is > 0 and <= 200);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static QuickTaskContextType ParseContext(string value) => value switch
    {
        "text" => QuickTaskContextType.Text,
        "image" => QuickTaskContextType.Image,
        "text_and_image" => QuickTaskContextType.TextAndImage,
        _ => throw new ArgumentException("Unsupported Quick Task context.", nameof(value)),
    };

    private static QuickTaskOutputMode ParseOutputMode(string value) => value switch
    {
        "replacement_text" => QuickTaskOutputMode.ReplacementText,
        "analysis" => QuickTaskOutputMode.Analysis,
        "structured_data" => QuickTaskOutputMode.StructuredData,
        "suggested_action" => QuickTaskOutputMode.SuggestedAction,
        _ => throw new ArgumentException("Unsupported Quick Task output mode.", nameof(value)),
    };

    private static string LoadSchema()
    {
        using Stream stream = typeof(QuickTaskFinalizationService).Assembly
            .GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException("The Quick Task draft schema is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record QuickTaskDraftDocument(
        int SchemaVersion,
        string DisplayName,
        string FinalizedInstruction,
        string SupportedContext,
        string OutputMode,
        bool MayProposeAction,
        IReadOnlyList<string> SafetyConcerns);
}
