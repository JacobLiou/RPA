using System.IO;
using ActionSdk;
using FlowEngine;
using Microsoft.Extensions.Configuration;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

namespace FlowRunnerGUI.Services;

public sealed class FlowExecutionService
{
    private readonly ActionRegistry _registry;

    public FlowExecutionService()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _registry = new ActionRegistry();
        _registry.RegisterFromAssembly(typeof(ActionBuiltin.DelayAction).Assembly);

        var allowedRootDir = configuration["Script:AllowedRootDirectory"] ?? Directory.GetCurrentDirectory();
        var pythonCommand = configuration["Script:PythonCommand"] ?? "python";
        _registry.Register(new RunScriptAction(new PythonScriptExecutor(pythonCommand, allowedRootDir)));
    }

    public async Task<FlowRunResult> RunAsync(
        FlowDefinition definition,
        Action<StepExecutionResult>? onStepCompleted,
        CancellationToken cancellationToken = default)
    {
        var context = new FlowExecutionContext
        {
            OnStepCompleted = onStepCompleted
        };

        var runner = new FlowRunner(_registry);
        return await runner.RunAsync(definition, context, cancellationToken);
    }
}
