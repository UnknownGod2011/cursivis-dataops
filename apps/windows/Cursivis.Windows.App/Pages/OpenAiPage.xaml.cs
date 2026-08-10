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

        GeminiModelInput.Text = Environment.GetEnvironmentVariable("GEMINI_MODEL")?.Trim() is { Length: > 0 } model
            ? model
            : "gemini-2.5-flash";
        DataHubEndpointInput.Text = Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL")?.Trim() is { Length: > 0 } endpoint
            ? endpoint
            : "http://localhost:8080/api/graphql";

        ShowConnection(
            "Gemini + DataHub",
            GeminiKeyInput.HasSavedKey
                ? "Gemini is configured. Test Gemini and DataHub before running the grounded SQL workflow."
                : "Save a Gemini API key, then test Gemini and DataHub before running the grounded SQL workflow.",
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
        ApplyConnectionSettings(showSuccess: false);
        string model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-2.5-flash";

        ShowConnection("Testing Gemini", $"Checking {model} with the configured Gemini credential...", InfoBarSeverity.Informational);
        ModelAvailabilityResult availability = await runtime.ResponsesGateway.CheckModelAvailabilityAsync(model);
        if (availability.Available)
        {
            ShowConnection(
                "Gemini connected",
                $"{model} accepted a structured-output request. The reasoning provider is ready.",
                InfoBarSeverity.Success);
            return;
        }

        ShowConnection(
            "Gemini test failed",
            GetFailureMessage(availability.Failure?.Kind),
            InfoBarSeverity.Error);
    }

    private void OnSaveConnectionSettingsClicked(object sender, RoutedEventArgs args) =>
        ApplyConnectionSettings(showSuccess: true);

    private void ApplyConnectionSettings(bool showSuccess)
    {
        string model = GeminiModelInput.Text.Trim();
        string endpoint = DataHubEndpointInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowConnection("Model is required", "Enter a Gemini model ID.", InfoBarSeverity.Error);
            return;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) ||
            endpointUri.Scheme is not ("http" or "https"))
        {
            ShowConnection(
                "DataHub endpoint is invalid",
                "Enter an absolute http:// or https:// GraphQL endpoint.",
                InfoBarSeverity.Error);
            return;
        }

        Environment.SetEnvironmentVariable("GEMINI_MODEL", model, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("DATAHUB_GRAPHQL_URL", endpointUri.ToString(), EnvironmentVariableTarget.Process);
        if (showSuccess)
        {
            ShowConnection(
                "Connection settings applied",
                "Gemini model and DataHub endpoint are active for this Cursivis session. Environment variables supplied at launch remain the reproducible setup path.",
                InfoBarSeverity.Success);
        }
    }

    private async void OnTestDataHubClicked(object sender, RoutedEventArgs args)
    {
        ApplyConnectionSettings(showSuccess: false);
        string? endpoint = Environment.GetEnvironmentVariable("DATAHUB_GRAPHQL_URL");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowConnection("DataHub test failed", "Configure the DataHub GraphQL endpoint first.", InfoBarSeverity.Error);
            return;
        }

        ShowConnection("Testing DataHub", $"Checking {endpoint}...", InfoBarSeverity.Informational);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
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
                "The GraphQL endpoint is reachable. Seed and verify the demo catalog before recording the grounded workflow.",
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
