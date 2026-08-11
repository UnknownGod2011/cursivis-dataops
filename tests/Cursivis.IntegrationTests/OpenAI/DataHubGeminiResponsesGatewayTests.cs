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
    public void GetEligibleWritebackContent_ExtractsExactSmartResultContentOnly()
    {
        const string smartResult = """
            {"schemaVersion":1,"finalContent":"Reviewed correction\nwith exact spacing"}
            """;
        const string guidedOptions = """
            {"schemaVersion":1,"options":[{"id":"explain","label":"Explain","instruction":"Explain it"}]}
            """;

        Assert.Equal(
            "Reviewed correction\nwith exact spacing",
            DataHubGeminiResponsesGateway.GetEligibleWritebackContent(smartResult));
        Assert.Null(DataHubGeminiResponsesGateway.GetEligibleWritebackContent(guidedOptions));
        Assert.Null(DataHubGeminiResponsesGateway.GetEligibleWritebackContent("not json"));
    }

    [Fact]
    public void GroundedWritebackEligibility_ExistsOnlyAfterSuccessfulGroundedSmartResult()
    {
        var gateway = new DataHubGeminiResponsesGateway();
        const string datasetUrn = "urn:li:dataset:(urn:li:dataPlatform:demo,analytics.customers,PROD)";
        const string reviewed = "Reviewed correction";

        gateway.ApplyGroundingOutcome(true, datasetUrn, "analytics.customers", reviewed);
        Assert.True(gateway.HasGroundedDataset);

        gateway.ApplyGroundingOutcome(false, datasetUrn, "analytics.customers", reviewed);
        Assert.False(gateway.HasGroundedDataset);

        gateway.ApplyGroundingOutcome(true, null, "analytics.customers", reviewed);
        Assert.False(gateway.HasGroundedDataset);

        gateway.ApplyGroundingOutcome(true, datasetUrn, "analytics.customers", null);
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

    [Fact]
    public async Task SaveResolutionAsync_RejectsStaleResultBeforeAnyMutation()
    {
        var gateway = new DataHubGeminiResponsesGateway();
        const string datasetUrn = "urn:li:dataset:(urn:li:dataPlatform:demo,analytics.customers,PROD)";
        gateway.ApplyGroundingOutcome(
            true,
            datasetUrn,
            "analytics.customers",
            "Newest grounded result");

        DataHubResolutionSaveResult result = await gateway.SaveResolutionAsync("Older displayed result");

        Assert.False(result.IsSuccess);
        Assert.Null(result.DocumentUrn);
        Assert.Contains("no longer the exact Gemini result", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
