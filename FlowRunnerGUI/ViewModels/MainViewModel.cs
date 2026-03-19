using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowEngine;
using FlowRunnerGUI.Services;
using Microsoft.Win32;
using Persistence;

namespace FlowRunnerGUI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly FlowExecutionService _executionService;
    private readonly FlowJsonRepository _repository;
    private readonly RunReportWriter _reportWriter = new();
    private readonly string _reportDir;
    private CancellationTokenSource? _runCts;

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ObservableCollection<FlowItemViewModel> Flows { get; } = [];
    public ObservableCollection<StepResultViewModel> StepResults { get; } = [];
    public ObservableCollection<RunHistoryItemViewModel> RunHistory { get; } = [];

    [ObservableProperty] private FlowItemViewModel? _selectedFlow;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex;

    private FlowDefinition? _loadedDefinition;
    private FlowRunResult? _lastRunResult;

    public MainViewModel()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "flow.schema.json");
        _repository = new FlowJsonRepository(schemaPath);
        _executionService = new FlowExecutionService();
        _reportDir = Path.Combine(Directory.GetCurrentDirectory(), "run-reports");
        Directory.CreateDirectory(_reportDir);

        LoadHistory();
    }

    partial void OnSelectedFlowChanged(FlowItemViewModel? value)
    {
        if (value is null)
        {
            _loadedDefinition = null;
            return;
        }

        try
        {
            var task = _repository.LoadAsync(value.FilePath);
            task.Wait();
            _loadedDefinition = task.Result;
            StatusMessage = $"Loaded: {_loadedDefinition.Name}";
        }
        catch (Exception ex)
        {
            _loadedDefinition = null;
            StatusMessage = $"Load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Flow Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            ScanFolder(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Flow JSON",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                AddFlowFile(file);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunFlow))]
    private async Task RunFlowAsync()
    {
        if (_loadedDefinition is null || SelectedFlow is null) return;

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        StepResults.Clear();
        LogText = string.Empty;
        ProgressValue = 0;
        SelectedTabIndex = 0;

        var totalSteps = CountStepsRecursive(_loadedDefinition.Steps);
        ProgressMax = Math.Max(totalSteps, 1);
        StatusMessage = $"Running: {_loadedDefinition.Name}...";
        AppendLog($"=== Start: {_loadedDefinition.Name} ===");

        try
        {
            _lastRunResult = await _executionService.RunAsync(
                _loadedDefinition,
                OnStepCompleted,
                _runCts.Token);

            StatusMessage = _lastRunResult.Success
                ? $"Completed: {_lastRunResult.FlowName} - ALL PASS"
                : $"Completed: {_lastRunResult.FlowName} - HAS FAILURES";

            AppendLog($"=== Finished: {(_lastRunResult.Success ? "SUCCESS" : "FAILURE")} ===");

            await SaveReport(_lastRunResult);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Run cancelled.";
            AppendLog("=== Cancelled ===");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Run error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            RunFlowCommand.NotifyCanExecuteChanged();
            StopFlowCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunFlow() => !IsRunning && _loadedDefinition is not null;

    [RelayCommand(CanExecute = nameof(CanStopFlow))]
    private void StopFlow()
    {
        _runCts?.Cancel();
        StatusMessage = "Stopping...";
    }

    private bool CanStopFlow() => IsRunning;

    [RelayCommand]
    private void ViewHistoryDetail(RunHistoryItemViewModel? item)
    {
        if (item is null || !File.Exists(item.ReportPath)) return;

        try
        {
            var json = File.ReadAllText(item.ReportPath);
            var record = JsonSerializer.Deserialize<RunRecord>(json, JsonReadOptions);
            if (record is null) return;

            StepResults.Clear();
            foreach (var step in record.Steps)
            {
                StepResults.Add(new StepResultViewModel(step));
            }

            ProgressMax = Math.Max(record.Steps.Count, 1);
            ProgressValue = record.Steps.Count;
            StatusMessage = $"History: {record.FlowName} ({(record.Success ? "PASS" : "FAIL")}) - {record.RunId[..8]}";
            SelectedTabIndex = 0;
        }
        catch (Exception ex)
        {
            StatusMessage = $"History load error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogText = string.Empty;
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunFlowCommand.NotifyCanExecuteChanged();
        StopFlowCommand.NotifyCanExecuteChanged();
    }

    private void OnStepCompleted(StepExecutionResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StepResults.Add(new StepResultViewModel(result));
            ProgressValue = StepResults.Count;

            var icon = result.Status == StepStatus.Success ? "PASS" : "FAIL";
            var error = string.IsNullOrEmpty(result.ErrorMessage) ? "" : $" | {result.ErrorMessage}";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] {icon} {result.StepId} ({result.StepType}) {result.DurationMs}ms{error}");
        });
    }

    private async Task SaveReport(FlowRunResult result)
    {
        try
        {
            await _reportWriter.WriteJsonAsync(result, _reportDir);
            await _reportWriter.WriteMarkdownAsync(result, _reportDir);
            LoadHistory();
        }
        catch (Exception ex)
        {
            AppendLog($"Report save error: {ex.Message}");
        }
    }

    private void ScanFolder(string folder)
    {
        Flows.Clear();
        var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            AddFlowFile(file);
        }

        StatusMessage = $"Found {Flows.Count} flow(s) in {folder}";
    }

    private void AddFlowFile(string filePath)
    {
        if (Flows.Any(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("flowId", out _) || !root.TryGetProperty("steps", out _))
                return;

            var item = new FlowItemViewModel
            {
                FlowId = root.TryGetProperty("flowId", out var fid) ? fid.GetString() ?? "" : "",
                Name = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : Path.GetFileName(filePath),
                Version = root.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                FilePath = filePath,
                StepCount = root.TryGetProperty("steps", out var steps) ? steps.GetArrayLength() : 0,
                VariableCount = root.TryGetProperty("variables", out var vars) ? CountJsonProperties(vars) : 0
            };

            Flows.Add(item);
        }
        catch
        {
            // skip non-flow JSON files
        }
    }

    private void LoadHistory()
    {
        RunHistory.Clear();
        if (!Directory.Exists(_reportDir)) return;

        var files = Directory.GetFiles(_reportDir, "*.json")
            .OrderByDescending(File.GetLastWriteTime);

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("runId", out _)) continue;

                var item = new RunHistoryItemViewModel
                {
                    RunId = root.TryGetProperty("runId", out var rid) ? rid.GetString() ?? "" : "",
                    FlowId = root.TryGetProperty("flowId", out var fid) ? fid.GetString() ?? "" : "",
                    FlowName = root.TryGetProperty("flowName", out var fn) ? fn.GetString() ?? "" : "",
                    Success = root.TryGetProperty("success", out var s) && s.GetBoolean(),
                    StepCount = root.TryGetProperty("steps", out var steps) ? steps.GetArrayLength() : 0,
                    FinishedAt = root.TryGetProperty("finishedAt", out var fa) ? fa.GetDateTimeOffset() : DateTimeOffset.MinValue,
                    ReportPath = file
                };

                RunHistory.Add(item);
            }
            catch
            {
                // skip malformed report files
            }
        }
    }

    private void AppendLog(string message)
    {
        LogText += message + Environment.NewLine;
    }

    private static int CountStepsRecursive(IReadOnlyList<FlowStep> steps)
    {
        var count = 0;
        foreach (var step in steps)
        {
            count++;
            count += CountStepsRecursive(step.ThenSteps);
            count += CountStepsRecursive(step.ElseSteps);
            count += CountStepsRecursive(step.BodySteps);
            count += CountStepsRecursive(step.TrySteps);
            count += CountStepsRecursive(step.CatchSteps);
        }
        return count;
    }

    private static int CountJsonProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        var count = 0;
        foreach (var _ in element.EnumerateObject()) count++;
        return count;
    }

    private sealed class RunRecord
    {
        public string RunId { get; set; } = "";
        public string FlowId { get; set; } = "";
        public string FlowName { get; set; } = "";
        public bool Success { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public List<StepExecutionResult> Steps { get; set; } = [];
    }
}
