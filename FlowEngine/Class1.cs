using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ActionSdk;

namespace FlowEngine;

public static class ErrorCodes
{
    public const string InvalidDefinition = "FLOW_INVALID_DEFINITION";
    public const string ActionFailed = "ACTION_FAILED";
    public const string Timeout = "STEP_TIMEOUT";
    public const string VariableNotFound = "VARIABLE_NOT_FOUND";
}

public enum StepStatus
{
    Success,
    Failed,
    Skipped,
    Timeout
}

public enum OnErrorStrategy
{
    Stop,
    Continue
}

public sealed class FlowDefinition
{
    public required string FlowId { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public Dictionary<string, JsonNode?> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FlowStep> Steps { get; init; } = [];
}

public sealed class FlowStep
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Action { get; init; }
    public Dictionary<string, JsonNode?> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Outputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutMs { get; init; } = 30_000;
    public int Retry { get; init; }
    public OnErrorStrategy OnError { get; init; } = OnErrorStrategy.Stop;
    public List<FlowStep> ThenSteps { get; init; } = [];
    public List<FlowStep> ElseSteps { get; init; } = [];
    public List<FlowStep> BodySteps { get; init; } = [];
    public List<FlowStep> TrySteps { get; init; } = [];
    public List<FlowStep> CatchSteps { get; init; } = [];
}

public sealed class StepExecutionResult
{
    public required string StepId { get; init; }
    public required string StepType { get; init; }
    public required string StepName { get; init; }
    public StepStatus Status { get; set; }
    public long DurationMs { get; set; }
    public Dictionary<string, object?> InputSnapshot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> OutputSnapshot { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
}

public sealed class FlowRunResult
{
    public required string RunId { get; init; }
    public required string FlowId { get; init; }
    public required string FlowName { get; init; }
    public bool Success { get; init; }
    public List<StepExecutionResult> Steps { get; init; } = [];
}

public sealed class ExecutionContext
{
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<StepExecutionResult> StepResults { get; } = [];
    public int? StartStepIndex { get; init; }
    public bool StopRequested { get; private set; }

    public void RequestStop() => StopRequested = true;
}

public static class VariableResolver
{
    private static readonly Regex PlaceholderRegex = new(@"\{\{(?<name>[^{}]+)\}\}", RegexOptions.Compiled);

    public static object? ResolveNode(JsonNode? node, IReadOnlyDictionary<string, object?> variables)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var str))
            {
                return ResolveString(str, variables);
            }

            return value.Deserialize<object>();
        }

        if (node is JsonObject obj)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj)
            {
                result[kv.Key] = ResolveNode(kv.Value, variables);
            }

            return result;
        }

        if (node is JsonArray arr)
        {
            var result = new List<object?>();
            foreach (var item in arr)
            {
                result.Add(ResolveNode(item, variables));
            }

            return result;
        }

        return node.Deserialize<object>();
    }

    public static string ResolveString(string input, IReadOnlyDictionary<string, object?> variables)
    {
        return PlaceholderRegex.Replace(input, match =>
        {
            var name = match.Groups["name"].Value.Trim();
            if (!variables.TryGetValue(name, out var value))
            {
                throw new KeyNotFoundException($"{ErrorCodes.VariableNotFound}:{name}");
            }

            return value?.ToString() ?? string.Empty;
        });
    }
}

public sealed class FlowRunner
{
    private readonly ActionRegistry _registry;

    public FlowRunner(ActionRegistry registry)
    {
        _registry = registry;
    }

    public async Task<FlowRunResult> RunAsync(
        FlowDefinition definition,
        ExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);
        context ??= new ExecutionContext();

        foreach (var kv in definition.Variables)
        {
            context.Variables[kv.Key] = VariableResolver.ResolveNode(kv.Value, context.Variables);
        }

        var startIndex = context.StartStepIndex ?? 0;
        for (var i = startIndex; i < definition.Steps.Count; i++)
        {
            if (context.StopRequested || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var shouldStop = await ExecuteStepWithPolicyAsync(definition.Steps[i], context, cancellationToken);
            if (shouldStop)
            {
                break;
            }
        }

        return new FlowRunResult
        {
            RunId = context.RunId,
            FlowId = definition.FlowId,
            FlowName = definition.Name,
            Success = context.StepResults.All(s => s.Status is StepStatus.Success or StepStatus.Skipped),
            Steps = context.StepResults
        };
    }

