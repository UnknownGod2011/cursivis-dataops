using Cursivis.Application.Actions;
using Cursivis.Application.OpenAI;
using Cursivis.Contracts.Browser;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Models;

namespace Cursivis.UnitTests;

public sealed class BrowserTakeActionServiceTests
{
    [Fact]
    public async Task PlanAndExecute_SingleExplicitField_UsesExactVerifiedTarget()
    {
        var responses = new StubResponsesGateway("""
            {
              "schemaVersion": 1,
              "goal": "Set the display name",
              "needsClarification": false,
              "clarifyingQuestion": null,
              "steps": [
                { "id": "set-name", "kind": "set_value", "stableTargetId": "target-name", "value": "Ada", "checked": null }
              ]
            }
            """);
        var browser = new StubBrowserGateway();
        var service = new BrowserTakeActionService(responses, browser);

        BrowserActionPlanningResult planning = await service.PlanAsync(
            Form(),
            "Change my display name to Ada",
            ActionProposalSource.DirectUserRequest,
            isDirectUserRequest: true,
            ModelCatalog.Balanced);

        Assert.Equal(BrowserActionPlanningStatus.Ready, planning.Status);
        Assert.NotNull(planning.Proposal);
        Assert.Equal("sha256:", planning.Proposal.Context.Fingerprint.Value[..7]);
        Assert.Contains("exact stableTargetId", responses.LastRequest!.SystemInstruction, StringComparison.Ordinal);

        BrowserActionExecutionResult execution = await service.ExecuteAsync(planning.Proposal);

        Assert.Equal(BrowserActionExecutionStatus.Completed, execution.Status);
        BrowserCommand command = Assert.Single(browser.Commands);
        Assert.Equal("target-name", command.StableTargetId);
        Assert.Equal("form-fingerprint", command.ContextFingerprint);
        Assert.Equal(BrowserCommandKind.SetValue, command.Kind);
        Assert.Equal("Ada", command.Value);
    }

    [Fact]
    public async Task Plan_InventedTarget_IsRejectedBeforeExecution()
    {
        var service = new BrowserTakeActionService(
            new StubResponsesGateway("""
                {
                  "schemaVersion": 1,
                  "goal": "Fill a hidden field",
                  "needsClarification": false,
                  "clarifyingQuestion": null,
                  "steps": [
                    { "id": "bad", "kind": "set_value", "stableTargetId": "invented", "value": "x", "checked": null }
                  ]
                }
                """),
            new StubBrowserGateway());

        BrowserActionPlanningResult result = await service.PlanAsync(
            Form(),
            "Fill the form",
            ActionProposalSource.DirectUserRequest,
            true,
            ModelCatalog.Balanced);

        Assert.Equal(BrowserActionPlanningStatus.InvalidPlan, result.Status);
        Assert.Equal(OpenAiFailureKind.MalformedResponse, result.Failure?.Kind);
    }

