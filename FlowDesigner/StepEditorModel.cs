using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlowDesigner;

public sealed class StepEditorModel : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _type = "CallMethod";
    private string _action = "SetVariable";
    private int _timeoutMs = 30_000;
    private int _retry;
    private string _onError = "Stop";
    private string _inputsJson = "{}";
    private string _outputsJson = "{}";

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public string Action
    {
        get => _action;
        set => SetField(ref _action, value);
    }

    public int TimeoutMs
    {
        get => _timeoutMs;
        set => SetField(ref _timeoutMs, value);
    }

    public int Retry
    {
        get => _retry;
        set => SetField(ref _retry, value);
    }

    public string OnError
    {
        get => _onError;
        set => SetField(ref _onError, value);
    }

    public string InputsJson
    {
        get => _inputsJson;
        set => SetField(ref _inputsJson, value);
    }

    public string OutputsJson
    {
        get => _outputsJson;
        set => SetField(ref _outputsJson, value);
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
    }
}
