using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cursivis.Domain.Context;

namespace Cursivis.Domain.Actions;

public enum RiskLevel
{
    None,
    Low,
    Medium,
    High,
}

public enum ConfirmationRequirement
{
    None,
    ContextDependent,
    Fresh,
}

public enum ActionStepType
{
    ReadSelection,
    AnalyzeExplicitImage,
    RunQuickTask,
    CopyGeneratedText,
    OpenGuidedMode,
    InspectActiveTabStructure,
    FocusField,
    SetValue,
    ChooseOption,
    ToggleCheckbox,
    ReplaceSelection,
    InsertText,
    RunBrowserSearch,
    ClickNavigation,
    OpenExternalUrl,
    StartNavigationGuidance,
    CaptureExtendedScreenContext,
    Scroll,
    SubmitForm,
    SendMessage,
    Purchase,
    ScheduleFinancialOperation,
    ModifyAccount,
    ChangePermission,
    EnterCredentialOrSecret,
    DeleteData,
    PostPublicly,
    AcceptLegalTerms,
    UploadPrivateFile,
}

public enum ActionTargetStrategy
{
    CurrentSelection,
    FocusedElement,
    Label,
    Role,
    Name,
    StableSelector,
    Accessibility,
    ActiveTab,
    ApprovedWindow,
}

public enum VerificationMethod
{
    SelectionText,
    ElementValue,
    ElementState,
    AccessibilityState,
    Url,
    Title,
    DomState,
    ScreenshotDigest,
}

public enum VerificationState
{
    NotStarted,
    Pending,
    Passed,
    Failed,
    NotApplicable,
}

public enum ActionOutcomeState
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
    RolledBack,
}

public readonly record struct ActionPlanId
{
    public ActionPlanId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An action plan identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ActionPlanId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ConfirmationId
{
    public ConfirmationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A confirmation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ConfirmationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public sealed class ActionTarget
{
    public ActionTarget(ActionTargetStrategy strategy, string value)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An action target value is required.", nameof(value));
        }

        if (value.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An action target value cannot exceed 512 characters.");
        }

        Strategy = strategy;
        Value = value.Trim();
    }

    public ActionTargetStrategy Strategy { get; }

    public string Value { get; }

    public override string ToString() => $"ActionTarget(Strategy={Strategy}, Value=<redacted>)";
}

public sealed class ActionValue
{
    public ActionValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("An action value cannot be empty.", nameof(value));
        }

        if (value.Length > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An action value cannot exceed 32,768 characters.");
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => "<redacted>";
}

public sealed class VerificationExpectation
{
    public VerificationExpectation(VerificationMethod method, string expectedValue)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            throw new ArgumentException("A verification expectation is required.", nameof(expectedValue));
        }

        if (expectedValue.Length > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedValue));
        }

        Method = method;
        ExpectedValue = expectedValue;
    }

    public VerificationMethod Method { get; }

    public string ExpectedValue { get; }

    public override string ToString() => $"VerificationExpectation(Method={Method}, ExpectedValue=<redacted>)";
}

public sealed class ActionStep
{
    public ActionStep(
        string id,
        ActionStepType type,
        ActionTarget target,
        VerificationExpectation expectedOutcome,
        ActionValue? value = null,
        bool isSensitiveTarget = false,
        bool isIrreversible = false)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("An action step identifier is required.", nameof(id));
        }

        if (id.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expectedOutcome);

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Id = id.Trim();
        Type = type;
        Target = target;
        Value = value;
        ExpectedOutcome = expectedOutcome;
        IsSensitiveTarget = isSensitiveTarget;
        IsIrreversible = isIrreversible;
    }

    public string Id { get; }

    public ActionStepType Type { get; }

    public ActionTarget Target { get; }

    public ActionValue? Value { get; }

    public VerificationExpectation ExpectedOutcome { get; }

    public bool IsSensitiveTarget { get; }

    public bool IsIrreversible { get; }

    public override string ToString() =>
        $"ActionStep(Id={Id}, Type={Type}, Target={Target.Strategy}, Value=<redacted>, Verification={ExpectedOutcome.Method}, Sensitive={IsSensitiveTarget}, Irreversible={IsIrreversible})";
}

