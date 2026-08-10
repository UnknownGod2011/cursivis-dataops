using System.Collections.Immutable;
using Cursivis.Domain.Context;

namespace Cursivis.Domain.Actions;

public enum ActionProposalSource
{
    DirectUserRequest,
    SmartMode,
    GuidedMode,
    QuickTask,
    Realtime,
}

public enum PolicyDisposition
{
    Allowed,
    ConfirmationRequired,
    Rejected,
}

public sealed record PolicyReason(string Code, string Message);

public sealed class ActionPolicyContext
{
    public ActionPolicyContext(
        ContextSnapshot currentContext,
        DateTimeOffset now,
        ActionProposalSource source,
        bool isDirectUserRequest,
        int currentSelectionLength = 0,
        ActionConfirmation? confirmation = null,
        IEnumerable<ConfirmationId>? consumedConfirmationIds = null)
    {
        ArgumentNullException.ThrowIfNull(currentContext);

        if (currentSelectionLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSelectionLength));
        }

        CurrentContextFingerprint = currentContext.Fingerprint;
        CurrentContextExpiresAt = currentContext.ExpiresAt;
        Now = now;
        Source = source;
        IsDirectUserRequest = isDirectUserRequest;
        CurrentSelectionLength = currentSelectionLength;
        Confirmation = confirmation;
        ConsumedConfirmationIds = (consumedConfirmationIds ?? [])
            .ToImmutableHashSet();
    }

    public ContextFingerprint CurrentContextFingerprint { get; }

    public DateTimeOffset CurrentContextExpiresAt { get; }

    public DateTimeOffset Now { get; }

    public ActionProposalSource Source { get; }

    public bool IsDirectUserRequest { get; }

    public int CurrentSelectionLength { get; }

    public ActionConfirmation? Confirmation { get; }

    public ImmutableHashSet<ConfirmationId> ConsumedConfirmationIds { get; }
}

public sealed record PolicyDecision(
    PolicyDisposition Disposition,
    RiskLevel EffectiveRisk,
    ConfirmationRequirement ConfirmationRequirement,
    bool RequiresPreview,
    ImmutableArray<PolicyReason> Reasons)
{
    public bool CanExecute => Disposition == PolicyDisposition.Allowed;
}

/// <summary>
/// Derives risk and confirmation locally. Model hints and saved task instructions have no authority here.
/// </summary>
public static class ActionPolicyEvaluator
{
    public const int LargeSelectionThreshold = 2_000;

    public static PolicyDecision Evaluate(ActionPlan plan, ActionPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Now >= context.CurrentContextExpiresAt)
        {
            return Rejected(
                RiskLevel.High,
                ConfirmationRequirement.Fresh,
                "context.expired",
                "The captured context has expired.");
        }

        if (plan.ContextFingerprint != context.CurrentContextFingerprint)
        {
            return Rejected(
                RiskLevel.High,
                ConfirmationRequirement.Fresh,
                "context.stale",
                "The plan context does not match the current active context.");
        }

        var (derivedRisk, derivedConfirmation) = Derive(plan, context);

        if (plan.DeclaredRisk < derivedRisk)
        {
            return Rejected(
                derivedRisk,
                derivedConfirmation,
                "risk.understated",
                "The plan declares a lower risk than its steps require.");
        }

        if (plan.DeclaredConfirmation < derivedConfirmation)
        {
            return Rejected(
                Max(plan.DeclaredRisk, derivedRisk),
                derivedConfirmation,
                "confirmation.understated",
                "The plan declares a weaker confirmation requirement than its steps require.");
        }

        var effectiveRisk = Max(plan.DeclaredRisk, derivedRisk);
        var effectiveConfirmation = Max(plan.DeclaredConfirmation, derivedConfirmation);
        var requiresPreview = effectiveRisk >= RiskLevel.Medium;

        if (effectiveConfirmation == ConfirmationRequirement.None)
        {
            return new(
                PolicyDisposition.Allowed,
                effectiveRisk,
                effectiveConfirmation,
                requiresPreview,
                []);
        }

        if (context.Confirmation is null)
        {
            return new(
                PolicyDisposition.ConfirmationRequired,
                effectiveRisk,
                effectiveConfirmation,
                requiresPreview,
                [new("confirmation.required", "This action requires confirmation bound to the current plan.")]);
        }

        if (context.ConsumedConfirmationIds.Contains(context.Confirmation.Id))
        {
            return Rejected(
                effectiveRisk,
                effectiveConfirmation,
                "confirmation.replayed",
                "The confirmation has already been used.");
        }

        if (context.Now >= context.Confirmation.ExpiresAt)
        {
            return Rejected(
                effectiveRisk,
                effectiveConfirmation,
                "confirmation.expired",
                "The confirmation has expired.");
        }

