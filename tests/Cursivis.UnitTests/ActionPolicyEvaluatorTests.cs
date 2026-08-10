using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;

namespace Cursivis.UnitTests;

public sealed class ActionPolicyEvaluatorTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = CapturedAt.AddMinutes(1);

    [Fact]
    public void Evaluate_ReadOnlyPlan_AllowsWithoutConfirmation()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.None,
            ConfirmationRequirement.None,
            Step(ActionStepType.ReadSelection));

        var decision = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot));

        Assert.Equal(PolicyDisposition.Allowed, decision.Disposition);
        Assert.Equal(RiskLevel.None, decision.EffectiveRisk);
        Assert.Equal(ConfirmationRequirement.None, decision.ConfirmationRequirement);
        Assert.False(decision.RequiresPreview);
    }

    [Fact]
    public void Evaluate_OneDirectNonSensitiveField_AllowsLowRiskWithoutConfirmation()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Low,
            ConfirmationRequirement.None,
            Step(ActionStepType.SetValue, "Email address", "fixture@example.com"));

        var decision = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot, isDirectUserRequest: true));

        Assert.Equal(PolicyDisposition.Allowed, decision.Disposition);
        Assert.Equal(RiskLevel.Low, decision.EffectiveRisk);
    }

    [Fact]
    public void Evaluate_MultipleFields_RequiresBoundConfirmationThenAllows()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            Step(ActionStepType.SetValue, "First name", "Ada", id: "step-1"),
            Step(ActionStepType.SetValue, "Last name", "Lovelace", id: "step-2"));

        var withoutConfirmation = ActionPolicyEvaluator.Evaluate(
            plan,
            PolicyContext(snapshot, isDirectUserRequest: true));

        Assert.Equal(PolicyDisposition.ConfirmationRequired, withoutConfirmation.Disposition);
        Assert.True(withoutConfirmation.RequiresPreview);

        var confirmation = ActionConfirmation.Bind(plan, RiskLevel.Medium, Now.AddMinutes(1));
        var allowed = ActionPolicyEvaluator.Evaluate(
            plan,
            PolicyContext(snapshot, isDirectUserRequest: true, confirmation));

        Assert.Equal(PolicyDisposition.Allowed, allowed.Disposition);
    }

    [Fact]
    public void Evaluate_SubmitForm_AlwaysRequiresFreshConfirmationAndPreview()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.High,
            ConfirmationRequirement.Fresh,
            Step(ActionStepType.SubmitForm));

        var pending = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot));

        Assert.Equal(PolicyDisposition.ConfirmationRequired, pending.Disposition);
        Assert.Equal(ConfirmationRequirement.Fresh, pending.ConfirmationRequirement);
        Assert.True(pending.RequiresPreview);

        var confirmation = ActionConfirmation.Bind(plan, RiskLevel.High, Now.AddSeconds(30));
        var allowed = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot, confirmation: confirmation));
        Assert.Equal(PolicyDisposition.Allowed, allowed.Disposition);
    }

    [Fact]
    public void Evaluate_UnderstatedRisk_IsRejectedRatherThanUpgradedSilently()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Low,
            ConfirmationRequirement.Fresh,
            Step(ActionStepType.DeleteData));

        var decision = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot));

        AssertRejected(decision, "risk.understated");
    }

    [Fact]
    public void Evaluate_UnderstatedConfirmation_IsRejected()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.High,
            ConfirmationRequirement.None,
            Step(ActionStepType.SendMessage));

        AssertRejected(
            ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot)),
            "confirmation.understated");
    }

    [Fact]
    public void Evaluate_DifferentContextFingerprint_IsRejected()
    {
        var original = CreateContext("original window");
        var current = CreateContext("current window");
        var plan = CreatePlan(
            original,
            RiskLevel.None,
            ConfirmationRequirement.None,
            Step(ActionStepType.ReadSelection));

        AssertRejected(
            ActionPolicyEvaluator.Evaluate(plan, PolicyContext(current)),
            "context.stale");
    }

    [Fact]
    public void Evaluate_ExpiredContext_IsRejectedEvenWhenFingerprintMatches()
    {
        var expired = ContextSnapshot.FromText(
            ContextKind.Text,
            ContextSource.DirectInput,
            new TargetIdentity("editor", "window-expired"),
            "fixture",
            Now.AddSeconds(-2),
            TimeSpan.FromSeconds(1));
        var plan = CreatePlan(
            expired,
            RiskLevel.None,
            ConfirmationRequirement.None,
            Step(ActionStepType.ReadSelection));

        AssertRejected(
            ActionPolicyEvaluator.Evaluate(plan, PolicyContext(expired)),
            "context.expired");
    }

    [Fact]
    public void Evaluate_QuickTaskMutation_CannotBypassActionConfirmation()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            Step(ActionStepType.SetValue, "Compose body", "Generated response"));

        var decision = ActionPolicyEvaluator.Evaluate(
            plan,
            PolicyContext(
                snapshot,
                isDirectUserRequest: true,
                source: ActionProposalSource.QuickTask));

        Assert.Equal(PolicyDisposition.ConfirmationRequired, decision.Disposition);
        Assert.Equal(RiskLevel.Medium, decision.EffectiveRisk);
    }

    [Fact]
    public void Evaluate_LargeSelectionReplacement_RequiresConfirmation()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            Step(ActionStepType.ReplaceSelection, "Current selection", "replacement"));

        var decision = ActionPolicyEvaluator.Evaluate(
            plan,
            PolicyContext(
                snapshot,
                isDirectUserRequest: true,
                selectionLength: ActionPolicyEvaluator.LargeSelectionThreshold + 1));

        Assert.Equal(PolicyDisposition.ConfirmationRequired, decision.Disposition);
    }

    [Fact]
    public void Evaluate_SensitiveTarget_RequiresFreshHighRiskConfirmation()
    {
        var snapshot = CreateContext();
        var sensitiveStep = Step(ActionStepType.SetValue, "Password", "private-value", sensitive: true);
        var plan = CreatePlan(snapshot, RiskLevel.High, ConfirmationRequirement.Fresh, sensitiveStep);

        var decision = ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot, isDirectUserRequest: true));

        Assert.Equal(PolicyDisposition.ConfirmationRequired, decision.Disposition);
        Assert.Equal(RiskLevel.High, decision.EffectiveRisk);
        Assert.Equal(ConfirmationRequirement.Fresh, decision.ConfirmationRequirement);
    }

    [Fact]
    public void Evaluate_ExpiredMismatchedAndReplayedConfirmations_AreRejected()
    {
        var snapshot = CreateContext();
        var plan = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            Step(ActionStepType.ClickNavigation));

        var expired = ActionConfirmation.Bind(plan, RiskLevel.Medium, Now);
        AssertRejected(
            ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot, confirmation: expired)),
            "confirmation.expired");

        var otherPlan = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            Step(ActionStepType.OpenExternalUrl));
        var mismatched = ActionConfirmation.Bind(otherPlan, RiskLevel.Medium, Now.AddMinutes(1));
        AssertRejected(
            ActionPolicyEvaluator.Evaluate(plan, PolicyContext(snapshot, confirmation: mismatched)),
            "confirmation.mismatch");

        var replayed = ActionConfirmation.Bind(plan, RiskLevel.Medium, Now.AddMinutes(1));
        AssertRejected(
            ActionPolicyEvaluator.Evaluate(
                plan,
                PolicyContext(snapshot, confirmation: replayed, consumed: [replayed.Id])),
            "confirmation.replayed");
    }

    [Fact]
    public void Confirmation_IsBoundToExactStepContent()
    {
        var snapshot = CreateContext();
        var id = ActionPlanId.New();
        var original = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            id,
            Step(ActionStepType.InsertText, "Compose", "original"));
        var changed = CreatePlan(
            snapshot,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            id,
            Step(ActionStepType.InsertText, "Compose", "changed"));
        var confirmation = ActionConfirmation.Bind(original, RiskLevel.Medium, Now.AddMinutes(1));

        AssertRejected(
            ActionPolicyEvaluator.Evaluate(changed, PolicyContext(snapshot, confirmation: confirmation)),
            "confirmation.mismatch");
    }

    [Fact]
    public void ActionContracts_RejectUnknownEnumsAndDuplicateSteps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActionTarget((ActionTargetStrategy)999, "target"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ActionStep(
                "step",
                (ActionStepType)999,
                new ActionTarget(ActionTargetStrategy.Name, "target"),
                new VerificationExpectation(VerificationMethod.ElementState, "changed")));

        var snapshot = CreateContext();
        var duplicate = Step(ActionStepType.ReadSelection, id: "duplicate");
        Assert.Throws<ArgumentException>(() =>
            CreatePlan(snapshot, RiskLevel.None, ConfirmationRequirement.None, duplicate, duplicate));
    }

    [Fact]
    public void ActionContracts_ToString_RedactsGoalTargetValueAndExpectation()
    {
        var snapshot = CreateContext();
        var plan = new ActionPlan(
            ActionPlanId.New(),
            "Send to customer@example.com",
            snapshot.Fingerprint,
            RiskLevel.Medium,
            ConfirmationRequirement.ContextDependent,
            [Step(ActionStepType.InsertText, "customer@example.com", "private body")]);

        var diagnostic = $"{plan} {plan.Steps[0]} {plan.Steps[0].Target} {plan.Steps[0].Value} {plan.Steps[0].ExpectedOutcome}";

        Assert.DoesNotContain("customer@example.com", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private body", diagnostic, StringComparison.Ordinal);
        Assert.Contains("<redacted>", diagnostic, StringComparison.Ordinal);
    }

    private static ActionPolicyContext PolicyContext(
        ContextSnapshot snapshot,
        bool isDirectUserRequest = false,
        ActionConfirmation? confirmation = null,
        ActionProposalSource source = ActionProposalSource.DirectUserRequest,
        int selectionLength = 0,
        IEnumerable<ConfirmationId>? consumed = null) =>
        new(
            snapshot,
            Now,
            source,
            isDirectUserRequest,
            selectionLength,
            confirmation,
            consumed);

    private static ActionPlan CreatePlan(
        ContextSnapshot snapshot,
        RiskLevel risk,
        ConfirmationRequirement confirmation,
        params ActionStep[] steps) =>
        CreatePlan(snapshot, risk, confirmation, ActionPlanId.New(), steps);

    private static ActionPlan CreatePlan(
        ContextSnapshot snapshot,
        RiskLevel risk,
        ConfirmationRequirement confirmation,
        ActionPlanId id,
        params ActionStep[] steps) =>
        new(id, "Fixture goal", snapshot.Fingerprint, risk, confirmation, steps);

    private static ActionStep Step(
        ActionStepType type,
        string target = "fixture target",
        string? value = null,
        string id = "step-1",
        bool sensitive = false) =>
        new(
            id,
            type,
            new ActionTarget(ActionTargetStrategy.Accessibility, target),
            new VerificationExpectation(VerificationMethod.AccessibilityState, "expected fixture state"),
            value is null ? null : new ActionValue(value),
            isSensitiveTarget: sensitive);

    private static ContextSnapshot CreateContext(string windowId = "window-1") => ContextSnapshot.FromText(
        ContextKind.Text,
        ContextSource.UserInterfaceAutomation,
        new TargetIdentity("editor", windowId),
        "bounded fixture selection",
        CapturedAt,
        TimeSpan.FromMinutes(5));

    private static void AssertRejected(PolicyDecision decision, string reasonCode)
    {
        Assert.Equal(PolicyDisposition.Rejected, decision.Disposition);
        Assert.False(decision.CanExecute);
        Assert.Contains(decision.Reasons, reason => reason.Code == reasonCode);
    }
}
