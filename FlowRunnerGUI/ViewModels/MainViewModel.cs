using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlowEngine;
using FlowRunnerGUI.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Persistence;

namespace FlowRunnerGUI.ViewModels;

public enum ExecutionState
{
    Idle,
    Running,
    Paused
}

public partial class MainViewModel : ObservableObject
{
    private readonly FlowExecutionService _executionService;
    private readonly FlowJsonRepository _repository;
    private readonly RunReportWriter _reportWriter;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MainViewModel> _logger;
    private readonly string _reportDir;
    private readonly ConcurrentDictionary<string, byte> _activeBreakpoints = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;
    private string _projectRoot = Directory.GetCurrentDirectory();

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ObservableCollection<FlowItemViewModel> Flows { get; } = [];
    public ObservableCollection<StepResultViewModel> StepResults { get; } = [];
    public ObservableCollection<RunHistoryItemViewModel> RunHistory { get; } = [];
    public ObservableCollection<FlowStepPreviewViewModel> FlowSteps { get; } = [];

    [ObservableProperty] private FlowItemViewModel? _selectedFlow;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private StepResultViewModel? _selectedStepResult;
    [ObservableProperty] private FlowStepPreviewViewModel? _selectedFlowStep;
    [ObservableProperty] private ExecutionState _executionState = ExecutionState.Idle;

    private FlowDefinition? _loadedDefinition;
    private FlowRunResult? _lastRunResult;

