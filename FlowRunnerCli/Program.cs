using System.Reflection;
using System.Text.Json;
using ActionSdk;
using FlowEngine;
using Microsoft.Extensions.Configuration;
using Persistence;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

if (args.Length == 0)
{
    Console.WriteLine("Usage: run <flowPath> [--start-index N] [--report-dir dir]");
    return 1;
}

var command = args[0].ToLowerInvariant();
if (command != "run")
{
    Console.WriteLine("Only `run` command is supported.");
    return 1;
}

if (args.Length < 2)
{
    Console.WriteLine("Please provide flow path.");
    return 1;
}

var flowPath = args[1];
var startIndex = ParseStartIndex(args);
var reportDir = ParseReportDir(args) ?? Path.Combine(Directory.GetCurrentDirectory(), "run-reports");

var schemaPath = Path.Combine(AppContext.BaseDirectory, "flow.schema.json");
var repository = new FlowJsonRepository(schemaPath);
var flow = await repository.LoadAsync(flowPath);

var registry = new ActionRegistry();
registry.RegisterFromAssembly(typeof(ActionBuiltin.DelayAction).Assembly);
var allowedRootDir = configuration["Script:AllowedRootDirectory"] ?? Directory.GetCurrentDirectory();
var pythonCommand = configuration["Script:PythonCommand"] ?? "python";
registry.Register(new RunScriptAction(new PythonScriptExecutor(pythonCommand, allowedRootDir)));

var runner = new FlowRunner(registry);
var context = new FlowExecutionContext { StartStepIndex = startIndex };
var runResult = await runner.RunAsync(flow, context);

var reportWriter = new RunReportWriter();
var jsonPath = await reportWriter.WriteJsonAsync(runResult, reportDir);
var mdPath = await reportWriter.WriteMarkdownAsync(runResult, reportDir);

Console.WriteLine($"RunId: {runResult.RunId}");
Console.WriteLine($"Success: {runResult.Success}");
Console.WriteLine($"JSON report: {jsonPath}");
Console.WriteLine($"Markdown report: {mdPath}");

return runResult.Success ? 0 : 2;

static int? ParseStartIndex(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--start-index" && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }
    }

    return null;
}

static string? ParseReportDir(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--report-dir")
        {
            return args[i + 1];
        }
    }

    return null;
}
