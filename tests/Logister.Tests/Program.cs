using System.Net;
using System.Text.Json;
using Logister;
using Logister.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("capture exception sends structured .NET context", CaptureExceptionSendsStructuredContext),
    ("capture metric sends metric value and unit", CaptureMetricSendsMetricContext),
    ("check-in uses check-in endpoint", CheckInUsesCheckInEndpoint),
    ("ASP.NET Core exception middleware reports and rethrows", AspNetCoreExceptionMiddlewareReportsAndRethrows),
    ("ASP.NET Core request cookies are captured only when enabled", AspNetCoreRequestCookiesAreCapturedOnlyWhenEnabled)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(exception);
    }
}

if (failures > 0)
{
    Environment.ExitCode = 1;
}

static async Task CaptureExceptionSendsStructuredContext()
{
    var handler = new RecordingHandler();
    using var client = BuildClient(handler);

    var exception = BuildThrownException();
    await client.CaptureExceptionAsync(
        exception,
        new CaptureOptions
        {
            Environment = "test",
            Release = "api@1.2.3",
            RequestId = "req_123",
            Context = new Dictionary<string, object?>
            {
                ["service"] = "checkout"
            }
        });

    var payload = handler.LastJson.RootElement.GetProperty("event");
    AssertEqual("error", payload.GetProperty("event_type").GetString());
    AssertEqual("checkout failed", payload.GetProperty("message").GetString());
    AssertEqual("test", payload.GetProperty("context").GetProperty("environment").GetString());
    AssertEqual("api@1.2.3", payload.GetProperty("context").GetProperty("release").GetString());
    AssertEqual(".NET", payload.GetProperty("context").GetProperty("runtime").GetString());

    var exceptionPayload = payload.GetProperty("context").GetProperty("exception");
    AssertEqual("InvalidOperationException", exceptionPayload.GetProperty("class").GetString());
    AssertEqual("checkout failed", exceptionPayload.GetProperty("message").GetString());
    AssertEqual("ArgumentException", exceptionPayload.GetProperty("inner_exception").GetProperty("class").GetString());
    AssertTrue(exceptionPayload.TryGetProperty("backtrace", out _), "Expected captured backtrace.");
}

static async Task CaptureMetricSendsMetricContext()
{
    var handler = new RecordingHandler();
    using var client = BuildClient(handler);

    await client.CaptureMetricAsync("timesheet.approvals.pending", 7, new MetricOptions { Unit = "count" });

    var payload = handler.LastJson.RootElement.GetProperty("event");
    AssertEqual("metric", payload.GetProperty("event_type").GetString());
    AssertEqual("timesheet.approvals.pending", payload.GetProperty("message").GetString());
    AssertEqual(7, payload.GetProperty("context").GetProperty("metric").GetProperty("value").GetDouble());
    AssertEqual("count", payload.GetProperty("context").GetProperty("metric").GetProperty("unit").GetString());
}

static async Task CheckInUsesCheckInEndpoint()
{
    var handler = new RecordingHandler();
    using var client = BuildClient(handler);

    await client.CheckInAsync("nightly-import", "ok", new CheckInOptions { DurationMs = 122.5 });

    AssertEqual("https://logister.test/api/v1/check_ins", handler.LastRequestUri?.ToString());
    var payload = handler.LastJson.RootElement.GetProperty("check_in");
    AssertEqual("nightly-import", payload.GetProperty("slug").GetString());
    AssertEqual("ok", payload.GetProperty("status").GetString());
    AssertEqual(122.5, payload.GetProperty("duration_ms").GetDouble());
}

