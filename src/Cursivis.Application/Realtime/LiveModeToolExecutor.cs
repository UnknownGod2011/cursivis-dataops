using System.Text.Json;
using Cursivis.Application.OpenAI;

namespace Cursivis.Application.Realtime;

/// <summary>
/// Typed allowlist for Live Mode. Tool arguments are untrusted model output;
/// every payload is schema-constrained and revalidated before a capability runs.
/// </summary>
public sealed class LiveModeToolExecutor : ILiveModeToolExecutor
{
    private const string EmptyObjectSchema = """
        {
          "type": "object",
          "properties": {},
          "required": [],
          "additionalProperties": false
        }
        """;
    private const string TextSchema = """
        {
          "type": "object",
          "properties": { "text": { "type": "string", "minLength": 1, "maxLength": 20000 } },
          "required": ["text"],
          "additionalProperties": false
        }
        """;
    private const string InstructionSchema = """
        {
          "type": "object",
          "properties": { "instruction": { "type": "string", "minLength": 2, "maxLength": 2000 } },
          "required": ["instruction"],
          "additionalProperties": false
        }
        """;
    private const string NavigationInstructionSchema = """
        {
          "type": "object",
          "properties": { "instruction": { "type": "string", "minLength": 2, "maxLength": 1000 } },
          "required": ["instruction"],
          "additionalProperties": false
        }
        """;
    private const string MemoryIdSchema = """
        {
          "type": "object",
          "properties": { "id": { "type": "string", "format": "uuid" } },
          "required": ["id"],
          "additionalProperties": false
        }
        """;

    private static readonly RealtimeToolDefinition[] BaseDefinitions =
    [
        new(
            "get_selected_text",
            "Return the user's currently selected text. Prefer a fresh capture; fall back to the selection from when Live Mode started.",
            EmptyObjectSchema),
        new(
            "get_active_application",
            "Return the application and window that were active when this Live Mode session began.",
            EmptyObjectSchema),
        new(
            "cancel_current_operation",
            "End the current Cursivis Live Mode session immediately when the user asks to stop.",
            EmptyObjectSchema),
    ];

    private static readonly RealtimeToolDefinition[] MemoryDefinitions =
    [
        new(
            "remember_explicitly",
            "Save one short fact only after the user explicitly says to remember it. Never save conversation history implicitly.",
            TextSchema),
        new(
            "list_saved_memories",
            "List the short facts the user explicitly saved for Live Mode.",
            EmptyObjectSchema),
        new(
            "forget_saved_memory",
            "Delete one explicitly saved Live Mode memory by its id after the user asks to forget it.",
            MemoryIdSchema),
    ];

    private static readonly RealtimeToolDefinition[] CapabilityDefinitions =
    [
        new(
            "copy_text",
            "Copy useful text to the Windows clipboard when the user asks.",
            TextSchema),
        new(
            "insert_text",
            "Insert text into the application and selection captured when Live Mode began.",
            TextSchema),
        new(
            "analyze_screen_region",
            "Open the Cursivis region selector and analyze the chosen screen region only after the user explicitly asks for screen analysis.",
            InstructionSchema),
        new(
            "take_browser_action",
            "Use the shared Cursivis Take Action confirmation and browser-policy workflow for an explicit browser task.",
            InstructionSchema),
        new(
            "start_navigation_guidance",
            "Start visible step-by-step Navigation Guidance only after the user explicitly asks for help navigating the current Windows application.",
            NavigationInstructionSchema),
    ];

    private readonly ILiveModeMemoryStore? _memory;
    private readonly ILiveModeCapabilityExecutor? _capabilities;
    private readonly ILiveModeContextProvider? _contextProvider;
    private readonly IReadOnlyList<RealtimeToolDefinition> _definitions;

    public LiveModeToolExecutor(
        ILiveModeMemoryStore? memory = null,
        ILiveModeCapabilityExecutor? capabilities = null,
        ILiveModeContextProvider? contextProvider = null)
    {
        _memory = memory;
        _capabilities = capabilities;
        _contextProvider = contextProvider;
        _definitions = BaseDefinitions
            .Concat(memory is null ? [] : MemoryDefinitions)
            .Concat(capabilities is null ? [] : CapabilityDefinitions)
            .ToArray();
    }

    public IReadOnlyList<RealtimeToolDefinition> Definitions => _definitions;

