using System.Net.Http.Json;
using System.Text;
using ActionSdk;

namespace ActionBuiltin;

[RpaAction("Delay")]
public sealed class DelayAction : IActionHandler
{
    public string Name => "Delay";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Delay",
        Description = "Wait for a specific duration.",
        Inputs = [new ActionParameterDefinition { Name = "milliseconds", Type = "int", Required = true }],
        Outputs = []
    };

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        if (!request.Inputs.TryGetValue("milliseconds", out var value))
        {
            return ActionResult.Fail("ARG_MISSING", "milliseconds is required.");
        }

        var ms = Convert.ToInt32(value);
        await Task.Delay(ms, cancellationToken);
        return ActionResult.Ok();
    }
}

[RpaAction("SetVariable")]
public sealed class SetVariableAction : IActionHandler
{
    public string Name => "SetVariable";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Set Variable",
        Description = "Set value to output.",
        Inputs =
        [
            new ActionParameterDefinition { Name = "name", Type = "string", Required = true },
            new ActionParameterDefinition { Name = "value", Type = "object", Required = false }
        ],
        Outputs = [new ActionParameterDefinition { Name = "value", Type = "object", Required = false }]
    };

    public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        request.Inputs.TryGetValue("value", out var value);
        return Task.FromResult(ActionResult.Ok(new Dictionary<string, object?> { ["value"] = value }));
    }
}

[RpaAction("ReadFile")]
public sealed class ReadFileAction : IActionHandler
{
    public string Name => "ReadFile";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Read File",
        Description = "Read text content from file.",
        Inputs = [new ActionParameterDefinition { Name = "path", Type = "string", Required = true }],
        Outputs = [new ActionParameterDefinition { Name = "content", Type = "string", Required = true }]
    };

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        if (!request.Inputs.TryGetValue("path", out var pathValue) || pathValue is null)
        {
            return ActionResult.Fail("ARG_MISSING", "path is required.");
        }

        var path = pathValue.ToString()!;
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return ActionResult.Ok(new Dictionary<string, object?> { ["content"] = content });
    }
}

[RpaAction("AssertEquals")]
public sealed class AssertEqualsAction : IActionHandler
{
    public string Name => "AssertEquals";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Assert Equals",
        Description = "Assert expected and actual values are equal.",
        Inputs =
        [
            new ActionParameterDefinition { Name = "expected", Type = "object", Required = true },
            new ActionParameterDefinition { Name = "actual", Type = "object", Required = true }
        ],
        Outputs = []
    };

    public Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        request.Inputs.TryGetValue("expected", out var expected);
        request.Inputs.TryGetValue("actual", out var actual);
        if (!Equals(expected?.ToString(), actual?.ToString()))
        {
            return Task.FromResult(ActionResult.Fail("ASSERT_NOT_EQUAL", $"Expected '{expected}', Actual '{actual}'."));
        }

        return Task.FromResult(ActionResult.Ok());
    }
}

[RpaAction("HttpRequest")]
public sealed class HttpRequestAction : IActionHandler
{
    private static readonly HttpClient HttpClient = new();

    public string Name => "HttpRequest";
    public ActionMetadata Metadata => new()
    {
        Name = Name,
        DisplayName = "Http Request",
        Description = "Send HTTP request and read body.",
        Inputs =
        [
            new ActionParameterDefinition { Name = "url", Type = "string", Required = true },
            new ActionParameterDefinition { Name = "method", Type = "string", Required = false },
            new ActionParameterDefinition { Name = "headers", Type = "object", Required = false },
            new ActionParameterDefinition { Name = "body", Type = "object", Required = false }
        ],
        Outputs =
        [
            new ActionParameterDefinition { Name = "statusCode", Type = "int", Required = true },
            new ActionParameterDefinition { Name = "body", Type = "string", Required = true }
        ]
    };

    public async Task<ActionResult> ExecuteAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        if (!request.Inputs.TryGetValue("url", out var urlValue) || urlValue is null)
        {
            return ActionResult.Fail("ARG_MISSING", "url is required.");
        }

        var url = urlValue.ToString()!;
        var method = request.Inputs.TryGetValue("method", out var methodValue)
            ? methodValue?.ToString() ?? "GET"
            : "GET";

        using var message = new HttpRequestMessage(new HttpMethod(method), url);
        if (request.Inputs.TryGetValue("headers", out var headersValue) &&
            headersValue is IDictionary<string, object?> headers)
        {
            foreach (var header in headers)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value?.ToString());
            }
        }

        if (request.Inputs.TryGetValue("body", out var bodyValue) && bodyValue is not null)
        {
            if (bodyValue is string str)
            {
                message.Content = new StringContent(str, Encoding.UTF8, "application/json");
            }
            else
            {
                message.Content = JsonContent.Create(bodyValue);
            }
        }

        var response = await HttpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ActionResult.Ok(new Dictionary<string, object?>
        {
            ["statusCode"] = (int)response.StatusCode,
            ["body"] = responseText
        });
    }
}
