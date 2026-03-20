using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ActionSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScriptHost;

public sealed class PythonScriptExecutor
{
    private readonly string _pythonCommand;
    private readonly string _allowedRootDirectory;
    private readonly ILogger<PythonScriptExecutor> _logger;

    public PythonScriptExecutor(string pythonCommand = "python", string? allowedRootDirectory = null,
        ILogger<PythonScriptExecutor>? logger = null)
    {
        _pythonCommand = pythonCommand;
        _allowedRootDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(allowedRootDirectory) ? Directory.GetCurrentDirectory() : allowedRootDirectory);
        _logger = logger ?? NullLoggerFactory.Instance.CreateLogger<PythonScriptExecutor>();
    }

    public async Task<Dictionary<string, object?>> ExecuteAsync(
        string scriptPath,
        Dictionary<string, object?> variables,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var fullScriptPath = ResolveScriptPath(scriptPath);
        if (!fullScriptPath.StartsWith(_allowedRootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Script path is outside the allowed root directory.");
        }

        _logger.LogDebug("Script resolved: {ScriptPath} -> {FullScriptPath}", scriptPath, fullScriptPath);

        var start = new ProcessStartInfo
        {
            FileName = _pythonCommand,
            Arguments = $"\"{fullScriptPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(fullScriptPath) ?? _allowedRootDirectory
        };
        var inputJson = JsonSerializer.Serialize(variables);
        start.Environment["RPA_INPUT_JSON"] = inputJson;

        _logger.LogDebug("Starting python process: {PythonCommand} \"{FullScriptPath}\"", _pythonCommand, fullScriptPath);

        using var process = new Process { StartInfo = start };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)));

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            _logger.LogError("Python script failed with exit code {ExitCode}: {StdErr}", process.ExitCode, error);
            throw new InvalidOperationException($"Python script failed. {error}");
        }

        _logger.LogDebug("Python script finished, ExitCode=0");
        return ParseOutput(output);
    }

    private string ResolveScriptPath(string scriptPath)
    {
        if (Path.IsPathRooted(scriptPath))
        {
            return Path.GetFullPath(scriptPath);
        }

        var normalized = scriptPath.Replace('\\', '/');
        var rootName = Path.GetFileName(_allowedRootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Avoid double-prefix paths such as "<allowedRoot>/Samples/..." when allowedRoot already ends with "Samples".
        if (segments.Length > 1 && segments[0].Equals(rootName, StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Join('/', segments.Skip(1));
        }

        return Path.GetFullPath(Path.Combine(_allowedRootDirectory, normalized));
    }

    private static Dictionary<string, object?> ParseOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new Dictionary<string, object?> { ["stdout"] = string.Empty };
        }

        var trimmed = output.Trim();
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(trimmed);
            if (parsed is not null)
            {
                return parsed;
            }
        }
        catch
        {
            // Fallback to plain output.
        }

        return new Dictionary<string, object?> { ["stdout"] = trimmed };
    }
}

[RpaAction("RunScript")]
public sealed class RunScriptAction : IActionHandler
{
    private static readonly string[] SensitiveKeys = ["password", "token", "secret", "apikey"];
    private readonly PythonScriptExecutor _executor;

    public RunScriptAction()
        : this(new PythonScriptExecutor())
    {
    }

    public RunScriptAction(PythonScriptExecutor executor)
    {
        _executor = executor;
    }

    public string Name => "RunScript";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Run Python Script",
        Description = "Execute Python script with injected variables.",
        Inputs =
        [
            new ActionParameterDefinition { Name = "scriptPath", Type = "string", Required = true },
            new ActionParameterDefinition { Name = "variables", Type = "object", Required = false }
        ],
        Outputs = [new ActionParameterDefinition { Name = "stdout", Type = "string", Required = false }]
    };

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        if (!request.Inputs.TryGetValue("scriptPath", out var scriptPathValue) || scriptPathValue is null)
        {
            return ActionResult.Fail("ARG_MISSING", "scriptPath is required.");
        }

        var scriptPath = scriptPathValue.ToString()!;
        request.Logger.LogInformation("RunScript executing: {ScriptPath}", scriptPath);

        var mergedVariables = new Dictionary<string, object?>(request.Variables, StringComparer.OrdinalIgnoreCase);
        if (request.Inputs.TryGetValue("variables", out var varsValue) && varsValue is Dictionary<string, object?> vars)
        {
            foreach (var kv in vars)
            {
                mergedVariables[kv.Key] = kv.Value;
            }
        }

        try
        {
            var result = await _executor.ExecuteAsync(scriptPath, mergedVariables, request.TimeoutMs, cancellationToken);
            var sanitized = Sanitize(result);
            request.Logger.LogDebug("RunScript completed: {ScriptPath}, OutputKeys={OutputKeys}",
                scriptPath, string.Join(",", sanitized.Keys));
            return ActionResult.Ok(sanitized);
        }
        catch (Exception ex)
        {
            request.Logger.LogError(ex, "RunScript failed: {ScriptPath}", scriptPath);
            return ActionResult.Fail("SCRIPT_FAILED", ex.Message);
        }
    }

    private static Dictionary<string, object?> Sanitize(Dictionary<string, object?> output)
    {
        var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in output)
        {
            if (SensitiveKeys.Any(k => kv.Key.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                clone[kv.Key] = "***";
                continue;
            }

            clone[kv.Key] = kv.Value;
        }

        return clone;
    }
}