static async Task AspNetCoreExceptionMiddlewareReportsAndRethrows()
{
    var handler = new RecordingHandler();
    var client = BuildClient(handler);
    var options = new LogisterAspNetCoreOptions();

    var middleware = new LogisterExceptionMiddleware(
        _ => throw new InvalidOperationException("pipeline failed"),
        client,
        options,
        NullLogger<LogisterExceptionMiddleware>.Instance);

    var context = new DefaultHttpContext();
    context.Request.Scheme = "https";
    context.Request.Host = new HostString("app.example.test");
    context.Request.Path = "/approvals";
    context.TraceIdentifier = "trace-aspnet";

    await AssertThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

    var payload = handler.LastJson.RootElement.GetProperty("event");
    AssertEqual("error", payload.GetProperty("event_type").GetString());
    AssertEqual("aspnetcore", payload.GetProperty("context").GetProperty("framework").GetString());
    var request = payload.GetProperty("context").GetProperty("request");
    AssertEqual("https://app.example.test/approvals", request.GetProperty("url").GetString());
    AssertEqual(500, request.GetProperty("status").GetInt32());
}

static async Task AspNetCoreRequestCookiesAreCapturedOnlyWhenEnabled()
{
    var defaultHandler = new RecordingHandler();
    var defaultClient = BuildClient(defaultHandler);
    var defaultMiddleware = new LogisterExceptionMiddleware(
        _ => throw new InvalidOperationException("pipeline failed"),
        defaultClient,
        new LogisterAspNetCoreOptions(),
        NullLogger<LogisterExceptionMiddleware>.Instance);
    var defaultContext = BuildCookieContext();

    await AssertThrowsAsync<InvalidOperationException>(() => defaultMiddleware.InvokeAsync(defaultContext));

    var defaultRequest = defaultHandler.LastJson.RootElement
        .GetProperty("event")
        .GetProperty("context")
        .GetProperty("request");
    AssertFalse(defaultRequest.TryGetProperty("cookies", out _), "Cookies should not be captured by default.");

    var enabledHandler = new RecordingHandler();
    var enabledClient = BuildClient(enabledHandler);
    var enabledOptions = new LogisterAspNetCoreOptions
    {
        CaptureRequestCookies = true
    };
    var enabledMiddleware = new LogisterExceptionMiddleware(
        _ => throw new InvalidOperationException("pipeline failed"),
        enabledClient,
        enabledOptions,
        NullLogger<LogisterExceptionMiddleware>.Instance);
    var enabledContext = BuildCookieContext();

    await AssertThrowsAsync<InvalidOperationException>(() => enabledMiddleware.InvokeAsync(enabledContext));

    var cookies = enabledHandler.LastJson.RootElement
        .GetProperty("event")
        .GetProperty("context")
        .GetProperty("request")
        .GetProperty("cookies");
    AssertEqual("dark", cookies.GetProperty("theme").GetString());
    AssertEqual("[Filtered]", cookies.GetProperty(".AspNetCore.Cookies").GetString());
}

static LogisterClient BuildClient(RecordingHandler handler)
{
    var options = new LogisterOptions
    {
        ApiKey = "test-key",
        BaseUrl = new Uri("https://logister.test")
    };

    return new LogisterClient(options, new HttpClient(handler));
}

static Exception BuildThrownException()
{
    try
    {
        throw new ArgumentException("missing order id");
    }
    catch (ArgumentException inner)
    {
        try
        {
            throw new InvalidOperationException("checkout failed", inner);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
    }
}

static DefaultHttpContext BuildCookieContext()
{
    var context = new DefaultHttpContext();
    context.Request.Scheme = "https";
    context.Request.Host = new HostString("app.example.test");
    context.Request.Path = "/approvals";
    context.Request.Headers.Cookie = "theme=dark; .AspNetCore.Cookies=secret-session";
    context.TraceIdentifier = "trace-cookies";

    return context;
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message)
{
    if (value)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }
    public JsonDocument LastJson { get; private set; } = JsonDocument.Parse("{}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        LastJson = JsonDocument.Parse(body);

        return new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"evt_123","legacy_id":123,"status":"accepted"}""")
        };
    }
}
