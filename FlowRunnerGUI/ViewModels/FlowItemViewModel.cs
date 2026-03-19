using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowRunnerGUI.ViewModels;

public partial class FlowItemViewModel : ObservableObject
{
    [ObservableProperty] private string _flowId = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private int _variableCount;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileName(FilePath) : Name;
    public string ShortPath => System.IO.Path.GetFileName(FilePath);
}
