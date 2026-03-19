using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowDesigner;

public sealed class GraphNodeViewModel : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _title = string.Empty;
    private string _type = "CallMethod";
    private string _action = "SetVariable";
    private double _x;
    private double _y;
    private bool _isSelected;
    private int _timeoutMs = 30_000;
    private int _retry;
    private string _onError = "Stop";
    private string _inputsJson = "{}";
    private string _outputsJson = "{}";

    public string Id { get => _id; set => SetField(ref _id, value); }
    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Type { get => _type; set => SetField(ref _type, value); }
    public string Action { get => _action; set => SetField(ref _action, value); }
    public double X { get => _x; set => SetField(ref _x, value); }
    public double Y { get => _y; set => SetField(ref _y, value); }
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }
    public int TimeoutMs { get => _timeoutMs; set => SetField(ref _timeoutMs, value); }
    public int Retry { get => _retry; set => SetField(ref _retry, value); }
    public string OnError { get => _onError; set => SetField(ref _onError, value); }
    public string InputsJson { get => _inputsJson; set => SetField(ref _inputsJson, value); }
    public string OutputsJson { get => _outputsJson; set => SetField(ref _outputsJson, value); }

    public double InPortX => X;
    public double InPortY => Y + 44;
    public double OutPortX => X + 190;
    public double OutPortY => Y + 44;
    public double ThenPortX => X + 190;
    public double ThenPortY => Y + 22;
    public double ElsePortX => X + 190;
    public double ElsePortY => Y + 66;

    public bool IsIfNode => Type.Equals("If", StringComparison.OrdinalIgnoreCase);
    public bool IsMergeNode => Type.Equals("Merge", StringComparison.OrdinalIgnoreCase);

    public string NodeColor => Type.ToLowerInvariant() switch
    {
        "if" => "#7C3AED",
        "merge" => "#0D9488",
        _ => "#1F2937"
    };

    public void NotifyPortPositions()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InPortX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InPortY)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutPortX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutPortY)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThenPortX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThenPortY)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElsePortX)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElsePortY)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(X) or nameof(Y))
        {
            NotifyPortPositions();
        }

        if (propertyName == nameof(Type))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIfNode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMergeNode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NodeColor)));
        }
    }
}