    public async ValueTask<LiveModeToolExecutionResult> ExecuteAsync(
        LiveModeContext context,
        string toolName,
        string untrustedArgumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        cancellationToken.ThrowIfCancellationRequested();

        switch (toolName)
        {
            case "get_selected_text":
                ValidateEmptyArguments(untrustedArgumentsJson);
                LiveModeContext selection = context;
                if (_contextProvider is not null)
                {
                    // Fresh capture on the UI thread. Session-start text is only
                    // a fallback when the user no longer has a selection.
                    LiveModeContext refreshed = await _contextProvider
                        .CaptureAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(refreshed.SelectedText))
                    {
                        selection = refreshed;
                    }
                }

                return Json(new
                {
                    available = !string.IsNullOrWhiteSpace(selection.SelectedText),
                    text = selection.SelectedText,
                    contextFingerprint = selection.ContextFingerprint,
                });
            case "get_active_application":
                ValidateEmptyArguments(untrustedArgumentsJson);
                return Json(new
                {
                    available = !string.IsNullOrWhiteSpace(context.ActiveApplication),
                    application = context.ActiveApplication,
                    windowTitle = context.ActiveWindowTitle,
                });
            case "cancel_current_operation":
                ValidateEmptyArguments(untrustedArgumentsJson);
                return new("{\"cancelled\":true}", StopSession: true);
            case "remember_explicitly":
                EnsureAvailable(_memory, toolName);
                LiveModeMemorySaveResult saved = await _memory!.RememberExplicitAsync(
                    ReadStringArgument(untrustedArgumentsJson, "text", 500),
                    cancellationToken).ConfigureAwait(false);
                return Json(new
                {
                    saved = saved.Status == LiveModeMemorySaveStatus.Saved,
                    status = saved.Status.ToString(),
                    id = saved.Entry?.Id,
                    message = saved.SafeMessage,
                });
            case "list_saved_memories":
                EnsureAvailable(_memory, toolName);
                ValidateEmptyArguments(untrustedArgumentsJson);
                LiveModeMemorySnapshot snapshot = await _memory!.GetAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Json(new
                {
                    enabled = snapshot.IsEnabled,
                    entries = snapshot.Entries.Select(entry => new
                    {
                        id = entry.Id,
                        text = entry.Text,
                        createdAtUtc = entry.CreatedAtUtc,
                    }),
                });
            case "forget_saved_memory":
                EnsureAvailable(_memory, toolName);
                string rawId = ReadStringArgument(untrustedArgumentsJson, "id", 64);
                bool removed = Guid.TryParse(rawId, out Guid id) &&
                    await _memory!.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                return Json(new { removed });
            case "copy_text":
                EnsureAvailable(_capabilities, toolName);
                return Capability(await _capabilities!.CopyAsync(
                    ReadStringArgument(untrustedArgumentsJson, "text", 20_000),
                    cancellationToken).ConfigureAwait(false));
            case "insert_text":
                EnsureAvailable(_capabilities, toolName);
                return Capability(await _capabilities!.InsertAsync(
                    context,
                    ReadStringArgument(untrustedArgumentsJson, "text", 20_000),
                    cancellationToken).ConfigureAwait(false));
            case "analyze_screen_region":
                EnsureAvailable(_capabilities, toolName);
                return Capability(await _capabilities!.AnalyzeScreenAsync(
                    ReadStringArgument(untrustedArgumentsJson, "instruction", 2_000),
                    cancellationToken).ConfigureAwait(false));
            case "take_browser_action":
                EnsureAvailable(_capabilities, toolName);
                return Capability(await _capabilities!.TakeBrowserActionAsync(
                    ReadStringArgument(untrustedArgumentsJson, "instruction", 2_000),
                    cancellationToken).ConfigureAwait(false));
            case "start_navigation_guidance":
                EnsureAvailable(_capabilities, toolName);
                return Capability(await _capabilities!.NavigateAsync(
                    ReadStringArgument(untrustedArgumentsJson, "instruction", 1_000),
                    cancellationToken).ConfigureAwait(false));
            default:
                throw new InvalidOperationException("The requested Live Mode tool is not allowlisted.");
        }
    }

    private static LiveModeToolExecutionResult Capability(LiveModeCapabilityResult result) =>
        Json(new
        {
            succeeded = result.Succeeded,
            message = result.SafeMessage,
            output = result.Output,
        });

    private static LiveModeToolExecutionResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value));

    private static string ReadStringArgument(string json, string propertyName, int maximumLength)
    {
        using JsonDocument document = ParseObject(json);
        JsonElement root = document.RootElement;
        JsonProperty[] properties = root.EnumerateObject().ToArray();
        if (properties.Length != 1 ||
            !string.Equals(properties[0].Name, propertyName, StringComparison.Ordinal) ||
            properties[0].Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The Live Mode tool received unsupported arguments.");
        }

        string value = properties[0].Value.GetString()?.Trim() ?? string.Empty;
        if (value.Length is < 1 || value.Length > maximumLength)
        {
            throw new InvalidOperationException("The Live Mode tool argument was outside its allowed length.");
        }

        return value;
    }

    private static void ValidateEmptyArguments(string json)
    {
        using JsonDocument document = ParseObject(json);
        if (document.RootElement.EnumerateObject().Any())
        {
            throw new InvalidOperationException("The Live Mode tool received unsupported arguments.");
        }
    }

    private static JsonDocument ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            json = "{}";
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Live Mode tool arguments were invalid.", exception);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidOperationException("The Live Mode tool received unsupported arguments.");
        }

        return document;
    }

    private static void EnsureAvailable(object? dependency, string toolName)
    {
        if (dependency is null)
        {
            throw new InvalidOperationException($"The {toolName} capability is unavailable in this runtime.");
        }
    }
}