    [Fact]
    public async Task Plan_MissingInformation_ReturnsOneClarificationWithoutSteps()
    {
        var service = new BrowserTakeActionService(
            new StubResponsesGateway("""
                {
                  "schemaVersion": 1,
                  "goal": "Complete the form",
                  "needsClarification": true,
                  "clarifyingQuestion": "What display name should I use?",
                  "steps": []
                }
                """),
            new StubBrowserGateway());

        BrowserActionPlanningResult result = await service.PlanAsync(
            Form(),
            "Complete this",
            ActionProposalSource.DirectUserRequest,
            true,
            ModelCatalog.Balanced);

        Assert.Equal(BrowserActionPlanningStatus.NeedsClarification, result.Status);
        Assert.Equal("What display name should I use?", result.ClarifyingQuestion);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task Execute_MultipleMutations_RequiresBoundOneTimeConfirmation()
    {
        var browser = new StubBrowserGateway();
        var service = new BrowserTakeActionService(
            new StubResponsesGateway("""
                {
                  "schemaVersion": 1,
                  "goal": "Update profile",
                  "needsClarification": false,
                  "clarifyingQuestion": null,
                  "steps": [
                    { "id": "set-name", "kind": "set_value", "stableTargetId": "target-name", "value": "Ada", "checked": null },
                    { "id": "set-role", "kind": "select_option", "stableTargetId": "target-role", "value": "Engineer", "checked": null }
                  ]
                }
                """),
            browser);
        BrowserActionPlanningResult planning = await service.PlanAsync(
            Form(),
            "Update this profile",
            ActionProposalSource.DirectUserRequest,
            true,
            ModelCatalog.Balanced);
        BrowserActionProposal proposal = Assert.IsType<BrowserActionProposal>(planning.Proposal);

        BrowserActionExecutionResult pending = await service.ExecuteAsync(proposal);
        Assert.Equal(BrowserActionExecutionStatus.ConfirmationRequired, pending.Status);
        Assert.Empty(browser.Commands);

        ActionConfirmation confirmation = service.CreateConfirmation(proposal);
        BrowserActionExecutionResult completed = await service.ExecuteAsync(proposal, confirmation);
        Assert.Equal(BrowserActionExecutionStatus.Completed, completed.Status);
        Assert.All(browser.Commands, command => Assert.Equal(confirmation.Id.ToString(), command.ConfirmationId));

        BrowserActionExecutionResult replay = await service.ExecuteAsync(proposal, confirmation);
        Assert.Equal(BrowserActionExecutionStatus.Rejected, replay.Status);
        Assert.Contains(replay.Policy.Reasons, reason => reason.Code == "confirmation.replayed");
    }

    [Fact]
    public async Task Execute_VerificationFailure_StopsRemainingSteps()
    {
        var browser = new StubBrowserGateway
        {
            Results =
            [
                new BrowserStepResult("set-name", false, false, null, "target_missing", "The field changed."),
            ],
        };
        var service = new BrowserTakeActionService(
            new StubResponsesGateway("""
                {
                  "schemaVersion": 1,
                  "goal": "Update profile",
                  "needsClarification": false,
                  "clarifyingQuestion": null,
                  "steps": [
                    { "id": "set-name", "kind": "set_value", "stableTargetId": "target-name", "value": "Ada", "checked": null }
                  ]
                }
                """),
            browser);
        BrowserActionProposal proposal = (await service.PlanAsync(
            Form(),
            "Change the name",
            ActionProposalSource.DirectUserRequest,
            true,
            ModelCatalog.Balanced)).Proposal!;

        BrowserActionExecutionResult execution = await service.ExecuteAsync(proposal);

        Assert.Equal(BrowserActionExecutionStatus.Failed, execution.Status);
        Assert.Equal("target_missing", execution.BrowserFailure?.SafeFailureCode);
        Assert.Single(browser.Commands);
        Assert.Equal(ActionOutcomeState.Failed, execution.Receipt?.State);
    }

    [Fact]
    public async Task ExplicitBrowserSearch_IsTypedLowRiskWhileOrdinarySubmitRemainsHighRisk()
    {
        var browser = new StubBrowserGateway();
        var service = new BrowserTakeActionService(
            new StubResponsesGateway("""
                {
                  "schemaVersion": 1,
                  "goal": "Search the web",
                  "needsClarification": false,
                  "clarifyingQuestion": null,
                  "steps": [
                    { "id": "query", "kind": "set_value", "stableTargetId": "target-query", "value": "Cursivis Windows", "checked": null },
                    { "id": "search", "kind": "submit_search", "stableTargetId": "target-search", "value": null, "checked": null }
                  ]
                }
                """),
            browser);
        var form = new BrowserFormSnapshot(
            new BrowserTabIdentity(7, 2, "https://www.google.com", true, true),
            "form-search",
            "Search",
            [new BrowserFormField("target-query", BrowserFormFieldKind.Text, "Search", null, true, false, string.Empty, [])],
            [new BrowserFormControl("target-search", BrowserFormControlKind.SearchSubmit, "Google Search", false)],
            "search-fingerprint");

        BrowserActionProposal proposal = (await service.PlanAsync(
            form,
            "Search Google for Cursivis Windows",
            ActionProposalSource.DirectUserRequest,
            true,
            ModelCatalog.Balanced)).Proposal!;
        BrowserActionExecutionResult result = await service.ExecuteAsync(proposal);

        Assert.Equal(BrowserActionExecutionStatus.Completed, result.Status);
        Assert.Equal(RiskLevel.Low, result.Policy.EffectiveRisk);
        Assert.Equal(ConfirmationRequirement.None, result.Policy.ConfirmationRequirement);
        Assert.Equal(BrowserCommandKind.SubmitSearch, browser.Commands[1].Kind);
    }

    private static BrowserFormSnapshot Form() => new(
        new BrowserTabIdentity(42, 7, "https://example.test", true, true),
        "form-profile",
        "Profile",
        [
            new BrowserFormField(
                "target-name",
                BrowserFormFieldKind.Text,
                "Display name",
                null,
                true,
                false,
                "Grace",
                []),
            new BrowserFormField(
                "target-role",
                BrowserFormFieldKind.Select,
                "Role",
                null,
                true,
                false,
                "Designer",
                ["Designer", "Engineer"]),
        ],
        [
            new BrowserFormControl("target-submit", BrowserFormControlKind.Submit, "Save", true),
        ],
        "form-fingerprint");

    private sealed class StubResponsesGateway(string json) : IResponsesGateway
    {
        public StructuredResponseRequest? LastRequest { get; private set; }

        public Task<StructuredResponseResult> CreateStructuredResponseAsync(
            StructuredResponseRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(StructuredResponseResult.Success(json, request.Model, "response-action"));
        }

        public Task<ModelAvailabilityResult> CheckModelAvailabilityAsync(
            string model,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelAvailabilityResult(model, true, null, DateTimeOffset.UtcNow));
    }

    private sealed class StubBrowserGateway : IBrowserActionGateway
    {
        private int _resultIndex;

        public List<BrowserCommand> Commands { get; } = [];

        public IReadOnlyList<BrowserStepResult> Results { get; init; } = [];

        public Task<BrowserStepResult> ExecuteAsync(
            BrowserCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            BrowserStepResult result = _resultIndex < Results.Count
                ? Results[_resultIndex++]
                : new BrowserStepResult(command.StepId, true, true, command.Value, null, null);
            return Task.FromResult(result);
        }
    }
}
