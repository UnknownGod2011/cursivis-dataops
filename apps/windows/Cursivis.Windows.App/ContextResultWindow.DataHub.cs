using Cursivis.Infrastructure.OpenAI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Cursivis.Windows.App;

public sealed partial class ContextResultWindow
{
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

        string reviewedContent = _presentation.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reviewedContent))
        {
            ResetDataHubSaveButton();
            ShowNotice(
                "Nothing to save",
                "Generate and review a grounded result before saving organizational knowledge.",
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
        // reviewed result is required before any DataHub write is attempted.
        if (!_dataHubSaveArmed ||
            !string.Equals(_dataHubSaveArmedContent, reviewedContent, StringComparison.Ordinal))
        {
            _dataHubSaveArmed = true;
            _dataHubSaveArmedContent = reviewedContent;
            SaveToDataHubButton.Content = "Confirm Save";
            ShowNotice(
                "Confirm DataHub write",
                "Click Confirm Save to store this reviewed result as a hidden context document linked to the grounded dataset.",
                InfoBarSeverity.Informational);
            return;
        }

        _dataHubSaveArmed = false;
        _dataHubSaveArmedContent = null;
        SaveToDataHubButton.Content = "Saving…";
        SaveToDataHubButton.IsEnabled = false;

        try
        {
            DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync(reviewedContent);
            if (result.IsSuccess)
            {
                SaveToDataHubButton.Content = "Saved";
                ShowNotice(
                    "Saved to DataHub",
                    result.Message,
                    InfoBarSeverity.Success);
                return;
            }

            ResetDataHubSaveButton();
            ShowNotice(
                "DataHub save failed",
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
                "Cursivis could not verify the DataHub write. No success is being claimed.",
                InfoBarSeverity.Error);
        }
    }

    private void ResetDataHubSaveButton()
    {
        _dataHubSaveArmed = false;
        _dataHubSaveArmedContent = null;
        SaveToDataHubButton.Content = "Save to DataHub";
        SaveToDataHubButton.IsEnabled = true;
    }
}
