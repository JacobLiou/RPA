using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowRunnerGUI.ViewModels;

public partial class RunHistoryItemViewModel : ObservableObject
{
    [ObservableProperty] private string _runId = string.Empty;
    [ObservableProperty] private string _flowId = string.Empty;
    [ObservableProperty] private string _flowName = string.Empty;
    [ObservableProperty] private bool _success;
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private DateTimeOffset _finishedAt;
    [ObservableProperty] private string _reportPath = string.Empty;

    public string SuccessText => Success ? "Pass" : "Fail";
    public string TimeText => FinishedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
