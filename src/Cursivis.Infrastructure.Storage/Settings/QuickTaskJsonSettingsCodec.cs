using System.Text.Json;
using System.Text.Json.Nodes;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Infrastructure.Storage.Settings;

public sealed class QuickTaskJsonSettingsCodec : IJsonSettingsCodec<QuickTaskDefinition>
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "id",
        "displayName",
        "finalizedInstruction",
        "supportedContext",
        "outputMode",
        "mayProposeAction",
        "isExplicitlyApproved",
    };
    private readonly Func<QuickTaskDefinition, bool> _validator;

    public QuickTaskJsonSettingsCodec(Func<QuickTaskDefinition, bool>? validator = null)
    {
        _validator = validator ?? (static definition => definition.IsExplicitlyApproved);
    }

    public QuickTaskDefinition CreateDefault() => QuickTaskDefaults.PromptOptimizer;

    public JsonObject Encode(QuickTaskDefinition value, JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_validator(value))
        {
            throw new InvalidOperationException("Quick Task validation failed before persistence.");
        }

        return new JsonObject
        {
            ["schemaVersion"] = value.SchemaVersion,
            ["id"] = value.Id.Value,
            ["displayName"] = value.DisplayName,
            ["finalizedInstruction"] = value.FinalizedInstruction,
            ["supportedContext"] = FormatContext(value.SupportedContext),
            ["outputMode"] = FormatOutputMode(value.OutputMode),
            ["mayProposeAction"] = value.MayProposeAction,
            ["isExplicitlyApproved"] = value.IsExplicitlyApproved,
        };
    }

    public SettingsDecodeResult<QuickTaskDefinition> Decode(
        JsonObject data,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Any(property => !KnownProperties.Contains(property.Key)))
        {
            throw new JsonException("The Quick Task definition contains unknown properties.");
        }

        try
        {
            if (ReadRequired<int>(data, "schemaVersion") != QuickTaskDefinition.CurrentSchemaVersion)
            {
                throw new JsonException("The Quick Task schema version is unsupported.");
            }

            var definition = new QuickTaskDefinition(
                new QuickTaskId(ReadRequired<string>(data, "id")),
                ReadRequired<string>(data, "displayName"),
                ReadRequired<string>(data, "finalizedInstruction"),
                ParseContext(ReadRequired<string>(data, "supportedContext")),
                ParseOutputMode(ReadRequired<string>(data, "outputMode")),
                ReadRequired<bool>(data, "mayProposeAction"),
                ReadRequired<bool>(data, "isExplicitlyApproved"));
            if (!_validator(definition))
            {
                throw new JsonException("The stored Quick Task failed validation.");
            }

            return new SettingsDecodeResult<QuickTaskDefinition>(definition, false, []);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new JsonException("The stored Quick Task is malformed.", exception);
        }
    }

    private static T ReadRequired<T>(JsonObject data, string propertyName)
    {
        if (data[propertyName] is not JsonValue value || !value.TryGetValue(out T? result) || result is null)
        {
            throw new JsonException($"The Quick Task property '{propertyName}' is missing or invalid.");
        }

        return result;
    }

    private static string FormatContext(QuickTaskContextType context) => context switch
    {
        QuickTaskContextType.Text => "text",
        QuickTaskContextType.Image => "image",
        QuickTaskContextType.TextAndImage => "text_and_image",
        _ => throw new InvalidOperationException("The Quick Task context is invalid."),
    };

    private static QuickTaskContextType ParseContext(string context) => context switch
    {
        "text" => QuickTaskContextType.Text,
        "image" => QuickTaskContextType.Image,
        "text_and_image" => QuickTaskContextType.TextAndImage,
        _ => throw new JsonException("The Quick Task context is invalid."),
    };

    private static string FormatOutputMode(QuickTaskOutputMode mode) => mode switch
    {
        QuickTaskOutputMode.ReplacementText => "replacement_text",
        QuickTaskOutputMode.Analysis => "analysis",
        QuickTaskOutputMode.StructuredData => "structured_data",
        QuickTaskOutputMode.SuggestedAction => "suggested_action",
        _ => throw new InvalidOperationException("The Quick Task output mode is invalid."),
    };

    private static QuickTaskOutputMode ParseOutputMode(string mode) => mode switch
    {
        "replacement_text" => QuickTaskOutputMode.ReplacementText,
        "analysis" => QuickTaskOutputMode.Analysis,
        "structured_data" => QuickTaskOutputMode.StructuredData,
        "suggested_action" => QuickTaskOutputMode.SuggestedAction,
        _ => throw new JsonException("The Quick Task output mode is invalid."),
    };
}
