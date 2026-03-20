using System.IO;
using ActionSdk;
using FlowEngine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

namespace FlowRunnerGUI.Services;

public sealed class FlowExecutionService
{
    private readonly string _pythonCommand;
    private readonly ILoggerFactory _loggerFactory;

    public FlowExecutionContext? CurrentContext { get; private set; }

    public FlowExecutionService(ILoggerFactory? loggerFactory = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _pythonCommand = configuration["Script:PythonCommand"] ?? "python";
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<FlowRunResult> RunAsync(
        FlowDefinition definition,
        Action<StepExecutionResult>? onStepCompleted,
        string rootDirectory,
        string? startStepId = null,
        bool stepMode = false,
        Func<string, bool>? checkBreakpoint = null,
        Action<string>? onBeforeStep = null,
        Action<string>? onBreakpointHit = null,
        CancellationToken cancellationToken = default)
    {
        var registry = new ActionRegistry();
        registry.RegisterFromAssembly(typeof(ActionBuiltin.DelayAction).Assembly);
        registry.Register(new RunScriptAction(new PythonScriptExecutor(
            _pythonCommand, rootDirectory, _loggerFactory.CreateLogger<PythonScriptExecutor>())));

        var context = new FlowExecutionContext
        {
            StartStepId = startStepId,
            OnStepCompleted = onStepCompleted
        };
        context.StepMode = stepMode;
        context.CheckBreakpoint = checkBreakpoint;
        context.OnBeforeStep = onBeforeStep;
        context.OnBreakpointHit = onBreakpointHit;
        CurrentContext = context;

        try
        {
            var runner = new FlowRunner(registry, _loggerFactory.CreateLogger<FlowRunner>());
            return await runner.RunAsync(definition, context, cancellationToken);
        }
        finally
        {
            CurrentContext = null;
        }
    }
}
