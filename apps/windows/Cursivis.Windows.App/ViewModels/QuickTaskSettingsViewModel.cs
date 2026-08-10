using Cursivis.Application.QuickTasks;
using Cursivis.Domain.QuickTasks;
using Cursivis.Windows.App.Helpers;

namespace Cursivis.Windows.App.ViewModels;

internal sealed class QuickTaskSettingsViewModel : ObservableViewModel
{
    private QuickTaskDefinition _savedDefinition = QuickTaskDefaults.PromptOptimizer;
    private QuickTaskId _id = QuickTaskDefaults.PromptOptimizerId;
    private string _displayName = string.Empty;
    private string _roughDescription = string.Empty;
    private string _finalInstruction = string.Empty;
    private bool _supportsText;
    private bool _supportsImage;
    private QuickTaskOutputMode _outputMode;
    private bool _mayProposeAction;
    private bool _isApproved;
    private bool _isDirty;
    private bool _isDefinitionValid;
    private string _validationSummary = string.Empty;
    private IReadOnlyList<string> _finalizationSafetyConcerns = [];

    public QuickTaskSettingsViewModel() => Load(QuickTaskDefaults.PromptOptimizer);

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value))
            {
                MarkEdited();
            }
        }
    }

    public string RoughDescription
    {
        get => _roughDescription;
        set
        {
            if (SetProperty(ref _roughDescription, value))
            {
                MarkEdited();
            }
        }
    }

    public string FinalInstruction
    {
        get => _finalInstruction;
        set
        {
            if (SetProperty(ref _finalInstruction, value))
            {
                MarkEdited();
            }
        }
    }

    public bool SupportsText
    {
        get => _supportsText;
        set
        {
            if (SetProperty(ref _supportsText, value))
            {
                MarkEdited();
            }
        }
    }

    public bool SupportsImage
    {
        get => _supportsImage;
        set
        {
            if (SetProperty(ref _supportsImage, value))
            {
                MarkEdited();
            }
        }
    }

    public int OutputModeIndex
    {
        get => (int)_outputMode;
        set
        {
            if (value is >= 0 and <= 3 && SetProperty(ref _outputMode, (QuickTaskOutputMode)value, nameof(OutputModeIndex)))
            {
                MarkEdited();
            }
        }
    }

    public bool MayProposeAction
    {
        get => _mayProposeAction;
        set
        {
            if (SetProperty(ref _mayProposeAction, value))
            {
                MarkEdited();
            }
        }
    }

    public bool IsApproved
    {
        get => _isApproved;
        set
        {
            if (SetProperty(ref _isApproved, value))
            {
                MarkEdited(resetApproval: false);
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public bool IsDefinitionValid
    {
        get => _isDefinitionValid;
        private set => SetProperty(ref _isDefinitionValid, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public void Load(QuickTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _savedDefinition = definition;
        _id = definition.Id;
        _displayName = definition.DisplayName;
        _roughDescription = definition.Id == QuickTaskDefaults.PromptOptimizerId
            ? ResourceText.Get("PromptOptimizerDefaultDescription")
            : string.Empty;
        _finalInstruction = definition.FinalizedInstruction;
        _supportsText = definition.SupportedContext.HasFlag(QuickTaskContextType.Text);
        _supportsImage = definition.SupportedContext.HasFlag(QuickTaskContextType.Image);
        _outputMode = definition.OutputMode;
        _mayProposeAction = definition.MayProposeAction;
        _isApproved = definition.IsExplicitlyApproved;
        _finalizationSafetyConcerns = [];
        NotifyAll();
        IsDirty = false;
        Validate();
    }

    public void ApplyDraft(QuickTaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _id = new QuickTaskId("custom-task");
        _displayName = draft.DisplayName;
        _finalInstruction = draft.FinalizedInstruction;
        _supportsText = draft.SupportedContext.HasFlag(QuickTaskContextType.Text);
        _supportsImage = draft.SupportedContext.HasFlag(QuickTaskContextType.Image);
        _outputMode = draft.OutputMode;
        _mayProposeAction = draft.MayProposeAction;
        _isApproved = false;
        _finalizationSafetyConcerns = draft.SafetyConcerns;
        NotifyAll();
        IsDirty = true;
        Validate();
    }

    public QuickTaskDefinition CreateDefinition(bool isExplicitlyApproved) => new(
        _id,
        DisplayName,
        FinalInstruction,
        GetSupportedContext(),
        _outputMode,
        MayProposeAction,
        isExplicitlyApproved);

    public void MarkSaved(QuickTaskDefinition definition) => Load(definition);

    public void RestorePromptOptimizer()
    {
        _id = QuickTaskDefaults.PromptOptimizerId;
        _displayName = QuickTaskDefaults.PromptOptimizer.DisplayName;
        _roughDescription = ResourceText.Get("PromptOptimizerDefaultDescription");
        _finalInstruction = QuickTaskDefaults.PromptOptimizer.FinalizedInstruction;
        _supportsText = true;
        _supportsImage = false;
        _outputMode = QuickTaskOutputMode.ReplacementText;
        _mayProposeAction = false;
        _isApproved = false;
        _finalizationSafetyConcerns = [];
        NotifyAll();
        IsDirty = true;
        Validate();
    }

    public void DiscardChanges() => Load(_savedDefinition);

    private void MarkEdited(bool resetApproval = true)
    {
        if (resetApproval && _isApproved)
        {
            _isApproved = false;
            OnPropertyChanged(nameof(IsApproved));
        }

        IsDirty = true;
        Validate();
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            SetValidation(false, "QuickTaskValidationNameRequired");
            return;
        }

        if (DisplayName.Trim().Length > 64)
        {
            SetValidation(false, "QuickTaskValidationNameLength");
            return;
        }

        if (string.IsNullOrWhiteSpace(FinalInstruction) || FinalInstruction.Trim().Length < 40)
        {
            SetValidation(false, "QuickTaskValidationInstructionRequired");
            return;
        }

        if (FinalInstruction.Length > 8_000)
        {
            SetValidation(false, "QuickTaskValidationInstructionLength");
            return;
        }

        if (!SupportsText && !SupportsImage)
        {
            SetValidation(false, "QuickTaskValidationContextRequired");
            return;
        }

        if (_finalizationSafetyConcerns.Count > 0 ||
            QuickTaskSafetyValidator.Validate(FinalInstruction).Count > 0)
        {
            SetValidation(false, "QuickTaskValidationSafetyRejected");
            return;
        }

        SetValidation(true, "QuickTaskValidationReady");
    }

    private QuickTaskContextType GetSupportedContext() =>
        (SupportsText ? QuickTaskContextType.Text : QuickTaskContextType.None) |
        (SupportsImage ? QuickTaskContextType.Image : QuickTaskContextType.None);

    private void SetValidation(bool isValid, string messageKey)
    {
        IsDefinitionValid = isValid;
        ValidationSummary = ResourceText.Get(messageKey);
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RoughDescription));
        OnPropertyChanged(nameof(FinalInstruction));
        OnPropertyChanged(nameof(SupportsText));
        OnPropertyChanged(nameof(SupportsImage));
        OnPropertyChanged(nameof(OutputModeIndex));
        OnPropertyChanged(nameof(MayProposeAction));
        OnPropertyChanged(nameof(IsApproved));
    }
}
