using Cursivis.Infrastructure.OpenAI;

namespace Cursivis.IntegrationTests.OpenAI;

public sealed class DataHubGeminiResponsesGatewayTests
{
    [Fact]
    public void ExtractDatasetReferences_FindsFromAndJoinInOrder()
    {
        const string sql = """
            SELECT c.customer_id, o.order_id
            FROM analytics.customers AS c
            JOIN warehouse.orders AS o ON o.customer_id = c.customer_id
            """;

        IReadOnlyList<string> datasets = DataHubGeminiResponsesGateway.ExtractDatasetReferences(sql);

        Assert.Equal(new[] { "analytics.customers", "warehouse.orders" }, datasets);
    }

    [Fact]
    public void ExtractDatasetReferences_NormalizesCommonQuotedIdentifiers()
    {
        const string sql = """
            SELECT *
            FROM `analytics.customers`
            JOIN [warehouse.orders] ON 1 = 1
            JOIN "finance.payments" ON 1 = 1
            """;

        IReadOnlyList<string> datasets = DataHubGeminiResponsesGateway.ExtractDatasetReferences(sql);

        Assert.Equal(
            new[] { "analytics.customers", "warehouse.orders", "finance.payments" },
            datasets);
    }

    [Fact]
    public void ExtractDatasetReferences_DeduplicatesCaseInsensitively()
    {
        const string sql = """
            SELECT *
            FROM analytics.customers c
            JOIN ANALYTICS.CUSTOMERS duplicate ON duplicate.customer_id = c.customer_id
            """;

        IReadOnlyList<string> datasets = DataHubGeminiResponsesGateway.ExtractDatasetReferences(sql);

        Assert.Single(datasets);
        Assert.Equal("analytics.customers", datasets[0]);
    }

    [Fact]
    public void ExtractDatasetReferences_ReturnsEmptyForNonDataText()
    {
        IReadOnlyList<string> datasets = DataHubGeminiResponsesGateway.ExtractDatasetReferences(
            "Explain why this deployment is failing.");

        Assert.Empty(datasets);
    }

    [Fact]
    public void GroundedWritebackEligibility_ExistsOnlyAfterSuccessfulGroundedGeneration()
    {
        var gateway = new DataHubGeminiResponsesGateway();
        const string datasetUrn = "urn:li:dataset:(urn:li:dataPlatform:demo,analytics.customers,PROD)";

        gateway.ApplyGroundingOutcome(true, datasetUrn, "analytics.customers");
        Assert.True(gateway.HasGroundedDataset);

        gateway.ApplyGroundingOutcome(false, datasetUrn, "analytics.customers");
        Assert.False(gateway.HasGroundedDataset);

        gateway.ApplyGroundingOutcome(true, null, "analytics.customers");
        Assert.False(gateway.HasGroundedDataset);
    }

    [Fact]
    public async Task SaveResolutionAsync_RejectsEmptyResolutionBeforeAnyMutation()
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("no reviewed resolution", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveResolutionAsync_RejectsOversizedResolutionBeforeAnyMutation()
    {
        var gateway = new DataHubGeminiResponsesGateway();
        string oversized = new('x', 24_001);

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync(oversized);

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("too large", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not truncate", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveResolutionAsync_RejectsWriteWithoutGroundedDataset()
    {
        var gateway = new DataHubGeminiResponsesGateway();

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync("Reviewed correction");

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("grounded dataset", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}