    private static void ValidateDefinition(FlowDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.FlowId) || string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new InvalidOperationException(ErrorCodes.InvalidDefinition);
        }
    }

    private async Task<bool> ExecuteStepWithPolicyAsync(
        FlowStep step,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var retries = Math.Max(0, step.Retry);
        var attempts = retries + 1;
        StepExecutionResult? lastResult = null;

        for (var i = 0; i < attempts; i++)
        {
            lastResult = await ExecuteSingleStepAsync(step, context, cancellationToken);
            var isSuccess = lastResult.Status == StepStatus.Success;
            if (isSuccess)
            {
                return false;
            }

            if (i < attempts - 1 && context.StepResults.Count > 0)
            {
                context.StepResults.RemoveAt(context.StepResults.Count - 1);
            }
        }

        return step.OnError == OnErrorStrategy.Stop;
    }

    private async Task<StepExecutionResult> ExecuteSingleStepAsync(
        FlowStep step,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var result = new StepExecutionResult
        {
            StepId = step.Id,
            StepType = step.Type,
            StepName = string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name,
            StartedAt = startedAt
        };

        try
        {
            switch (step.Type.ToLowerInvariant())
            {
                case "if":
                    await ExecuteIfAsync(step, context, cancellationToken);
                    result.Status = StepStatus.Success;
                    break;
                case "foreach":
                    await ExecuteForEachAsync(step, context, cancellationToken);
                    result.Status = StepStatus.Success;
                    break;
                case "trycatch":
                    await ExecuteTryCatchAsync(step, context, cancellationToken);
                    result.Status = StepStatus.Success;
                    break;
                default:
                    await ExecuteActionStepAsync(step, context, result, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = StepStatus.Timeout;
            result.ErrorCode = ErrorCodes.Timeout;
            result.ErrorMessage = "Step timed out or was cancelled.";
        }
        catch (Exception ex)
        {
            result.Status = StepStatus.Failed;
            result.ErrorCode = ErrorCodes.ActionFailed;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            watch.Stop();
            result.DurationMs = watch.ElapsedMilliseconds;
            result.EndedAt = DateTimeOffset.UtcNow;
            context.StepResults.Add(result);
        }

        return result;
    }

    private async Task ExecuteActionStepAsync(
        FlowStep step,
        ExecutionContext context,
        StepExecutionResult stepResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Action))
        {
            throw new InvalidOperationException($"Step '{step.Id}' must specify action.");
        }

        var resolvedInputs = ResolveInputs(step.Inputs, context.Variables);
        stepResult.InputSnapshot = resolvedInputs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, step.TimeoutMs)));
        var handler = _registry.Resolve(step.Action);
        var actionResult = await handler.ExecuteAsync(
            new ActionRequest
            {
                StepId = step.Id,
                StepName = step.Name,
                TimeoutMs = step.TimeoutMs,
                Inputs = resolvedInputs,
                Variables = new Dictionary<string, object?>(context.Variables, StringComparer.OrdinalIgnoreCase)
            },
            timeoutCts.Token);

        if (!actionResult.Success)
        {
            stepResult.Status = StepStatus.Failed;
            stepResult.ErrorCode = actionResult.ErrorCode ?? ErrorCodes.ActionFailed;
            stepResult.ErrorMessage = actionResult.ErrorMessage ?? "Action failed.";
            return;
        }

        stepResult.Status = StepStatus.Success;
        stepResult.OutputSnapshot = actionResult.Outputs;
        ApplyOutputs(step, actionResult, context);
    }

    private async Task ExecuteIfAsync(FlowStep step, ExecutionContext context, CancellationToken cancellationToken)
    {
        if (!step.Inputs.TryGetValue("condition", out var conditionNode))
        {
            throw new InvalidOperationException("If step requires inputs.condition.");
        }

        var condition = VariableResolver.ResolveNode(conditionNode, context.Variables);
        var value = ConvertToBoolean(condition);
        var branch = value ? step.ThenSteps : step.ElseSteps;

        foreach (var child in branch)
        {
            var shouldStop = await ExecuteStepWithPolicyAsync(child, context, cancellationToken);
            if (shouldStop)
            {
                break;
            }
        }
    }

    private async Task ExecuteForEachAsync(FlowStep step, ExecutionContext context, CancellationToken cancellationToken)
    {
        if (!step.Inputs.TryGetValue("items", out var itemsNode))
        {
            throw new InvalidOperationException("ForEach step requires inputs.items.");
        }

        var resolved = VariableResolver.ResolveNode(itemsNode, context.Variables);
        var items = resolved as IEnumerable<object?>;
        if (items is null)
        {
            throw new InvalidOperationException("ForEach inputs.items must be array.");
        }

        var variableName = "CurrentItem";
        if (step.Inputs.TryGetValue("itemName", out var itemNameNode))
        {
            variableName = VariableResolver.ResolveNode(itemNameNode, context.Variables)?.ToString() ?? variableName;
        }

        foreach (var item in items)
        {
            context.Variables[variableName] = item;
            foreach (var child in step.BodySteps)
            {
                var shouldStop = await ExecuteStepWithPolicyAsync(child, context, cancellationToken);
                if (shouldStop)
                {
                    return;
                }
            }
        }
    }

    private async Task ExecuteTryCatchAsync(FlowStep step, ExecutionContext context, CancellationToken cancellationToken)
    {
        var shouldRunCatch = false;
        try
        {
            foreach (var child in step.TrySteps)
            {
                var shouldStop = await ExecuteStepWithPolicyAsync(child, context, cancellationToken);
                if (shouldStop)
                {
                    shouldRunCatch = true;
                    break;
                }
            }
        }
        catch
        {
            shouldRunCatch = true;
        }

        if (shouldRunCatch)
        {
            foreach (var child in step.CatchSteps)
            {
                var shouldStop = await ExecuteStepWithPolicyAsync(child, context, cancellationToken);
                if (shouldStop)
                {
                    return;
                }
            }
        }
    }

    private static Dictionary<string, object?> ResolveInputs(
        Dictionary<string, JsonNode?> inputs,
        Dictionary<string, object?> variables)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in inputs)
        {
            result[kv.Key] = VariableResolver.ResolveNode(kv.Value, variables);
        }

        return result;
    }

    private static void ApplyOutputs(FlowStep step, ActionResult actionResult, ExecutionContext context)
    {
        foreach (var mapping in step.Outputs)
        {
            if (actionResult.Outputs.TryGetValue(mapping.Key, out var outputValue))
            {
                context.Variables[mapping.Value] = outputValue;
            }
        }
    }

    private static bool ConvertToBoolean(object? value)
    {
        return value switch
        {
            null => false,
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when int.TryParse(s, out var i) => i != 0,
            int i => i != 0,
            long i => i != 0,
            _ => true
        };
    }
}
