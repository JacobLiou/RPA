using System.Text.Json.Nodes;
using ActionBuiltin;
using ActionSdk;
using FlowEngine;
using Persistence;
using ScriptHost;
using FlowExecutionContext = FlowEngine.ExecutionContext;

namespace Tests;

public sealed class UnitTests
{
    [Fact]
    public void ActionRegistry_CanResolveRegisteredAction()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        var action = registry.Resolve("SetVariable");
        Assert.Equal("SetVariable", action.Name);
    }

    [Fact]
    public void ActionRegistry_DuplicateAction_Throws()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        Assert.Throws<InvalidOperationException>(() => registry.Register(new SetVariableAction()));
    }

    [Fact]
    public void VariableResolver_ResolvesPlaceholder()
    {
        var resolved = VariableResolver.ResolveString("hello {{Name}}", new Dictionary<string, object?> { ["Name"] = "RPA" });
        Assert.Equal("hello RPA", resolved);
    }

    [Fact]
    public void VariableResolver_MissingVariable_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => VariableResolver.ResolveString("{{Missing}}", new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task FlowRunner_SequentialActions_Success()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);
        var flow = CreateSimpleFlow();
        var result = await runner.RunAsync(flow);
        Assert.True(result.Success);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task FlowRunner_MapsOutputToVariable()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);
        var context = new FlowExecutionContext();
        var flow = CreateSimpleFlow();
        await runner.RunAsync(flow, context);
        Assert.Equal("done", context.Variables["Result"]);
    }

    [Fact]
    public async Task FlowRunner_Retry_SecondAttemptSucceeds()
    {
        var registry = new ActionRegistry();
        registry.Register(new FlakyAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "retry",
            Steps =
            [
                new FlowStep { Id = "s1", Type = "CallMethod", Action = "FlakyAction", Retry = 1, OnError = OnErrorStrategy.Stop }
            ]
        };
        var result = await runner.RunAsync(flow);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task FlowRunner_OnErrorContinue_ContinuesAfterFailure()
    {
        var registry = new ActionRegistry();
        registry.Register(new AlwaysFailAction());
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);

        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "continue",
            Steps =
            [
                new FlowStep { Id = "s1", Type = "CallMethod", Action = "AlwaysFailAction", OnError = OnErrorStrategy.Continue },
                new FlowStep
                {
                    Id = "s2",
                    Type = "CallMethod",
                    Action = "SetVariable",
                    Inputs = new Dictionary<string, JsonNode?> { ["value"] = "next" },
                    Outputs = new Dictionary<string, string> { ["value"] = "AfterFailure" }
                }
            ]
        };
        var context = new FlowExecutionContext();
        var result = await runner.RunAsync(flow, context);
        Assert.False(result.Success);
        Assert.Equal("next", context.Variables["AfterFailure"]);
    }

    [Fact]
    public async Task FlowRunner_Timeout_RecordsTimeout()
    {
        var registry = new ActionRegistry();
        registry.Register(new LongRunningAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "timeout",
            Steps = [new FlowStep { Id = "s1", Type = "CallMethod", Action = "LongRunningAction", TimeoutMs = 10 }]
        };
        var result = await runner.RunAsync(flow);
        Assert.Equal(StepStatus.Timeout, result.Steps.Single().Status);
    }

    [Fact]
    public async Task FlowRunner_StartStepIndex_SkipsPreviousSteps()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "startAt",
            Steps =
            [
                new FlowStep { Id = "s1", Type = "CallMethod", Action = "SetVariable", Inputs = new Dictionary<string, JsonNode?> { ["value"] = "one" } },
                new FlowStep { Id = "s2", Type = "CallMethod", Action = "SetVariable", Inputs = new Dictionary<string, JsonNode?> { ["value"] = "two" } }
            ]
        };
        var context = new FlowExecutionContext { StartStepIndex = 1 };
        var result = await runner.RunAsync(flow, context);
        Assert.Single(result.Steps);
        Assert.Equal("s2", result.Steps[0].StepId);
    }

    [Fact]
    public async Task FlowRunner_If_ThenBranchRuns()
    {
        var registry = new ActionRegistry();
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "if",
            Variables = new Dictionary<string, JsonNode?> { ["Flag"] = "true" },
            Steps =
            [
                new FlowStep
                {
                    Id = "if1",
                    Type = "If",
                    Inputs = new Dictionary<string, JsonNode?> { ["condition"] = "{{Flag}}" },
                    ThenSteps =
                    [
                        new FlowStep
                        {
                            Id = "then",
                            Type = "CallMethod",
                            Action = "SetVariable",
                            Inputs = new Dictionary<string, JsonNode?> { ["value"] = "yes" },
                            Outputs = new Dictionary<string, string> { ["value"] = "Branch" }
                        }
                    ]
                }
            ]
        };
        var context = new FlowExecutionContext();
        await runner.RunAsync(flow, context);
        Assert.Equal("yes", context.Variables["Branch"]);
    }

    [Fact]
    public async Task FlowRunner_ForEach_RunsBodyForEachItem()
    {
        var registry = new ActionRegistry();
        registry.Register(new CounterAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "foreach",
            Steps =
            [
                new FlowStep
                {
                    Id = "loop",
                    Type = "ForEach",
                    Inputs = new Dictionary<string, JsonNode?> { ["items"] = JsonNode.Parse("[1,2,3]") },
                    BodySteps = [new FlowStep { Id = "count", Type = "CallMethod", Action = "CounterAction" }]
                }
            ]
        };
        await runner.RunAsync(flow);
        Assert.Equal(3, CounterAction.Count);
        CounterAction.Count = 0;
    }

    [Fact]
    public async Task FlowRunner_TryCatch_CatchRunsOnFailure()
    {
        var registry = new ActionRegistry();
        registry.Register(new AlwaysFailAction());
        registry.Register(new SetVariableAction());
        var runner = new FlowRunner(registry);
        var flow = new FlowDefinition
        {
            FlowId = "f",
            Name = "trycatch",
            Steps =
            [
                new FlowStep
                {
                    Id = "tc",
                    Type = "TryCatch",
                    TrySteps = [new FlowStep { Id = "try1", Type = "CallMethod", Action = "AlwaysFailAction" }],
                    CatchSteps =
                    [
                        new FlowStep
                        {
                            Id = "catch1",
                            Type = "CallMethod",
                            Action = "SetVariable",
                            Inputs = new Dictionary<string, JsonNode?> { ["value"] = "handled" },
                            Outputs = new Dictionary<string, string> { ["value"] = "Handled" }
                        }
                    ]
                }
            ]
        };
        var context = new FlowExecutionContext();
        await runner.RunAsync(flow, context);
        Assert.Equal("handled", context.Variables["Handled"]);
    }

    [Fact]
    public async Task Repository_SaveAndLoad_Works()
    {
        var tempDir = CreateTempDir();
        var schemaPath = CopySchemaTo(tempDir);
        var repo = new FlowJsonRepository(schemaPath);
        var path = Path.Combine(tempDir, "flow.json");
        await repo.SaveAsync(CreateSimpleFlow(), path);
        var loaded = await repo.LoadAsync(path);
        Assert.Equal("flow_1", loaded.FlowId);
    }

    [Fact]
    public async Task Repository_LoadInvalid_Throws()
    {
        var tempDir = CreateTempDir();
        var schemaPath = CopySchemaTo(tempDir);
        var repo = new FlowJsonRepository(schemaPath);
        var path = Path.Combine(tempDir, "bad.json");
        await File.WriteAllTextAsync(path, "{}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.LoadAsync(path));
    }

    [Fact]
    public async Task ReportWriter_CreatesJsonAndMarkdown()
    {
        var writer = new RunReportWriter();
        var dir = CreateTempDir();
        var result = new FlowRunResult { RunId = "r1", FlowId = "f", FlowName = "n", Success = true, Steps = [] };
        var json = await writer.WriteJsonAsync(result, dir);
        var md = await writer.WriteMarkdownAsync(result, dir);
        Assert.True(File.Exists(json));
        Assert.True(File.Exists(md));
    }

    [Fact]
    public async Task DelayAction_WaitsSuccessfully()
    {
        var action = new DelayAction();
        var result = await action.ExecuteAsync(
            new ActionRequest { StepId = "s", StepName = "s", Inputs = new Dictionary<string, object?> { ["milliseconds"] = 1 } },
            CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task SetVariableAction_ReturnsValue()
    {
        var action = new SetVariableAction();
        var result = await action.ExecuteAsync(
            new ActionRequest { StepId = "s", StepName = "s", Inputs = new Dictionary<string, object?> { ["value"] = "x" } },
            CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("x", result.Outputs["value"]);
    }

    [Fact]
    public async Task AssertEqualsAction_WhenEqual_Success()
    {
        var action = new AssertEqualsAction();
        var result = await action.ExecuteAsync(
            new ActionRequest
            {
                StepId = "s",
                StepName = "s",
                Inputs = new Dictionary<string, object?> { ["expected"] = "a", ["actual"] = "a" }
            },
            CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task AssertEqualsAction_WhenNotEqual_Fails()
    {
        var action = new AssertEqualsAction();
        var result = await action.ExecuteAsync(
            new ActionRequest
            {
                StepId = "s",
                StepName = "s",
                Inputs = new Dictionary<string, object?> { ["expected"] = "a", ["actual"] = "b" }
            },
            CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ReadFileAction_ReadsContent()
    {
        var temp = Path.GetTempFileName();
        await File.WriteAllTextAsync(temp, "hello");
        var action = new ReadFileAction();
        var result = await action.ExecuteAsync(
            new ActionRequest { StepId = "s", StepName = "s", Inputs = new Dictionary<string, object?> { ["path"] = temp } },
            CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("hello", result.Outputs["content"]);
    }

    [Fact]
    public async Task RunScriptAction_MissingScriptPath_Fails()
    {
        var action = new RunScriptAction(new PythonScriptExecutor("python", Directory.GetCurrentDirectory()));
        var result = await action.ExecuteAsync(
            new ActionRequest { StepId = "s", StepName = "s", Inputs = new Dictionary<string, object?>() },
            CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public void Metadata_IsExposed()
    {
        var action = new DelayAction();
        Assert.Equal("Delay", action.Metadata.Name);
    }

    [Fact]
    public async Task E2E_SampleFlows_RunSuccessfully()
    {
        var schema = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Persistence", "flow.schema.json"));
        var repo = new FlowJsonRepository(schema);
        var registry = new ActionRegistry();
        registry.RegisterFromAssembly(typeof(DelayAction).Assembly);
        var runner = new FlowRunner(registry);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var flows = new[]
        {
            "Samples/flow-01-basic.json",
            "Samples/flow-02-if.json",
            "Samples/flow-03-foreach.json",
            "Samples/flow-04-trycatch.json"
        };

        foreach (var relativePath in flows)
        {
            var flow = await repo.LoadAsync(Path.Combine(root, relativePath));
            var result = await runner.RunAsync(flow);
            Assert.NotEmpty(result.Steps);
        }
    }

    private static FlowDefinition CreateSimpleFlow()
    {
        return new FlowDefinition
        {
            FlowId = "flow_1",
            Name = "simple",
            Steps =
            [
                new FlowStep
                {
                    Id = "step_1",
                    Type = "CallMethod",
                    Action = "SetVariable",
                    Inputs = new Dictionary<string, JsonNode?> { ["value"] = "done" },
                    Outputs = new Dictionary<string, string> { ["value"] = "Result" }
                }
            ]
        };
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "rpa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CopySchemaTo(string targetDir)
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Persistence", "flow.schema.json"));
        var destination = Path.Combine(targetDir, "flow.schema.json");
        File.Copy(source, destination, true);
        return destination;
    }

    private sealed class FlakyAction : IActionHandler
    {
        private int _attempt;
        public string Name => "FlakyAction";
        public ActionMetadata Metadata => new() { Name = Name, DisplayName = Name, Description = Name };

        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            _attempt++;
            return Task.FromResult(_attempt < 2
                ? ActionResult.Fail("X", "fail first")
                : ActionResult.Ok());
        }
    }

    private sealed class AlwaysFailAction : IActionHandler
    {
        public string Name => "AlwaysFailAction";
        public ActionMetadata Metadata => new() { Name = Name, DisplayName = Name, Description = Name };

        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(ActionResult.Fail("FAIL", "always"));
    }

    private sealed class LongRunningAction : IActionHandler
    {
        public string Name => "LongRunningAction";
        public ActionMetadata Metadata => new() { Name = Name, DisplayName = Name, Description = Name };

        public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(1000, cancellationToken);
            return ActionResult.Ok();
        }
    }

    private sealed class CounterAction : IActionHandler
    {
        public static int Count;
        public string Name => "CounterAction";
        public ActionMetadata Metadata => new() { Name = Name, DisplayName = Name, Description = Name };

        public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Task.FromResult(ActionResult.Ok());
        }
    }
}
