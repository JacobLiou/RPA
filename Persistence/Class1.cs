using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlowEngine;
using Json.Schema;

namespace Persistence;

public interface IFlowRepository
{
    Task SaveAsync(FlowDefinition definition, string path, CancellationToken cancellationToken = default);
    Task<FlowDefinition> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class FlowJsonRepository : IFlowRepository
{
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly JsonSchema _schema;

    public FlowJsonRepository(string schemaPath)
    {
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException("Schema file was not found.", schemaPath);
        }

        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        _serializerOptions.Converters.Add(new JsonStringEnumConverter());
        _schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
    }

    public async Task SaveAsync(FlowDefinition definition, string path, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(definition, _serializerOptions);
        Validate(json);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task<FlowDefinition> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        Validate(json);
        var flow = JsonSerializer.Deserialize<FlowDefinition>(json, _serializerOptions);
        return flow ?? throw new InvalidOperationException("Unable to deserialize flow definition.");
    }

    private void Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = _schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
        {
            var details = result.Details?.Select(d => d.InstanceLocation.ToString()) ?? [];
            throw new InvalidOperationException($"Schema validation failed: {string.Join("; ", details)}");
        }
    }
}

public sealed class RunRecord
{
    public required string RunId { get; init; }
    public required string FlowId { get; init; }
    public required string FlowName { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public bool Success { get; init; }
    public required List<StepExecutionResult> Steps { get; init; }
}

public interface IRunReportWriter
{
    Task<string> WriteJsonAsync(FlowRunResult result, string directory, CancellationToken cancellationToken = default);
    Task<string> WriteMarkdownAsync(FlowRunResult result, string directory, CancellationToken cancellationToken = default);
}

public sealed class RunReportWriter : IRunReportWriter
{
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<string> WriteJsonAsync(FlowRunResult result, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var record = new RunRecord
        {
            RunId = result.RunId,
            FlowId = result.FlowId,
            FlowName = result.FlowName,
            Success = result.Success,
            FinishedAt = DateTimeOffset.UtcNow,
            Steps = result.Steps
        };
        var path = Path.Combine(directory, $"{result.RunId}.json");
        var content = JsonSerializer.Serialize(record, _serializerOptions);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }

    public async Task<string> WriteMarkdownAsync(FlowRunResult result, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var builder = new StringBuilder();
        builder.AppendLine($"# Run Report - {result.FlowName}");
        builder.AppendLine();
        builder.AppendLine($"- RunId: `{result.RunId}`");
        builder.AppendLine($"- Success: `{result.Success}`");
        builder.AppendLine($"- Steps: `{result.Steps.Count}`");
        builder.AppendLine();
        builder.AppendLine("| StepId | Type | Status | DurationMs | Error |");
        builder.AppendLine("|---|---|---|---:|---|");

        foreach (var step in result.Steps)
        {
            var error = step.ErrorMessage?.Replace("|", "\\|") ?? string.Empty;
            builder.AppendLine($"| {step.StepId} | {step.StepType} | {step.Status} | {step.DurationMs} | {error} |");
        }

        var path = Path.Combine(directory, $"{result.RunId}.md");
        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return path;
    }
}
