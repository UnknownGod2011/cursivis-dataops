using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cursivis.Contracts.OpenAI;
using Cursivis.Infrastructure.Storage.Security;
using Cursivis.Windows.Platform.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App.Pages;

public sealed partial class OpenAiPage : Page
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly WindowsGeminiCredentialManager _geminiCredentials =
        WindowsGeminiCredentialStore.CreateManager();

    public OpenAiPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        GeminiKeyInput.IsSecureStorageAvailable = OperatingSystem.IsWindows();
        GeminiKeyInput.HasSavedKey = _geminiCredentials.HasSavedKey ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

        DataOpsConnectionSettings saved = WindowsGeminiCredentialStore.LoadConnectionSettings();
        GeminiModelInput.Text = Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim() is { Length: > 0 } model
            ? model
            : saved.GeminiModel;
        DataHubEndpointInput.Text = Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")?.Trim() is { Length: > 0 } endpoint
            ? endpoint
            : saved.DataHubGraphQlUrl;

        ShowConnection(
            "Gemini + DataHub MCP",
            GeminiKeyInput.HasSavedKey
                ? "Gemini is configured. Test Gemini and DataHub before running the MCP-grounded SQL workflow."
                : "Save a Gemini API key, then test Gemini and DataHub before running the MCP-grounded SQL workflow.",
            InfoBarSeverity.Informational);
    }

    private async void OnSaveRequested(object? sender, EventArgs args)
    {
        string replacement = GeminiKeyInput.TakeReplacementKey();
        char[] characters = replacement.ToCharArray();
        replacement = string.Empty;
        try
        {
            if (characters.Length == 0)
            {
                ShowConnection("Gemini key not saved", "Enter a Gemini API key first.", InfoBarSeverity.Error);
                return;
            }

            using var secret = new SecretBuffer(characters);
            await _geminiCredentials.SaveAsync(secret);
            GeminiKeyInput.HasSavedKey = true;
            ShowConnection(
                "Gemini key saved",
                "The key is protected for the current Windows user and is available to the Cursivis DataOps provider.",
                InfoBarSeverity.Success);
        }
        catch (SecretStoreException)
        {
            ShowStorageFailure();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }

    private async void OnDeleteRequested(object? sender, EventArgs args)
    {
        try
        {
            _ = await _geminiCredentials.DeleteAsync();
            GeminiKeyInput.HasSavedKey = false;
            GeminiKeyInput.ClearReplacementKey();
            ShowConnection(
                "Gemini key removed",
                "The securely saved Gemini credential was removed for this Windows user.",
                InfoBarSeverity.Informational);
        }
        catch (SecretStoreException)
        {
            ShowStorageFailure();
        }
    }

    private async void OnTestRequested(object? sender, EventArgs args)
    {
        if (App.CurrentRuntime is not { } runtime)
        {
            ShowConnection("Gemini test unavailable", "Cursivis is still initializing.", InfoBarSeverity.Error);
            return;
        }

        _ = await WindowsGeminiCredentialStore.LoadIntoProcessEnvironmentAsync();
        if (!TryApplyConnectionSettings(out DataOpsConnectionSettings settings))
        {
            return;
        }

        ShowConnection(
            "Testing Gemini",
            $"Checking {settings.GeminiModel} with the configured Gemini credential...",
            InfoBarSeverity.Informational);
        ModelAvailabilityResult availability = await runtime.ResponsesGateway
            .CheckModelAvailabilityAsync(settings.GeminiModel);
        if (availability.Available)
        {
            ShowConnection(
                "Gemini connected",
                $"{settings.GeminiModel} accepted a structured-output request. The reasoning provider is ready.",
                InfoBarSeverity.Success);
            return;
        }

        ShowConnection(
            "Gemini test failed",
            GetFailureMessage(availability.Failure?.Kind),
            InfoBarSeverity.Error);
    }

    private async void OnSaveConnectionSettingsClicked(object sender, RoutedEventArgs args)
    {
        if (!TryApplyConnectionSettings(out DataOpsConnectionSettings settings))
        {
            return;
        }

        try
        {
            await WindowsGeminiCredentialStore.SaveConnectionSettingsAsync(settings);
            Environment.SetEnvironmentVariable("GEMINI_MODEL", settings.GeminiModel, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("DATAHUB_GRAPHQL_URL", settings.DataHubGraphQlUrl, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("DATAHUB_GMS_URL", settings.DataHubGmsUrl, EnvironmentVariableTarget.Process);
            ShowConnection(
                "Connection settings saved",
                $"Gemini model and DataHub endpoint are stored for this Windows user. The DataHub MCP Server will target {settings.DataHubGmsUrl}.",
                InfoBarSeverity.Success);
        }
        catch (IOException)
        {
            ShowConnection("Connection settings not saved", "Cursivis could not write the DataOps connection settings file.", InfoBarSeverity.Error);
        }
        catch (UnauthorizedAccessException)
        {
            ShowConnection("Connection settings not saved", "Windows denied access to the Cursivis settings directory.", InfoBarSeverity.Error);
        }
    }

    private bool TryApplyConnectionSettings(out DataOpsConnectionSettings settings)
    {
        string model = GeminiModelInput.Text.Trim();
        string endpoint = DataHubEndpointInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            settings = DataOpsConnectionSettings.Default;
            ShowConnection("Model is required", "Enter a Gemini model ID.", InfoBarSeverity.Error);
            return false;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            settings = DataOpsConnectionSettings.Default;
            ShowConnection(
                "DataHub endpoint is invalid",
                "Enter an absolute http:// or https:// GraphQL endpoint.",
                InfoBarSeverity.Error);
            return false;
        }

        settings = new DataOpsConnectionSettings(model, endpointUri.ToString()).Normalize();
        Environment.SetEnvironmentVariable("GEMINI_MODEL", settings.GeminiModel, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("DATAHUB_GRAPHQL_URL", settings.DataHubGraphQlUrl, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("DATAHUB_GMS_URL", settings.DataHubGmsUrl, EnvironmentVariableTarget.Process);
        return true;
    }

    private async void OnTestDataHubClicked(object sender, RoutedEventArgs args)
    {
        if (!TryApplyConnectionSettings(out DataOpsConnectionSettings settings))
        {
            return;
        }

        ShowConnection("Testing DataHub", $"Checking {settings.DataHubGraphQlUrl}...", InfoBarSeverity.Informational);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.DataHubGraphQlUrl);
            string? token = Environment.GetEnvironmentVariable("DATAHUB_TOKEN")?.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            request.Content = new StringContent(
                JsonSerializer.Serialize(new { query = "query CursivisDataHubHealth { __typename }" }),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                ShowConnection(
                    "DataHub test failed",
                    $"DataHub returned HTTP {(int)response.StatusCode}. Check the endpoint and token.",
                    InfoBarSeverity.Error);
                return;
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out JsonElement errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                ShowConnection(
                    "DataHub test failed",
                    "DataHub responded, but GraphQL rejected the health query. Check endpoint permissions.",
                    InfoBarSeverity.Error);
                return;
            }

            ShowConnection(
                "DataHub connected",
                $"The catalog endpoint is reachable. The official MCP runtime will target {settings.DataHubGmsUrl}; seed and verify the demo catalog before recording.",
                InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowConnection("DataHub test timed out", "DataHub did not respond within the connection timeout.", InfoBarSeverity.Error);
        }
        catch (HttpRequestException)
        {
            ShowConnection("DataHub test failed", "Cursivis could not reach the configured DataHub endpoint.", InfoBarSeverity.Error);
        }
        catch (JsonException)
        {
            ShowConnection("DataHub test failed", "DataHub returned an unexpected response.", InfoBarSeverity.Error);
        }
    }

    private void ShowStorageFailure() => ShowConnection(
        "Secure storage unavailable",
        "Cursivis could not update the protected Gemini credential for this Windows user.",
        InfoBarSeverity.Error);

    private void ShowConnection(string title, string message, InfoBarSeverity severity)
    {
        ConnectionInfo.Title = title;
        ConnectionInfo.Message = message;
        ConnectionInfo.Severity = severity;
        ConnectionInfo.IsOpen = true;
    }

    private static string GetFailureMessage(OpenAiFailureKind? kind) => kind switch
    {
        OpenAiFailureKind.Authentication => "Gemini rejected the API key. Check the configured credential.",
        OpenAiFailureKind.Permission => "Gemini accepted the key but denied access to the configured model.",
        OpenAiFailureKind.ModelUnavailable => "The configured Gemini model is unavailable. Verify GEMINI_MODEL.",
        OpenAiFailureKind.Quota => "The Gemini project has no available quota for this request.",
        OpenAiFailureKind.RateLimit => "Gemini is temporarily rate limited. Try again after the provider cooldown.",
        OpenAiFailureKind.Network => "Cursivis could not reach Gemini.",
        OpenAiFailureKind.Timeout => "Gemini did not respond before the request timeout.",
        OpenAiFailureKind.MalformedResponse => "Gemini returned an invalid structured response.",
        _ => "Gemini could not be verified. Check the key, model, and network connection.",
    };
}
