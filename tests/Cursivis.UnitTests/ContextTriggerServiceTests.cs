using Cursivis.Application.Context;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.UnitTests;

public sealed class ContextTriggerServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_ValidStructuredResult_ReturnsTypedSmartResult()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.91));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));

        ContextTriggerExecutionResult result = await service.ExecuteAsync(
            CreateInput(),
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Equal("A concise reply.", result.Result?.FinalContent);
        Assert.Equal("Drafting reply", result.Result?.OperationLabel);
        Assert.Equal("gemini-2.5-flash", gateway.LastRequest?.Model);
        Assert.Equal("cursivis_smart_result", gateway.LastRequest?.SchemaName);
        Assert.Contains("additionalProperties", gateway.LastRequest?.JsonSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_LowConfidence_RequestsGuidedModeWithoutDiscardingResult()
    {
        var service = new ContextTriggerService(
            new StubResponsesGateway(SuccessJson(confidence: 0.35)),
            new FixedTimeProvider(Now));

        ContextTriggerExecutionResult result = await service.ExecuteAsync(
            CreateInput(),
            ModelCatalog.Economy,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.NeedsGuidedMode, result.Status);
        Assert.NotNull(result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedStructuredResult_ReturnsSafeFailure()
    {
        var service = new ContextTriggerService(
            new StubResponsesGateway("{\"schemaVersion\":1,\"unexpected\":true}"),
            new FixedTimeProvider(Now));

        ContextTriggerExecutionResult result = await service.ExecuteAsync(
            CreateInput(),
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.MalformedResponse, result.Status);
        Assert.Equal(OpenAiFailureKind.MalformedResponse, result.Failure?.Kind);
        Assert.Null(result.Result);
    }

    [Fact]
    public async Task ExecuteAsync_ProviderFailure_PreservesClassifiedSafeFailure()
    {
        var failure = new OpenAiFailure(OpenAiFailureKind.RateLimit, "Rate limited.", true);
        var service = new ContextTriggerService(
            new StubResponsesGateway(failure),
            new FixedTimeProvider(Now));

        ContextTriggerExecutionResult result = await service.ExecuteAsync(
            CreateInput(),
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.ProviderFailed, result.Status);
        Assert.Same(failure, result.Failure);
    }

    [Fact]
    public async Task ExecuteGuidedAsync_ValidOperation_UsesSameContextFingerprintWithoutConfidenceFallback()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.20));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        ContextSnapshot context = CreateContext();

        ContextTriggerExecutionResult result = await service.ExecuteGuidedAsync(
            ContextExecutionInput.FromText(context),
            GuidedOperation.Summarize,
            customInstruction: null,
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Equal(context.Fingerprint.Value, gateway.LastRequest?.OperationId);
        Assert.Contains("Requested operation: Summarize", gateway.LastRequest?.UserContent, StringComparison.Ordinal);
        Assert.Contains(context.Text!, gateway.LastRequest?.UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteGuidedAsync_CustomTaskWithoutInstruction_RejectsRequestBeforeGateway()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.90));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteGuidedAsync(
            CreateInput(),
            GuidedOperation.CustomTask,
            " ",
            ModelCatalog.Balanced,
            CancellationToken.None));

        Assert.Null(gateway.LastRequest);
    }

    [Fact]
    public async Task ExecuteAsync_ImageContext_UsesTypedResponsesVisionInput()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.90));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];
        ContextExecutionInput input = CreateImageInput(png);

        ContextTriggerExecutionResult result = await service.ExecuteAsync(
            input,
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Equal("image/png", gateway.LastRequest?.Image?.MediaType);
        Assert.True(gateway.LastRequest?.Image?.EncodedBytes.Span.SequenceEqual(png));
        Assert.Contains("captured image", gateway.LastRequest?.UserContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteGuidedAsync_ImageContext_ReusesFingerprintAndImagePayload()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.90));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 8, 7, 6, 5];
        ContextExecutionInput input = CreateImageInput(png);

        ContextTriggerExecutionResult result = await service.ExecuteGuidedAsync(
            input,
            GuidedOperation.ExtractText,
            customInstruction: null,
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Equal(input.Context.Fingerprint.Value, gateway.LastRequest?.OperationId);
        Assert.NotNull(input.Image);
        Assert.True(gateway.LastRequest?.Image?.EncodedBytes.Span.SequenceEqual(png));
        Assert.Contains("attached user-captured image", gateway.LastRequest?.UserContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateGuidedOptionsAsync_UsesTypedContextSpecificOptions()
    {
        var gateway = new StubResponsesGateway(GuidedOptionsJson());
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));

        GuidedOptionsExecutionResult result = await service.GenerateGuidedOptionsAsync(
            CreateInput(),
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Equal(3, result.Options?.Count);
        Assert.Equal("draft_reply", result.Options?[0].Id);
        Assert.Contains("distinct, high-value", gateway.LastRequest?.SystemInstruction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cursivis_guided_options", gateway.LastRequest?.SchemaName);
        Assert.Equal(CreateContext().Fingerprint.Value, gateway.LastRequest?.OperationId);
    }

    [Fact]
    public async Task ExecuteGuidedInstructionAsync_ReusesContextAndOptionInstruction()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.95));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        ContextExecutionInput input = CreateInput();
        var option = new GuidedOption(
            "draft_reply",
            "Draft reply",
            "Draft a concise reply that answers the message and preserves its requested tone.");

        ContextTriggerExecutionResult result = await service.ExecuteGuidedInstructionAsync(
            input,
            option,
            ModelCatalog.Balanced,
            CancellationToken.None);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, result.Status);
        Assert.Contains("Draft reply", gateway.LastRequest?.UserContent, StringComparison.Ordinal);
        Assert.Contains(option.Instruction, gateway.LastRequest?.UserContent, StringComparison.Ordinal);
        Assert.Equal(input.Context.Fingerprint.Value, gateway.LastRequest?.OperationId);
    }

    [Fact]
    public void FromImage_MismatchedPayload_RejectsBeforeModelRequest()
    {
        ContextExecutionInput valid = CreateImageInput([1, 2, 3, 4]);
        var mismatched = new ContextImagePayload([4, 3, 2, 1], "image/png", 1, 1);

        Assert.Throws<ArgumentException>(() => ContextExecutionInput.FromImage(valid.Context, mismatched));
    }

    [Fact]
    public async Task ExecuteQuickTaskAsync_ApprovedDefinition_UsesSameStandardResponsesPath()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.95));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        var definition = new QuickTaskDefinition(
            new QuickTaskId("fixture-task"),
            "Fixture Task",
            "Rewrite the selected context clearly while preserving every fact and stated requirement.",
            QuickTaskContextType.Text,
            QuickTaskOutputMode.ReplacementText,
            mayProposeAction: false,
            isExplicitlyApproved: true);

        ContextTriggerExecutionResult execution = await service.ExecuteQuickTaskAsync(
            CreateInput(),
            definition,
            ModelCatalog.Balanced);

        Assert.Equal(ContextTriggerExecutionStatus.Completed, execution.Status);
        Assert.Contains("approved_quick_task_instruction", gateway.LastRequest?.SystemInstruction, StringComparison.Ordinal);
        Assert.Null(gateway.LastRequest?.Image);
        Assert.Equal(SuggestedActionType.None, execution.Result?.SuggestedAction.Type);
    }

    [Fact]
    public async Task ExecuteQuickTaskAsync_UnsupportedContext_RejectsBeforeProviderCall()
    {
        var gateway = new StubResponsesGateway(SuccessJson(confidence: 0.95));
        var service = new ContextTriggerService(gateway, new FixedTimeProvider(Now));
        var definition = new QuickTaskDefinition(
            new QuickTaskId("image-only"),
            "Image only",
            "Describe only the explicit image capture accurately and return a concise visual analysis.",
            QuickTaskContextType.Image,
            QuickTaskOutputMode.Analysis,
            mayProposeAction: false,
            isExplicitlyApproved: true);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteQuickTaskAsync(
            CreateInput(),
            definition,
            ModelCatalog.Balanced));

        Assert.Null(gateway.LastRequest);
    }

    private static ContextSnapshot CreateContext() => ContextSnapshot.FromText(
        ContextKind.Text,
        ContextSource.UserInterfaceAutomation,
        new TargetIdentity("notepad", "00000001"),
        "Please reply to this email.",
        Now,
        TimeSpan.FromMinutes(5));

    private static ContextExecutionInput CreateInput() => ContextExecutionInput.FromText(CreateContext());

    private static ContextExecutionInput CreateImageInput(byte[] bytes)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(bytes);
        ContextSnapshot context = ContextSnapshot.FromImageDigest(
            ContextSource.RegionCapture,
            new TargetIdentity("desktop", "00000001"),
            digest,
            Now,
            TimeSpan.FromMinutes(5));
        return ContextExecutionInput.FromImage(
            context,
            new ContextImagePayload(bytes, "image/png", 1, 1));
    }

    private static string SuccessJson(double confidence) => $$"""
        {
          "schemaVersion": 1,
          "contextType": "email",
          "intent": "reply",
          "operationLabel": "Drafting reply",
          "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "finalContent": "A concise reply.",
          "suggestedAction": {
            "type": "replace_selection",
            "summary": "Replace the selected draft"
          },
          "riskHint": "low",
          "confirmationHint": "none"
        }
        """;

    private static string GuidedOptionsJson() => """
        {
          "schemaVersion": 1,
          "options": [
            {
              "id": "draft_reply",
              "label": "Draft reply",
              "instruction": "Draft a concise reply that answers the message and preserves its requested tone."
            },
            {
              "id": "summarize_request",
              "label": "Summarize request",
              "instruction": "Summarize the requested actions, deadline, and relevant constraints in a compact list."
            },
            {
              "id": "make_professional",
              "label": "Make professional",
              "instruction": "Rewrite the message in a concise professional tone while preserving its meaning."
            }
          ]
        }
        """;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubResponsesGateway : IResponsesGateway
    {
        private readonly StructuredResponseResult _result;

        public StubResponsesGateway(string json)
            : this(StructuredResponseResult.Success(json, ModelCatalog.Balanced.Value, "response-1"))
        {
        }

        public StubResponsesGateway(OpenAiFailure failure)
            : this(StructuredResponseResult.Failed(failure))
        {
        }

        private StubResponsesGateway(StructuredResponseResult result)
        {
            _result = result;
        }

        public StructuredResponseRequest? LastRequest { get; private set; }

        public Task<StructuredResponseResult> CreateStructuredResponseAsync(
            StructuredResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_result);
        }

        public Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelAvailabilityResult(model, true, null, Now));
    }
}
