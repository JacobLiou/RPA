using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowRunnerGUI.ViewModels;

public partial class FlowStepPreviewViewModel : ObservableObject
{
    [ObservableProperty] private string _stepId = string.Empty;
    [ObservableProperty] private string _stepName = string.Empty;
    [ObservableProperty] private string _stepType = string.Empty;
    [ObservableProperty] private string _action = string.Empty;
    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _hasBreakpoint;
    [ObservableProperty] private bool _isCurrentStep;

    public string DisplayLabel => string.IsNullOrWhiteSpace(StepName)
        ? $"{Index + 1}. [{StepType}] {StepId}"
        : $"{Index + 1}. {StepName}";
}