        if (!context.Confirmation.Matches(plan, effectiveRisk))
        {
            return Rejected(
                effectiveRisk,
                effectiveConfirmation,
                "confirmation.mismatch",
                "The confirmation is not bound to this exact plan and context.");
        }

        return new(
            PolicyDisposition.Allowed,
            effectiveRisk,
            effectiveConfirmation,
            requiresPreview,
            []);
    }

    private static (RiskLevel Risk, ConfirmationRequirement Confirmation) Derive(
        ActionPlan plan,
        ActionPolicyContext context)
    {
        var fieldMutationCount = plan.Steps.Count(step => step.Type is
            ActionStepType.SetValue or
            ActionStepType.ChooseOption or
            ActionStepType.ToggleCheckbox);

        var risk = RiskLevel.None;
        var confirmation = ConfirmationRequirement.None;
        var hasMutatingStep = false;

        foreach (var step in plan.Steps)
        {
            var classification = ClassifyStep(step, context, fieldMutationCount);
            risk = Max(risk, classification.Risk);
            confirmation = Max(confirmation, classification.Confirmation);
            hasMutatingStep |= IsMutating(step.Type);
        }

        if (context.Source == ActionProposalSource.QuickTask && hasMutatingStep)
        {
            risk = Max(risk, RiskLevel.Medium);
            confirmation = Max(confirmation, ConfirmationRequirement.ContextDependent);
        }

        return (risk, confirmation);
    }

    private static (RiskLevel Risk, ConfirmationRequirement Confirmation) ClassifyStep(
        ActionStep step,
        ActionPolicyContext context,
        int fieldMutationCount)
    {
        if (step.IsIrreversible || step.IsSensitiveTarget)
        {
            return (RiskLevel.High, ConfirmationRequirement.Fresh);
        }

        return step.Type switch
        {
            ActionStepType.ReadSelection or
            ActionStepType.AnalyzeExplicitImage or
            ActionStepType.RunQuickTask or
            ActionStepType.OpenGuidedMode or
            ActionStepType.InspectActiveTabStructure =>
                (RiskLevel.None, ConfirmationRequirement.None),

            ActionStepType.CopyGeneratedText or
            ActionStepType.FocusField or
            ActionStepType.RunBrowserSearch or
            ActionStepType.Scroll =>
                (RiskLevel.Low, ConfirmationRequirement.None),

            ActionStepType.ReplaceSelection when
                context.IsDirectUserRequest && context.CurrentSelectionLength <= LargeSelectionThreshold =>
                (RiskLevel.Low, ConfirmationRequirement.None),

            ActionStepType.SetValue or
            ActionStepType.ChooseOption or
            ActionStepType.ToggleCheckbox when
                context.IsDirectUserRequest && fieldMutationCount == 1 =>
                (RiskLevel.Low, ConfirmationRequirement.None),

            ActionStepType.ReplaceSelection or
            ActionStepType.SetValue or
            ActionStepType.ChooseOption or
            ActionStepType.ToggleCheckbox or
            ActionStepType.InsertText or
            ActionStepType.ClickNavigation or
            ActionStepType.OpenExternalUrl or
            ActionStepType.StartNavigationGuidance or
            ActionStepType.CaptureExtendedScreenContext =>
                (RiskLevel.Medium, ConfirmationRequirement.ContextDependent),

            ActionStepType.SubmitForm or
            ActionStepType.SendMessage or
            ActionStepType.Purchase or
            ActionStepType.ScheduleFinancialOperation or
            ActionStepType.ModifyAccount or
            ActionStepType.ChangePermission or
            ActionStepType.EnterCredentialOrSecret or
            ActionStepType.DeleteData or
            ActionStepType.PostPublicly or
            ActionStepType.AcceptLegalTerms or
            ActionStepType.UploadPrivateFile =>
                (RiskLevel.High, ConfirmationRequirement.Fresh),

            _ => (RiskLevel.High, ConfirmationRequirement.Fresh),
        };
    }

    private static bool IsMutating(ActionStepType type) => type is not (
        ActionStepType.ReadSelection or
        ActionStepType.AnalyzeExplicitImage or
        ActionStepType.RunQuickTask or
        ActionStepType.CopyGeneratedText or
        ActionStepType.OpenGuidedMode or
        ActionStepType.InspectActiveTabStructure);

    private static PolicyDecision Rejected(
        RiskLevel risk,
        ConfirmationRequirement confirmation,
        string code,
        string message) =>
        new(
            PolicyDisposition.Rejected,
            risk,
            confirmation,
            risk >= RiskLevel.Medium,
            [new(code, message)]);

    private static RiskLevel Max(RiskLevel left, RiskLevel right) =>
        left >= right ? left : right;

    private static ConfirmationRequirement Max(
        ConfirmationRequirement left,
        ConfirmationRequirement right) =>
        left >= right ? left : right;
}
