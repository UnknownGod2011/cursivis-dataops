using Cursivis.Infrastructure.OpenAI;

namespace Cursivis.IntegrationTests.OpenAI;

public sealed class DataHubConfirmedWritebackTests
{
    [Theory]
    [InlineData(" Reviewed correction")]
    [InlineData("Reviewed correction ")]
    [InlineData("\nReviewed correction")]
    [InlineData("Reviewed correction\r\n")]
    public async Task SaveConfirmedResolutionAsync_RejectsContentThatWouldBeNormalized(string reviewedContent)
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveConfirmedResolutionAsync(reviewedContent);

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("will not modify confirmed content", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveConfirmedResolutionAsync_PreservesAlreadyExactContentForNormalValidation()
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveConfirmedResolutionAsync("Reviewed correction");

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("grounded dataset", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
