using System.Text.Json;
using Cursivis.Infrastructure.OpenAI;

namespace Cursivis.IntegrationTests;

public sealed class DataHubMcpContractTests
{
    [Fact]
    public void DatasetExtractionFindsFromAndJoinReferencesWithoutDuplicates()
    {
        const string sql = """
            SELECT c.customer_id
            FROM `analytics.customers` c
            JOIN [raw.customers] r ON r.customer_id = c.customer_id
            JOIN analytics.customers again ON again.customer_id = c.customer_id;
            """;

        IReadOnlyList<string> references = DataHubGeminiResponsesGateway.ExtractDatasetReferences(sql);

        Assert.Equal(2, references.Count);
        Assert.Equal("analytics.customers", references[0]);
        Assert.Equal("raw.customers", references[1]);
    }

    [Fact]
    public void DatasetExtractionHandlesQuotedQualifiedName()
    {
        const string sql = "SELECT * FROM \"analytics.customer_360\" WHERE customer_id IS NOT NULL;";

        IReadOnlyList<string> references = DataHubGeminiResponsesGateway.ExtractDatasetReferences(sql);

        Assert.Single(references);
        Assert.Equal("analytics.customer_360", references[0]);
    }

    [Fact]
    public void McpRuntimeDefaultsToPinnedOfficialRelease()
    {
        Assert.Equal("mcp-server-datahub@0.6.0", DataHubMcpClient.DefaultPackage);
    }

    [Fact]
    public void McpRuntimeBoundsEveryRequest()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), DataHubMcpClient.RequestTimeout);
    }

    [Fact]
    public void McpToolResultPrefersStructuredContent()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "content": [{"type":"text","text":"fallback"}],
              "structuredContent": {"urn":"urn:li:dataset:(urn:li:dataPlatform:demo,analytics.customers,PROD)"}
            }
            """);

        string result = DataHubMcpClient.GetToolResultText(document.RootElement);

        Assert.Contains("analytics.customers", result, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", result, StringComparison.Ordinal);
    }

    [Fact]
    public void McpToolResultCombinesTextContentWhenStructuredContentIsAbsent()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "content": [
                {"type":"text","text":"schema: customer_id"},
                {"type":"text","text":"owner: data-platform"}
              ]
            }
            """);

        string result = DataHubMcpClient.GetToolResultText(document.RootElement);

        Assert.Contains("schema: customer_id", result, StringComparison.Ordinal);
        Assert.Contains("owner: data-platform", result, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceVerificationRecognizesJsonEscapedReviewedContentAndAsset()
    {
        const string documentUrn = "urn:li:document:cursivis-test";
        const string title = "Cursivis resolution — analytics.customers";
        const string content = "Line one\nLine two with \"quoted\" evidence";
        const string datasetUrn = "urn:li:dataset:(urn:li:dataPlatform:demo,analytics.customers,PROD)";
        string verified = JsonSerializer.Serialize(new
        {
            urn = documentUrn,
            title,
            contents = new { text = content },
            relatedAssets = new[] { datasetUrn },
        });

        Assert.True(DataHubGeminiResponsesGateway.ContainsPersistedValue(verified, documentUrn));
        Assert.True(DataHubGeminiResponsesGateway.ContainsPersistedValue(verified, title));
        Assert.True(DataHubGeminiResponsesGateway.ContainsPersistedValue(verified, content));
        Assert.True(DataHubGeminiResponsesGateway.ContainsPersistedValue(verified, datasetUrn));
        Assert.False(DataHubGeminiResponsesGateway.ContainsPersistedValue(verified, "different reviewed content"));
    }

    [Fact]
    public async Task SaveResolutionRequiresGroundedDatasetBeforeAnyMutation()
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync("Reviewed resolution");

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("grounded dataset", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveResolutionRejectsEmptyContentBeforeAnyMutation()
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Contains("no reviewed resolution", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
