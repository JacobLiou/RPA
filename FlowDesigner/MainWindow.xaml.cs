using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using IOPath = System.IO.Path;
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
    private const double NodeWidth = 190;
    private const double NodeHeight = 88;

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ActionRegistry _actionRegistry;
    private readonly FlowRunner _runner;
    private string? _currentFlowPath;
    private GraphNodeViewModel? _selectedNode;
    private int _nodeCounter;

    private GraphNodeViewModel? _draggingNode;
    private Point _dragOffset;
    private bool _isDraggingNode;

    private bool _isDraggingLine;
    private string? _dragLineFromNodeId;
    private EdgeKind _dragLineKind;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());

        _actionRegistry = new ActionRegistry();
        _actionRegistry.RegisterFromAssembly(typeof(DelayAction).Assembly);
        _actionRegistry.Register(new RunScriptAction(new PythonScriptExecutor("python", Directory.GetCurrentDirectory())));
        _runner = new FlowRunner(_actionRegistry);

        Nodes = [];
        Edges = [];
        NewFlow();
    }

    public ObservableCollection<GraphNodeViewModel> Nodes { get; }
    public ObservableCollection<GraphEdgeViewModel> Edges { get; }

    public GraphNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value) return;
            if (_selectedNode is not null) _selectedNode.IsSelected = false;
            _selectedNode = value;
            if (_selectedNode is not null) _selectedNode.IsSelected = true;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    // ────────── Toolbar ──────────

    private void NewFlow_Click(object sender, RoutedEventArgs e) => NewFlow();
    private void AutoLayout_Click(object sender, RoutedEventArgs e)
    {
        AutoLayoutEngine.Layout(Nodes, Edges);
        RecalculateEdges();
        StatusText.Text = "自动布局完成";
    }

    private async void LoadFlow_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Flow JSON|*.json|All|*.*", InitialDirectory = IOPath.Combine(Directory.GetCurrentDirectory(), "Samples") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var flow = await CreateRepository().LoadAsync(dlg.FileName);
            BuildGraphFromFlow(flow);
            _currentFlowPath = dlg.FileName;
            RenderFlowJson(flow);
            StatusText.Text = $"已加载: {_currentFlowPath}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "加载失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void SaveFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var flow = BuildFlowFromGraph();
            if (string.IsNullOrWhiteSpace(_currentFlowPath))
            {
                var dlg = new SaveFileDialog { Filter = "Flow JSON|*.json|All|*.*", FileName = $"{flow.FlowId}.json" };
                if (dlg.ShowDialog() != true) return;
                _currentFlowPath = dlg.FileName;
            }
            await CreateRepository().SaveAsync(flow, _currentFlowPath!);
            RenderFlowJson(flow);
            StatusText.Text = $"已保存: {_currentFlowPath}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RunFlow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var flow = BuildFlowFromGraph();
            var result = await _runner.RunAsync(flow, new FlowExecutionContext());
            var w = new RunReportWriter();
            var dir = IOPath.Combine(Directory.GetCurrentDirectory(), "designer-reports");
            var jr = await w.WriteJsonAsync(result, dir);
            var mr = await w.WriteMarkdownAsync(result, dir);
            var lines = new List<string> { $"RunId: {result.RunId}", $"Success: {result.Success}", $"Steps: {result.Steps.Count}", $"JSON: {jr}", $"MD: {mr}", "" };
            lines.AddRange(result.Steps.Select(s => $"{s.StepId} | {s.Status} | {s.DurationMs}ms | {s.ErrorMessage}"));
            RunLogTextBox.Text = string.Join(Environment.NewLine, lines);
            RenderFlowJson(flow);
            StatusText.Text = "运行完成";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "运行失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SyncJson_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var flow = JsonSerializer.Deserialize<FlowDefinition>(FlowJsonTextBox.Text, _jsonOptions);
            if (flow is not null) { BuildGraphFromFlow(flow); StatusText.Text = "JSON->画布 同步完成"; return; }
        }
        catch { /* fallthrough */ }
        RenderFlowJson(BuildFlowFromGraph());
        StatusText.Text = "画布->JSON 同步完成";
    }

    private void DeleteNode_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is null) return;
        var rid = SelectedNode.Id;
        Nodes.Remove(SelectedNode);
        for (var i = Edges.Count - 1; i >= 0; i--)
            if (Edges[i].FromId == rid || Edges[i].ToId == rid) Edges.RemoveAt(i);
        SelectedNode = Nodes.FirstOrDefault();
        RecalculateEdges();
        StatusText.Text = "节点已删除";
    }

    // ────────── Toolbox drag ──────────

    private void ToolboxList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ToolboxList.SelectedItem is not ListBoxItem item) return;
        DragDrop.DoDragDrop(ToolboxList, item.Content?.ToString() ?? "CallMethod", DragDropEffects.Copy);
    }

    private void DesignerCanvas_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DesignerCanvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.Text)) return;
        var type = e.Data.GetData(DataFormats.Text)?.ToString() ?? "CallMethod";
        var pos = e.GetPosition(DesignerCanvas);
        AddNode(type, pos.X, pos.Y);
        StatusText.Text = $"节点 {type} 已添加";
    }

    // ────────── Canvas mouse (deselect + line preview + drop) ──────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == DesignerCanvas) SelectedNode = null;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingLine) return;
        var pos = e.GetPosition(DesignerCanvas);
        PreviewLine.X2 = pos.X;
        PreviewLine.Y2 = pos.Y;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingLine)
        {
            _isDraggingLine = false;
            PreviewLine.Visibility = Visibility.Collapsed;
            StatusText.Text = "连线取消";
        }
    }

    // ────────── Port dragging (start line from port) ──────────

    private void OutPort_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: GraphNodeViewModel node })
            StartLineDrag(node, EdgeKind.Sequence, node.OutPortX, node.OutPortY);
        e.Handled = true;
    }

    private void ThenPort_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: GraphNodeViewModel node })
            StartLineDrag(node, EdgeKind.Then, node.ThenPortX, node.ThenPortY);
        e.Handled = true;
    }

    private void ElsePort_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: GraphNodeViewModel node })
            StartLineDrag(node, EdgeKind.Else, node.ElsePortX, node.ElsePortY);
        e.Handled = true;
    }

    private void InPort_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingLine && sender is Ellipse { Tag: GraphNodeViewModel targetNode } && _dragLineFromNodeId != targetNode.Id)
        {
            AddOrReplaceEdge(_dragLineFromNodeId!, targetNode.Id, _dragLineKind);
            _isDraggingLine = false;
            PreviewLine.Visibility = Visibility.Collapsed;
            StatusText.Text = "连线已创建";
        }
        e.Handled = true;
    }

    private void StartLineDrag(GraphNodeViewModel from, EdgeKind kind, double startX, double startY)
    {
        _isDraggingLine = true;
        _dragLineFromNodeId = from.Id;
        _dragLineKind = kind;
        PreviewLine.X1 = startX;
        PreviewLine.Y1 = startY;
        PreviewLine.X2 = startX;
        PreviewLine.Y2 = startY;
        PreviewLine.Visibility = Visibility.Visible;
        SelectedNode = from;
        StatusText.Text = $"从 {from.Title} 拖线 ({kind})...";
    }

    // ────────── Node body dragging ──────────

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not GraphNodeViewModel node) return;
        SelectedNode = node;
        _draggingNode = node;
        _dragOffset = e.GetPosition(border);
        _isDraggingNode = true;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingNode || _draggingNode is null) return;
        var pos = e.GetPosition(DesignerCanvas);
        _draggingNode.X = Math.Max(0, pos.X - _dragOffset.X);
        _draggingNode.Y = Math.Max(0, pos.Y - _dragOffset.Y);
        RecalculateEdges();
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        _isDraggingNode = false;
        _draggingNode = null;
    }

    // ────────── Graph helpers ──────────

    private void AddNode(string type, double x, double y)
    {
        _nodeCounter++;
        var defaultAction = type switch { "If" => string.Empty, "Merge" => string.Empty, _ => "SetVariable" };
        var defaultInputs = type switch
        {
            "If" => "{\n  \"condition\": \"true\"\n}",
            "Merge" => "{}",
            _ => "{\n  \"value\": \"demo\"\n}"
        };
        var defaultOutputs = type switch { "If" or "Merge" => "{}", _ => "{\n  \"value\": \"Result\"\n}" };
        var node = new GraphNodeViewModel
        {
            Id = $"node_{_nodeCounter}",
            Title = type switch { "If" => $"If {_nodeCounter}", "Merge" => $"Merge {_nodeCounter}", _ => $"Step {_nodeCounter}" },
            Type = type,
            Action = defaultAction,
            X = x,
            Y = y,
            InputsJson = defaultInputs,
            OutputsJson = defaultOutputs
        };
        Nodes.Add(node);
        SelectedNode = node;
    }

    private void AddOrReplaceEdge(string fromId, string toId, EdgeKind kind)
    {
        for (var i = Edges.Count - 1; i >= 0; i--)
            if (Edges[i].FromId == fromId && Edges[i].Kind == kind) Edges.RemoveAt(i);
        Edges.Add(new GraphEdgeViewModel { FromId = fromId, ToId = toId, Kind = kind });
        RecalculateEdges();
    }

    private void RecalculateEdges()
    {
        var map = Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in Edges)
        {
            if (!map.TryGetValue(edge.FromId, out var from) || !map.TryGetValue(edge.ToId, out var to)) continue;
            switch (edge.Kind)
            {
                case EdgeKind.Then:
                    edge.X1 = from.ThenPortX; edge.Y1 = from.ThenPortY;
                    break;
                case EdgeKind.Else:
                    edge.X1 = from.ElsePortX; edge.Y1 = from.ElsePortY;
                    break;
                default:
                    edge.X1 = from.OutPortX; edge.Y1 = from.OutPortY;
                    break;
            }
            edge.X2 = to.InPortX;
            edge.Y2 = to.InPortY;
        }
    }

    // ────────── Graph <-> FlowDefinition ──────────

    private void NewFlow()
    {
        _currentFlowPath = null;
        _nodeCounter = 0;
        Nodes.Clear();
        Edges.Clear();
        AddNode("CallMethod", 160, 200);
        RunLogTextBox.Text = string.Empty;
        RenderFlowJson(BuildFlowFromGraph());
        StatusText.Text = "新建流程完成";
    }

    private FlowDefinition BuildFlowFromGraph()
    {
        if (Nodes.Count == 0) throw new InvalidOperationException("至少添加一个节点。");
        var incoming = Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Edges) if (incoming.ContainsKey(e.ToId)) incoming[e.ToId]++;
        var entry = Nodes.Where(n => incoming[n.Id] == 0).OrderBy(n => n.X).ThenBy(n => n.Y).FirstOrDefault()
                    ?? Nodes.OrderBy(n => n.X).ThenBy(n => n.Y).First();
        return new FlowDefinition
        {
            FlowId = "designer_flow",
            Name = "Designer Flow",
            Version = "1.0.0",
            Variables = new Dictionary<string, JsonNode?>(),
            Steps = BuildStepChain(entry.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
    }

    private List<FlowStep> BuildStepChain(string? startId, HashSet<string> visited)
    {
        var map = Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var steps = new List<FlowStep>();
        var cur = startId;
        while (!string.IsNullOrWhiteSpace(cur) && map.TryGetValue(cur, out var node) && !visited.Contains(cur))
        {
            visited.Add(cur);

            if (node.Type.Equals("Merge", StringComparison.OrdinalIgnoreCase))
            {
                cur = Edges.FirstOrDefault(e => e.FromId == cur && e.Kind == EdgeKind.Sequence)?.ToId;
                continue;
            }

            List<FlowStep> thenSteps = [], elseSteps = [];
            if (node.Type.Equals("If", StringComparison.OrdinalIgnoreCase))
            {
                var thenId = Edges.FirstOrDefault(e => e.FromId == cur && e.Kind == EdgeKind.Then)?.ToId;
                var elseId = Edges.FirstOrDefault(e => e.FromId == cur && e.Kind == EdgeKind.Else)?.ToId;
                thenSteps = BuildStepChain(thenId, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
                elseSteps = BuildStepChain(elseId, new HashSet<string>(visited, StringComparer.OrdinalIgnoreCase));
            }

            steps.Add(ConvertNodeToStep(node, thenSteps, elseSteps));
            cur = Edges.FirstOrDefault(e => e.FromId == cur && e.Kind == EdgeKind.Sequence)?.ToId;
        }
        return steps;
    }

    private FlowStep ConvertNodeToStep(GraphNodeViewModel node, List<FlowStep>? then = null, List<FlowStep>? @else = null)
    {
        return new FlowStep
        {
            Id = node.Id,
            Name = node.Title,
            Type = node.Type,
            Action = string.IsNullOrWhiteSpace(node.Action) ? null : node.Action,
            Inputs = ParseObjectNode(node.InputsJson, "Inputs"),
            Outputs = ParseStringMap(node.OutputsJson, "Outputs"),
            TimeoutMs = node.TimeoutMs,
            Retry = node.Retry,
            OnError = ParseOnError(node.OnError),
            ThenSteps = then ?? [],
            ElseSteps = @else ?? []
        };
    }

    private void BuildGraphFromFlow(FlowDefinition flow)
    {
        Nodes.Clear(); Edges.Clear(); _nodeCounter = 0;
        AddStepsToGraph(flow.Steps, 160, 200);
        AutoLayoutEngine.Layout(Nodes, Edges);
        RecalculateEdges();
    }

    private string? AddStepsToGraph(List<FlowStep> steps, double x, double y)
    {
        GraphNodeViewModel? prev = null;
        GraphNodeViewModel? first = null;
        foreach (var step in steps)
        {
            _nodeCounter++;
            var node = new GraphNodeViewModel
            {
                Id = step.Id,
                Title = string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name,
                Type = step.Type,
                Action = step.Action ?? string.Empty,
                X = x, Y = y,
                TimeoutMs = step.TimeoutMs,
                Retry = step.Retry,
                OnError = step.OnError.ToString(),
                InputsJson = JsonSerializer.Serialize(step.Inputs, _jsonOptions),
                OutputsJson = JsonSerializer.Serialize(step.Outputs, _jsonOptions)
            };
            Nodes.Add(node);
            first ??= node;
            if (prev is not null) Edges.Add(new GraphEdgeViewModel { FromId = prev.Id, ToId = node.Id, Kind = EdgeKind.Sequence });
            if (step.Type.Equals("If", StringComparison.OrdinalIgnoreCase))
            {
                var tId = AddStepsToGraph(step.ThenSteps, x + 260, y - 150);
                var eId = AddStepsToGraph(step.ElseSteps, x + 260, y + 150);
                if (tId is not null) Edges.Add(new GraphEdgeViewModel { FromId = node.Id, ToId = tId, Kind = EdgeKind.Then });
                if (eId is not null) Edges.Add(new GraphEdgeViewModel { FromId = node.Id, ToId = eId, Kind = EdgeKind.Else });
            }
            prev = node;
            x += 240;
        }
        SelectedNode = first;
        return first?.Id;
    }

    // ────────── JSON helpers ──────────

    private Dictionary<string, JsonNode?> ParseObjectNode(string json, string field)
    {
        try
        {
            var obj = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject
                      ?? throw new InvalidOperationException($"{field} 必须是 JSON 对象。");
            var r = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj) r[kv.Key] = kv.Value;
            return r;
        }
        catch (JsonException ex) { throw new InvalidOperationException($"{field} JSON 错误: {ex.Message}"); }
    }

    private Dictionary<string, string> ParseStringMap(string json, string field)
    {
        try
        {
            var obj = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject
                      ?? throw new InvalidOperationException($"{field} 必须是 JSON 对象。");
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj) r[kv.Key] = kv.Value?.ToString() ?? string.Empty;
            return r;
        }
        catch (JsonException ex) { throw new InvalidOperationException($"{field} JSON 错误: {ex.Message}"); }
    }

    private static OnErrorStrategy ParseOnError(string raw) => Enum.TryParse<OnErrorStrategy>(raw, true, out var v) ? v : OnErrorStrategy.Stop;
    private void RenderFlowJson(FlowDefinition flow) => FlowJsonTextBox.Text = JsonSerializer.Serialize(flow, _jsonOptions);
    private FlowJsonRepository CreateRepository() => new(IOPath.Combine(AppContext.BaseDirectory, "flow.schema.json"));
}
