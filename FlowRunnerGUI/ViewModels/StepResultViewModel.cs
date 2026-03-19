using CommunityToolkit.Mvvm.ComponentModel;
using FlowEngine;

namespace FlowRunnerGUI.ViewModels;

public partial class StepResultViewModel : ObservableObject
{
    [ObservableProperty] private string _stepId = string.Empty;
    [ObservableProperty] private string _stepName = string.Empty;
    [ObservableProperty] private string _stepType = string.Empty;
    [ObservableProperty] private StepStatus _status;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private string? _errorMessage;

    public string StatusIcon => Status switch
    {
        StepStatus.Success => "\u2714",
        StepStatus.Failed => "\u2716",
        StepStatus.Timeout => "\u23F1",
        StepStatus.Skipped => "\u23ED",
        _ => "\u2022"
    };

    public StepResultViewModel()
    {
    }

    public StepResultViewModel(StepExecutionResult result)
    {
        StepId = result.StepId;
        StepName = result.StepName;
        StepType = result.StepType;
        Status = result.Status;
        DurationMs = result.DurationMs;
        ErrorMessage = result.ErrorMessage;
    }
}
