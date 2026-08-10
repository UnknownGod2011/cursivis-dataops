using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cursivis.Contracts.Browser;

public static class BrowserMessageTypes
{
    public const string Hello = "hello";
    public const string Welcome = "welcome";
    public const string GetSelection = "get_selection";
    public const string Selection = "selection";
    public const string DiscoverForm = "discover_form";
    public const string Form = "form";
    public const string Execute = "execute";
    public const string StepResult = "step_result";
    public const string Health = "health";
    public const string Error = "error";
}

public sealed record BrowserEnvelope(
    int ProtocolVersion,
    string Type,
    string CorrelationId,
    DateTimeOffset SentAt,
    JsonElement Payload,
    string? SessionToken = null)
{
    public const int MaxCorrelationIdLength = 96;

    public static BrowserEnvelope Create<T>(
        string type,
        string correlationId,
        T payload,
        DateTimeOffset? sentAt = null,
        string? sessionToken = null) =>
        new(
            ProtocolVersions.BrowserBridge,
            type,
            correlationId,
            sentAt ?? DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(payload, BrowserJson.SerializerOptions),
            sessionToken);
}

public sealed record BrowserHello(
    string ExtensionId,
    string ExtensionVersion,
    string Nonce,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> RequestedCapabilities);

public sealed record BrowserWelcome(
    string SessionId,
    string SessionToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> GrantedCapabilities);

public sealed record BrowserTabIdentity(
    int TabId,
    long NavigationGeneration,
    string UrlOrigin,
    bool IsActive,
    bool IsTopFrame);

public sealed record BrowserSelectionRequest(
    BrowserTabIdentity Tab,
    bool IncludeNearbySemanticContext,
    int MaximumCharacters);

public sealed record BrowserSelectionResponse(
    BrowserTabIdentity Tab,
    string SelectedText,
    string? NearbySemanticContext,
    BrowserFocusedElement? FocusedElement,
    bool IsTruncated);

public sealed record BrowserFocusedElement(
    string ElementType,
    string? AccessibleName,
    bool IsEditable,
    bool IsSensitive);

public enum BrowserFormFieldKind
{
    Text,
    TextArea,
    Email,
    Number,
    Radio,
    Checkbox,
    Select,
    Date,
    Unsupported,
}

public sealed record BrowserFormField(
    string StableTargetId,
    BrowserFormFieldKind Kind,
    string Label,
    string? Description,
    bool IsRequired,
    bool IsSensitive,
    string? CurrentValue,
    IReadOnlyList<string> Options);

public enum BrowserFormControlKind
{
    SearchSubmit,
    Submit,
    Navigation,
}

public sealed record BrowserFormControl(
    string StableTargetId,
    BrowserFormControlKind Kind,
    string Label,
    bool IsHighImpact);

public sealed record BrowserFormSnapshot(
    BrowserTabIdentity Tab,
    string FormId,
    string? AccessibleName,
    IReadOnlyList<BrowserFormField> Fields,
    IReadOnlyList<BrowserFormControl> Controls,
    string ContextFingerprint);

public enum BrowserCommandKind
{
    SetValue,
    SetChecked,
    SelectOption,
    Focus,
    Highlight,
    ClickNavigation,
    SubmitSearch,
    Submit,
}

public sealed record BrowserCommand(
    BrowserTabIdentity Tab,
    string ContextFingerprint,
    string StepId,
    BrowserCommandKind Kind,
    string StableTargetId,
    string? Value,
    bool? Checked,
    string? ConfirmationId);

public sealed record BrowserStepResult(
    string StepId,
    bool Executed,
    bool Verified,
    string? ObservedValue,
    string? SafeFailureCode,
    string? SafeMessage);

public sealed record BrowserHealth(
    bool Connected,
    string ExtensionVersion,
    int ProtocolVersion,
    DateTimeOffset? LastAuthenticatedHandshake,
    IReadOnlyList<string> Capabilities);

public sealed record BrowserError(string Code, string Message, bool Retryable);

public static class BrowserJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
