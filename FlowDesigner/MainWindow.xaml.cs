using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using ActionBuiltin;
using ActionSdk;
using FlowEngine;
using Microsoft.Win32;
using Persistence;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

namespace FlowDesigner;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ActionRegistry _actionRegistry;
    private readonly FlowRunner _runner;
    private string? _currentFlowPath;
    private StepEditorModel? _selectedStep;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());

        _actionRegistry = new ActionRegistry();
        _actionRegistry.RegisterFromAssembly(typeof(DelayAction).Assembly);
        _actionRegistry.Register(new RunScriptAction(new PythonScriptExecutor("python", Directory.GetCurrentDirectory())));
        _runner = new FlowRunner(_actionRegistry);

        Steps = [];
        NewFlow();
    }

    public ObservableCollection<StepEditorModel> Steps { get; }

    public StepEditorModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_selectedStep == value)
            {
                return;
            }

            _selectedStep = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void NewFlow_Click(object sender, RoutedEventArgs e) => NewFlow();

    private async void LoadFlow_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Flow json (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Samples")
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var repository = CreateRepository();
            var flow = await repository.LoadAsync(dialog.FileName);
            PopulateFromFlow(flow);
            _currentFlowPath = dialog.FileName;
            StatusText.Text = $"已加载: {_currentFlowPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var flow = BuildFlowFromUi();
            var dialog = new SaveFileDialog
            {
                Filter = "Flow json (*.json)|*.json|All files (*.*)|*.*",
                FileName = string.IsNullOrWhiteSpace(_currentFlowPath) ? $"{flow.FlowId}.json" : Path.GetFileName(_currentFlowPath)
            };
            if (string.IsNullOrWhiteSpace(_currentFlowPath))
            {
                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                _currentFlowPath = dialog.FileName;
            }

            var repository = CreateRepository();
            await repository.SaveAsync(flow, _currentFlowPath!);
            StatusText.Text = $"已保存: {_currentFlowPath}";
            RenderFlowJson(flow);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var flow = BuildFlowFromUi();
            var context = new FlowExecutionContext();
            var result = await _runner.RunAsync(flow, context);

            var reportWriter = new RunReportWriter();
            var reportDir = Path.Combine(Directory.GetCurrentDirectory(), "designer-reports");
            var jsonReport = await reportWriter.WriteJsonAsync(result, reportDir);
            var mdReport = await reportWriter.WriteMarkdownAsync(result, reportDir);

            var lines = new List<string>
            {
                $"RunId: {result.RunId}",
                $"Success: {result.Success}",
                $"Steps: {result.Steps.Count}",
                $"JsonReport: {jsonReport}",
                $"MdReport: {mdReport}",
                ""
            };
            foreach (var step in result.Steps)
            {
                lines.Add($"{step.StepId} | {step.Status} | {step.DurationMs}ms | {step.ErrorMessage}");
            }

            RunLogTextBox.Text = string.Join(Environment.NewLine, lines);
            StatusText.Text = "运行完成";
            RenderFlowJson(flow);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "运行失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        var index = Steps.Count + 1;
        var step = new StepEditorModel
        {
            Id = $"step_{index}",
            Name = $"Step {index}",
            Type = "CallMethod",
            Action = "SetVariable",
            TimeoutMs = 30000,
            Retry = 0,
            OnError = "Stop",
            InputsJson = "{\n  \"value\": \"demo\"\n}",
            OutputsJson = "{\n  \"value\": \"Result\"\n}"
        };
        Steps.Add(step);
        SelectedStep = step;
        StatusText.Text = "已添加步骤";
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null)
        {
            return;
        }

        var index = Steps.IndexOf(SelectedStep);
        Steps.Remove(SelectedStep);
        SelectedStep = index >= 0 && index < Steps.Count ? Steps[index] : Steps.LastOrDefault();
        StatusText.Text = "已删除步骤";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null)
        {
            return;
        }

        var index = Steps.IndexOf(SelectedStep);
        if (index <= 0)
        {
            return;
        }

        Steps.Move(index, index - 1);
        StatusText.Text = "步骤上移";
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null)
        {
            return;
        }

        var index = Steps.IndexOf(SelectedStep);
        if (index < 0 || index >= Steps.Count - 1)
        {
            return;
        }

        Steps.Move(index, index + 1);
        StatusText.Text = "步骤下移";
    }

    private void SyncJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(FlowJsonTextBox.Text))
            {
                var flow = JsonSerializer.Deserialize<FlowDefinition>(FlowJsonTextBox.Text, _jsonOptions);
                if (flow is not null)
                {
                    PopulateFromFlow(flow);
                    StatusText.Text = "已从JSON同步到可视化编辑区";
                    return;
                }
            }
        }
        catch
        {
            // Ignore and fallback to render.
        }

        RenderFlowJson(BuildFlowFromUi());
        StatusText.Text = "已从可视化编辑区同步到JSON";
    }

    private void NewFlow()
    {
        _currentFlowPath = null;
        Steps.Clear();
        Steps.Add(new StepEditorModel
        {
            Id = "step_1",
            Name = "Set Variable",
            Type = "CallMethod",
            Action = "SetVariable",
            InputsJson = "{\n  \"value\": \"hello\"\n}",
            OutputsJson = "{\n  \"value\": \"Greeting\"\n}"
        });
        SelectedStep = Steps[0];
        RunLogTextBox.Text = string.Empty;
        RenderFlowJson(BuildFlowFromUi());
        StatusText.Text = "新建流程完成";
    }

    private void PopulateFromFlow(FlowDefinition flow)
    {
        Steps.Clear();
        foreach (var step in flow.Steps)
        {
            Steps.Add(new StepEditorModel
            {
                Id = step.Id,
                Name = step.Name,
                Type = step.Type,
                Action = step.Action ?? string.Empty,
                TimeoutMs = step.TimeoutMs,
                Retry = step.Retry,
                OnError = step.OnError.ToString(),
                InputsJson = JsonSerializer.Serialize(step.Inputs, _jsonOptions),
                OutputsJson = JsonSerializer.Serialize(step.Outputs, _jsonOptions)
            });
        }

        if (Steps.Count == 0)
        {
            AddStep_Click(this, new RoutedEventArgs());
        }

        SelectedStep = Steps.FirstOrDefault();
        RenderFlowJson(flow);
    }

    private FlowDefinition BuildFlowFromUi()
    {
        var steps = new List<FlowStep>();
        foreach (var editable in Steps)
        {
            if (string.IsNullOrWhiteSpace(editable.Id))
            {
                throw new InvalidOperationException("每个步骤都必须有 Id。");
            }

            var inputs = ParseObjectNode(editable.InputsJson, "Inputs");
            var outputs = ParseStringMap(editable.OutputsJson, "Outputs");

            steps.Add(new FlowStep
            {
                Id = editable.Id,
                Name = editable.Name,
                Type = editable.Type,
                Action = string.IsNullOrWhiteSpace(editable.Action) ? null : editable.Action,
                Inputs = inputs,
                Outputs = outputs,
                TimeoutMs = editable.TimeoutMs,
                Retry = editable.Retry,
                OnError = ParseOnError(editable.OnError)
            });
        }

        return new FlowDefinition
        {
            FlowId = "designer_flow",
            Name = "Designer Flow",
            Version = "1.0.0",
            Variables = new Dictionary<string, JsonNode?>(),
            Steps = steps
        };
    }

    private Dictionary<string, JsonNode?> ParseObjectNode(string json, string fieldName)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            if (node is null)
            {
                throw new InvalidOperationException($"{fieldName} 必须是 JSON 对象。");
            }

            var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in node)
            {
                result[kv.Key] = kv.Value;
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{fieldName} JSON 格式错误: {ex.Message}");
        }
    }

    private Dictionary<string, string> ParseStringMap(string json, string fieldName)
    {
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            if (node is null)
            {
                throw new InvalidOperationException($"{fieldName} 必须是 JSON 对象。");
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in node)
            {
                result[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{fieldName} JSON 格式错误: {ex.Message}");
        }
    }

    private static OnErrorStrategy ParseOnError(string raw)
    {
        if (Enum.TryParse<OnErrorStrategy>(raw, true, out var value))
        {
            return value;
        }

        return OnErrorStrategy.Stop;
    }

    private void RenderFlowJson(FlowDefinition flow)
    {
        FlowJsonTextBox.Text = JsonSerializer.Serialize(flow, _jsonOptions);
    }

    private FlowJsonRepository CreateRepository()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "flow.schema.json");
        return new FlowJsonRepository(schemaPath);
    }
}