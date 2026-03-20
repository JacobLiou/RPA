using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActionSdk;

public sealed class ActionMetadata
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<ActionParameterDefinition> Inputs { get; init; } = [];
    public IReadOnlyList<ActionParameterDefinition> Outputs { get; init; } = [];
}

public sealed class ActionParameterDefinition
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Required { get; init; }
    public string? Description { get; init; }
}

public sealed class ActionRequest
{
    public required string StepId { get; init; }
    public required string StepName { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
    public Dictionary<string, object?> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public ILogger Logger { get; init; } = NullLogger.Instance;
}

public sealed class ActionResult
{
    public bool Success { get; init; } = true;
    public Dictionary<string, object?> Outputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static ActionResult Ok(Dictionary<string, object?>? outputs = null)
    {
        return new ActionResult
        {
            Success = true,
            Outputs = outputs ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static ActionResult Fail(string errorCode, string errorMessage)
    {
        return new ActionResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }
}

public interface IActionHandler
{
    string Name { get; }
    ActionMetadata Metadata { get; }
    Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken);
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RpaActionAttribute : Attribute
{
    public RpaActionAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed class ActionRegistry
{
    private readonly ConcurrentDictionary<string, IActionHandler> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IActionHandler> Handlers => _handlers.Values;

    public void Register(IActionHandler handler)
    {
        if (!_handlers.TryAdd(handler.Name, handler))
        {
            throw new InvalidOperationException($"Action '{handler.Name}' already exists.");
        }
    }

    public void RegisterFromAssembly(Assembly assembly, Func<Type, object?>? activator = null)
    {
        activator ??= Activator.CreateInstance;
        var actionTypes = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IActionHandler).IsAssignableFrom(t));

        foreach (var type in actionTypes)
        {
            var instance = activator(type) as IActionHandler;
            if (instance is null)
            {
                continue;
            }

            Register(instance);
        }
    }

    public IActionHandler Resolve(string name)
    {
        if (_handlers.TryGetValue(name, out var handler))
        {
            return handler;
        }

        throw new KeyNotFoundException($"Action '{name}' was not found.");
    }
}