    public MainViewModel()
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
            builder.AddFile(Path.Combine(AppContext.BaseDirectory, "logs", "rpa-gui-{Date}.log"));
            builder.AddProvider(new GuiLoggerProvider(msg =>
                Application.Current?.Dispatcher?.BeginInvoke(() => AppendLog(msg))));
        });
        _logger = _loggerFactory.CreateLogger<MainViewModel>();

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "flow.schema.json");
        _repository = new FlowJsonRepository(schemaPath, _loggerFactory.CreateLogger<FlowJsonRepository>());
        _executionService = new FlowExecutionService(_loggerFactory);
        _reportWriter = new RunReportWriter(_loggerFactory.CreateLogger<RunReportWriter>());
        _reportDir = Path.Combine(Directory.GetCurrentDirectory(), "run-reports");
        Directory.CreateDirectory(_reportDir);

        LoadHistory();
    }

    partial void OnSelectedFlowChanged(FlowItemViewModel? value)
    {
        if (value is null)
        {
            _loadedDefinition = null;
            FlowSteps.Clear();
            RunFlowCommand.NotifyCanExecuteChanged();
            return;
        }

        _ = LoadSelectedFlowAsync(value);
    }

    private async Task LoadSelectedFlowAsync(FlowItemViewModel item)
    {
        try
        {
            _loadedDefinition = await Task.Run(() => _repository.LoadAsync(item.FilePath));
            StatusMessage = $"Loaded: {_loadedDefinition.Name}";
            PopulateFlowSteps(_loadedDefinition);
            RunFlowCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _loadedDefinition = null;
            FlowSteps.Clear();
            StatusMessage = $"Load error: {ex.Message}";
            RunFlowCommand.NotifyCanExecuteChanged();
        }
    }

    private void PopulateFlowSteps(FlowDefinition definition)
    {
        FlowSteps.Clear();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            FlowSteps.Add(new FlowStepPreviewViewModel
            {
                StepId = step.Id,
                StepName = step.Name,
                StepType = step.Type,
                Action = step.Action ?? step.Type,
                Index = i
            });
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
            if (dialog.FileNames.Length > 0)
                _projectRoot = Path.GetDirectoryName(dialog.FileNames[0]) ?? _projectRoot;
            foreach (var file in dialog.FileNames)
            {
                AddFlowFile(file);
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunFlow))]
    private async Task RunFlowAsync()
    {
        await RunFlowCoreAsync(startStepId: null, stepMode: false);
    }

    private bool CanRunFlow() => ExecutionState == ExecutionState.Idle && _loadedDefinition is not null;

    [RelayCommand(CanExecute = nameof(CanRunFromStep))]
    private async Task RunFromStep(string? stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId)) return;
        await RunFlowCoreAsync(startStepId: stepId, stepMode: false);
    }

    private bool CanRunFromStep(string? stepId) => ExecutionState == ExecutionState.Idle && _loadedDefinition is not null && !string.IsNullOrEmpty(stepId);

    [RelayCommand(CanExecute = nameof(CanStepOnce))]
    private async Task StepOnceAsync()
    {
        if (ExecutionState == ExecutionState.Idle)
        {
            await RunFlowCoreAsync(startStepId: null, stepMode: true);
            return;
        }

        if (ExecutionState == ExecutionState.Paused)
        {
            ReleaseStepGateOnce();
        }
    }

    private bool CanStepOnce() => (ExecutionState == ExecutionState.Idle && _loadedDefinition is not null)
                                  || ExecutionState == ExecutionState.Paused;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        var ctx = _executionService.CurrentContext;
        if (ctx is null) return;
        ctx.StepMode = true;
        ExecutionState = ExecutionState.Paused;
        StatusMessage = "Paused (step mode)";
        NotifyDebugCommandStates();
    }

    private bool CanPause() => ExecutionState == ExecutionState.Running;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        var ctx = _executionService.CurrentContext;
        if (ctx is null) return;
        ctx.StepMode = false;
        ExecutionState = ExecutionState.Running;
        StatusMessage = $"Running: {_loadedDefinition?.Name}...";
        ReleaseStepGateOnce();
        NotifyDebugCommandStates();
    }

    private bool CanResume() => ExecutionState == ExecutionState.Paused;

    [RelayCommand(CanExecute = nameof(CanStopFlow))]
    private void StopFlow()
    {
        _executionService.CurrentContext?.RequestStop();
        _runCts?.Cancel();
        StatusMessage = "Stopping...";
    }

    private bool CanStopFlow() => ExecutionState != ExecutionState.Idle;

    [RelayCommand]
    private async Task StartOrContinueAsync()
    {
        if (ExecutionState == ExecutionState.Idle && _loadedDefinition is not null)
        {
            await RunFlowCoreAsync(startStepId: null, stepMode: false);
        }
        else if (ExecutionState == ExecutionState.Paused)
        {
            Resume();
        }
    }

    [RelayCommand]
    private void ToggleBreakpoint(FlowStepPreviewViewModel? step)
    {
        if (step is null) return;
        step.HasBreakpoint = !step.HasBreakpoint;
        if (step.HasBreakpoint)
            _activeBreakpoints.TryAdd(step.StepId, 0);
        else
            _activeBreakpoints.TryRemove(step.StepId, out _);
    }

    [RelayCommand]
    private void ToggleSelectedBreakpoint()
    {
        ToggleBreakpoint(SelectedFlowStep);
    }

    [RelayCommand]
    private void ClearAllBreakpoints()
    {
        _activeBreakpoints.Clear();
        foreach (var step in FlowSteps)
            step.HasBreakpoint = false;
    }

    private void ReleaseStepGateOnce()
    {
        var ctx = _executionService.CurrentContext;
        if (ctx is not null && ctx.StepGate.CurrentCount == 0)
        {
            ctx.StepGate.Release();
        }
    }

    private void NotifyDebugCommandStates()
    {
        RunFlowCommand.NotifyCanExecuteChanged();
        StopFlowCommand.NotifyCanExecuteChanged();
        StepOnceCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RunFromStepCommand.NotifyCanExecuteChanged();
    }

    private async Task RunFlowCoreAsync(string? startStepId, bool stepMode)
    {
        if (_loadedDefinition is null || SelectedFlow is null) return;

        IsRunning = true;
        ExecutionState = stepMode ? ExecutionState.Paused : ExecutionState.Running;
        _runCts = new CancellationTokenSource();
        StepResults.Clear();
        SelectedStepResult = null;
        LogText = string.Empty;
        ProgressValue = 0;
        SelectedTabIndex = 0;
        NotifyDebugCommandStates();

        var totalSteps = CountStepsRecursive(_loadedDefinition.Steps);
        ProgressMax = Math.Max(totalSteps, 1);

        var modeLabel = stepMode ? " (step mode)" : "";
        var fromLabel = startStepId is not null ? $" from {startStepId}" : "";
        StatusMessage = stepMode ? "Paused (step mode) - press Step to advance"
                                 : $"Running: {_loadedDefinition.Name}{fromLabel}...";
        _logger.LogInformation("=== Start: {FlowName}{FromLabel}{ModeLabel} ===", _loadedDefinition.Name, fromLabel, modeLabel);

        ClearCurrentStepHighlight();

        if (stepMode)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                ReleaseStepGateOnce();
            });
        }

        try
        {
            var definition = _loadedDefinition;
            var cts = _runCts;
            _lastRunResult = await Task.Run(() =>
                _executionService.RunAsync(definition, OnStepCompleted, _projectRoot,
                    startStepId, stepMode,
                    checkBreakpoint: stepId => _activeBreakpoints.ContainsKey(stepId),
                    onBeforeStep: OnBeforeStep,
                    onBreakpointHit: OnBreakpointHit,
                    cts.Token));

            StatusMessage = _lastRunResult.Success
                ? $"Completed: {_lastRunResult.FlowName} - ALL PASS"
                : $"Completed: {_lastRunResult.FlowName} - HAS FAILURES";

            _logger.LogInformation("=== Finished: {Result} ===", _lastRunResult.Success ? "SUCCESS" : "FAILURE");

            await SaveReport(_lastRunResult);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Run cancelled.";
            _logger.LogWarning("=== Cancelled ===");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Run error: {ex.Message}";
            _logger.LogError(ex, "Run error");
        }
        finally
        {
            IsRunning = false;
            ExecutionState = ExecutionState.Idle;
            _runCts?.Dispose();
            _runCts = null;
            ClearCurrentStepHighlight();
            NotifyDebugCommandStates();
        }
    }

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
        NotifyDebugCommandStates();
    }

    private void OnStepCompleted(StepExecutionResult result)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var vm = new StepResultViewModel(result);
            StepResults.Add(vm);
            ProgressValue = StepResults.Count;
            SelectedStepResult = vm;

            if (ExecutionState == ExecutionState.Paused)
            {
                StatusMessage = $"Paused after: {result.StepId} ({result.Status})";
            }
        });
    }

    private void OnBeforeStep(string stepId)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() => HighlightCurrentStep(stepId));
    }

    private void OnBreakpointHit(string stepId)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            ExecutionState = ExecutionState.Paused;
            StatusMessage = $"Breakpoint hit: {stepId}";
            _logger.LogInformation("Breakpoint hit: {StepId}", stepId);
            NotifyDebugCommandStates();
        });
    }

    private void HighlightCurrentStep(string stepId)
    {
        foreach (var s in FlowSteps)
            s.IsCurrentStep = s.StepId == stepId;
    }

    private void ClearCurrentStepHighlight()
    {
        foreach (var s in FlowSteps)
            s.IsCurrentStep = false;
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
            _logger.LogError(ex, "Report save error");
        }
    }

    private void ScanFolder(string folder)
    {
        Flows.Clear();
        _projectRoot = folder;
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
