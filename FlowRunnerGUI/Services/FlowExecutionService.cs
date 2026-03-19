using System.IO;
using ActionSdk;
using FlowEngine;
using Microsoft.Extensions.Configuration;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

namespace FlowRunnerGUI.Services;

public sealed class FlowExecutionService
{
    private readonly string _pythonCommand;

    public FlowExecutionService()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _pythonCommand = configuration["Script:PythonCommand"] ?? "python";
    }

    public async Task<FlowRunResult> RunAsync(
        FlowDefinition definition,
        Action<StepExecutionResult>? onStepCompleted,
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        var registry = new ActionRegistry();
        registry.RegisterFromAssembly(typeof(ActionBuiltin.DelayAction).Assembly);
        registry.Register(new RunScriptAction(new PythonScriptExecutor(_pythonCommand, rootDirectory)));

        var context = new FlowExecutionContext
        {
            OnStepCompleted = onStepCompleted
        };

        var runner = new FlowRunner(registry);
        return await runner.RunAsync(definition, context, cancellationToken);
    }
}
