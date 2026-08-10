using System.Text.Json;
using System.Text.Json.Nodes;
using Cursivis.Domain.Models;
using Cursivis.Domain.Settings;

namespace Cursivis.Infrastructure.Storage.Settings;

/// <summary>
/// Persists the unified application preferences while leaving Quick Task and
/// hotkey payloads in their dedicated transactional stores. Keeping those two
/// sections authoritative in one place avoids divergent bindings/instructions.
/// </summary>
public sealed class ApplicationSettingsJsonCodec : IJsonSettingsCodec<ApplicationSettings>
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "models",
        "interaction",
        "voice",
        "browser",
        "privacy",
        "startup",
        "appearance",
        "diagnostics",
    };

    public ApplicationSettings CreateDefault() => ApplicationSettings.CreateDefault();

    public JsonObject Encode(ApplicationSettings value, JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        if (!SettingsValidator.Validate(value).IsValid)
        {
            throw new InvalidOperationException("Application settings validation failed before persistence.");
        }

        return new JsonObject
        {
            ["schemaVersion"] = value.SchemaVersion,
            ["models"] = new JsonObject
            {
                ["qualityProfile"] = (int)value.Models.QualityProfile,
                ["responsesModel"] = value.Models.ResponsesModel.Value,
                ["realtimeModel"] = value.Models.RealtimeModel.Value,
                ["transcriptionModel"] = value.Models.TranscriptionModel.Value,
            },
            ["interaction"] = JsonSerializer.SerializeToNode(value.Interaction, serializerOptions),
            ["voice"] = JsonSerializer.SerializeToNode(value.Voice, serializerOptions),
            ["browser"] = JsonSerializer.SerializeToNode(value.Browser, serializerOptions),
            ["privacy"] = JsonSerializer.SerializeToNode(value.Privacy, serializerOptions),
            ["startup"] = JsonSerializer.SerializeToNode(value.Startup, serializerOptions),
            ["appearance"] = JsonSerializer.SerializeToNode(value.Appearance, serializerOptions),
            ["diagnostics"] = JsonSerializer.SerializeToNode(value.Diagnostics, serializerOptions),
        };
    }

    public SettingsDecodeResult<ApplicationSettings> Decode(
        JsonObject data,
        JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ApplicationSettings current = CreateDefault();
        var reset = new List<string>();
        bool recovered = data.Any(property => !KnownProperties.Contains(property.Key));

        foreach ((string name, JsonNode? node) in data)
        {
            if (!KnownProperties.Contains(name))
            {
                continue;
            }

            try
            {
                ApplicationSettings candidate = name switch
                {
                    "schemaVersion" => current with
                    {
                        SchemaVersion = ReadRequired<int>(node, name),
                    },
                    "models" => current with { Models = DecodeModels(node, serializerOptions) },
                    "interaction" => current with { Interaction = Deserialize<InteractionSettings>(node, serializerOptions, name) },
                    "voice" => current with { Voice = Deserialize<VoiceAudioSettings>(node, serializerOptions, name) },
                    "browser" => current with { Browser = Deserialize<BrowserAutomationSettings>(node, serializerOptions, name) },
                    "privacy" => current with { Privacy = Deserialize<PrivacySafetySettings>(node, serializerOptions, name) },
                    "startup" => current with { Startup = Deserialize<StartupSystemSettings>(node, serializerOptions, name) },
                    "appearance" => current with { Appearance = Deserialize<AppearanceFeedbackSettings>(node, serializerOptions, name) },
                    "diagnostics" => current with { Diagnostics = Deserialize<DiagnosticsSettings>(node, serializerOptions, name) },
                    _ => current,
                };
                if (!SettingsValidator.Validate(candidate).IsValid)
                {
                    reset.Add(name);
                    recovered = true;
                    continue;
                }

                current = candidate;
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or InvalidOperationException)
            {
                reset.Add(name);
                recovered = true;
            }
        }

        return new SettingsDecodeResult<ApplicationSettings>(current, recovered, reset.AsReadOnly());
    }

    private static ModelSelectionSettings DecodeModels(
        JsonNode? node,
        JsonSerializerOptions serializerOptions)
    {
        _ = serializerOptions;
        JsonObject models = node as JsonObject
            ?? throw new JsonException("The model settings are invalid.");
        return new ModelSelectionSettings(
            (QualityProfile)ReadRequired<int>(models["qualityProfile"], "qualityProfile"),
            new ModelIdentifier(ReadRequired<string>(models["responsesModel"], "responsesModel")),
            new ModelIdentifier(ReadRequired<string>(models["realtimeModel"], "realtimeModel")),
            new ModelIdentifier(ReadRequired<string>(models["transcriptionModel"], "transcriptionModel")));
    }

    private static T Deserialize<T>(
        JsonNode? node,
        JsonSerializerOptions serializerOptions,
        string propertyName)
        where T : class =>
        node?.Deserialize<T>(serializerOptions)
        ?? throw new JsonException($"The '{propertyName}' settings are invalid.");

    private static T ReadRequired<T>(JsonNode? node, string propertyName)
    {
        if (node is not JsonValue value || !value.TryGetValue(out T? result) || result is null)
        {
            throw new JsonException($"The '{propertyName}' setting is missing or invalid.");
        }

        return result;
    }
}
