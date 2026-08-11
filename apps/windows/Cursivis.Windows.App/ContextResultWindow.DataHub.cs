using Cursivis.Infrastructure.OpenAI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App;

public sealed partial class ContextResultWindow
{
    private const int MaximumDataHubResolutionCharacters = 24_000;
    private bool _dataHubSaveArmed;
    private string? _dataHubSaveArmedContent;

    private async void OnSaveToDataHubClicked(object sender, RoutedEventArgs args)
    {
        if (App.CurrentRuntime?.ResponsesGateway is not DataHubGeminiResponsesGateway gateway)
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "DataHub save unavailable",
                "The active reasoning provider does not support DataHub resolution write-back.",
                InfoBarSeverity.Error);
            return;
        }

        string reviewedContent = _presentation.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reviewedContent))
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "Nothing to save",
                "Generate and review a grounded result before saving organizational knowledge.",
                InfoBarSeverity.Error);
            return;
        }

        // Never ask the user to confirm one artifact and then persist a different,
        // silently truncated artifact. The MCP write path has a bounded document
        // size, so an oversized reviewed result must fail visibly before the
        // confirmation is armed and before any mutation process is started.
        if (reviewedContent.Length > MaximumDataHubResolutionCharacters)
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "Resolution too large to save exactly",
                $"This reviewed result is {reviewedContent.Length:N0} characters. DataHub write-back is limited to {MaximumDataHubResolutionCharacters:N0} characters, so Cursivis will not truncate or publish it. Shorten the reviewed result and try again.",
                InfoBarSeverity.Error);
            return;
        }

        if (!gateway.HasGroundedDataset)
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "Grounded dataset required",
                "This result is not associated with a resolved DataHub dataset, so Cursivis will not write it to the catalog.",
                InfoBarSeverity.Error);
            return;
        }

        // The first click only arms the mutation. A second click on the same
        // reviewed result is required before any DataHub MCP write is attempted.
        if (!_dataHubSaveArmed ||
            !string.Equals(_dataHubSaveArmedContent, reviewedContent, StringComparison.Ordinal))
        {
            _dataHubSaveArmed = true;
            _dataHubSaveArmedContent = reviewedContent;
            SaveToDataHubButton.Content = "Confirm Save";
            ShowNotice(
                "Confirm DataHub MCP write",
                "Click Confirm Save to publish this reviewed resolution as a DataHub knowledge document linked to the grounded dataset. No write occurs before confirmation.",
                InfoBarSeverity.Informational);
            return;
        }

        _dataHubSaveArmed = false;
        _dataHubSaveArmedContent = null;
        SaveToDataHubButton.Content = "Saving…";
        SaveToDataHubButton.IsEnabled = false;

        try
        {
            DataHubResolutionSaveResult result = await gateway.SaveConfirmedResolutionAsync(reviewedContent);
            if (result.IsSuccess)
            {
                SaveToDataHubButton.Content = "Saved";
                ShowNotice(
                    "Saved and verified through DataHub MCP",
                    result.Message,
                    InfoBarSeverity.Success);
                return;
            }

            ResetDataHubSaveButton();
            ShowNotice(
                "DataHub MCP save failed",
                result.Message,
                InfoBarSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "DataHub save cancelled",
                "No reviewed resolution was confirmed as saved.",
                InfoBarSeverity.Error);
        }
        catch (Exception)
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "DataHub save failed safely",
                "Cursivis could not verify the DataHub MCP write. No success is being claimed.",
                InfoBarSeverity.Error);
        }
    }

    private void ResetDataHubSaveButton(bool isEnabled = true)
    {
        _dataHubSaveArmed = false;
        _dataHubSaveArmedContent = null;
        SaveToDataHubButton.Content = "Save to DataHub";
        SaveToDataHubButton.IsEnabled = isEnabled;
    }
}