public sealed class ActionPlan
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSteps = 20;

    public ActionPlan(
        ActionPlanId id,
        string goal,
        ContextFingerprint contextFingerprint,
        RiskLevel declaredRisk,
        ConfirmationRequirement declaredConfirmation,
        IEnumerable<ActionStep> steps)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("An action plan goal is required.", nameof(goal));
        }

        ArgumentNullException.ThrowIfNull(steps);

        if (!Enum.IsDefined(declaredRisk))
        {
            throw new ArgumentOutOfRangeException(nameof(declaredRisk));
        }

        if (!Enum.IsDefined(declaredConfirmation))
        {
            throw new ArgumentOutOfRangeException(nameof(declaredConfirmation));
        }
        var immutableSteps = steps.ToImmutableArray();
        if (immutableSteps.Length is < 1 or > MaximumSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), $"An action plan must contain between one and {MaximumSteps} steps.");
        }

        if (immutableSteps.GroupBy(step => step.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Action step identifiers must be unique.", nameof(steps));
        }

        Id = id;
        Goal = goal.Trim();
        ContextFingerprint = contextFingerprint;
        DeclaredRisk = declaredRisk;
        DeclaredConfirmation = declaredConfirmation;
        Steps = immutableSteps;
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public ActionPlanId Id { get; }

    public string Goal { get; }

    public ContextFingerprint ContextFingerprint { get; }

    public RiskLevel DeclaredRisk { get; }

    public ConfirmationRequirement DeclaredConfirmation { get; }

    public ImmutableArray<ActionStep> Steps { get; }

    public override string ToString() =>
        $"ActionPlan(Id={Id}, Goal=<redacted>, Fingerprint={ContextFingerprint}, DeclaredRisk={DeclaredRisk}, DeclaredConfirmation={DeclaredConfirmation}, Steps={Steps.Length})";

    internal string CreateConfirmationBinding()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, Id.ToString());
        Append(hash, ContextFingerprint.Value);
        Append(hash, Goal);
        Append(hash, DeclaredRisk.ToString());
        Append(hash, DeclaredConfirmation.ToString());

        foreach (var step in Steps)
        {
            Append(hash, step.Id);
            Append(hash, step.Type.ToString());
            Append(hash, step.Target.Strategy.ToString());
            Append(hash, step.Target.Value);
            Append(hash, step.Value?.Value ?? string.Empty);
            Append(hash, step.ExpectedOutcome.Method.ToString());
            Append(hash, step.ExpectedOutcome.ExpectedValue);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed class ActionConfirmation
{
    private ActionConfirmation(
        ConfirmationId id,
        ActionPlanId planId,
        ContextFingerprint contextFingerprint,
        RiskLevel risk,
        string binding,
        DateTimeOffset expiresAt)
    {
        Id = id;
        PlanId = planId;
        ContextFingerprint = contextFingerprint;
        Risk = risk;
        Binding = binding;
        ExpiresAt = expiresAt;
    }

    public ConfirmationId Id { get; }

    public ActionPlanId PlanId { get; }

    public ContextFingerprint ContextFingerprint { get; }

    public RiskLevel Risk { get; }

    public DateTimeOffset ExpiresAt { get; }

    private string Binding { get; }

    public static ActionConfirmation Bind(
        ActionPlan plan,
        RiskLevel effectiveRisk,
        DateTimeOffset expiresAt,
        ConfirmationId? id = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(
            id ?? ConfirmationId.New(),
            plan.Id,
            plan.ContextFingerprint,
            effectiveRisk,
            plan.CreateConfirmationBinding(),
            expiresAt);
    }

    internal bool Matches(ActionPlan plan, RiskLevel effectiveRisk) =>
        PlanId == plan.Id &&
        ContextFingerprint == plan.ContextFingerprint &&
        Risk == effectiveRisk &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Binding),
            Convert.FromHexString(plan.CreateConfirmationBinding()));

    public override string ToString() =>
        $"ActionConfirmation(Id={Id}, Plan={PlanId}, Fingerprint={ContextFingerprint}, Risk={Risk}, ExpiresAt={ExpiresAt:O}, Binding=<redacted>)";
}

public sealed record StepOutcome(
    string StepId,
    ActionOutcomeState State,
    VerificationState Verification,
    string? FailureCode = null);

public sealed record ExecutionReceipt(
    ActionPlanId PlanId,
    ActionOutcomeState State,
    ImmutableArray<StepOutcome> Steps,
    DateTimeOffset CompletedAt,
    bool HasVerifiedStateTransition);
