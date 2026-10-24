using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using Logister;
using Logister.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SecurityTests
{
    private const string Redacted = "[REDACTED]";

    internal static Task RedactionFixtures()
    {
        var sanitizer = LogisterValueSanitizer.Default;
        foreach (var key in new[]
                 {
                     "password", "Password", "passwd", "pwd", "secret", "token", "api_key", "apiKey", "x-api-key", "X-Api-Key",
                     "authorization", "Proxy-Authorization", "cookie", "Set-Cookie", "session_token", "sessionKey", "credential",
                     "credentials", "private_key", "client_secret", "accessToken", "refresh_token", "id_token", "auth_token",
                     "x-auth-token", "x-access-token", "X-CSRF-Token", "x-xsrf-token", "signature", "ssn", "credit_card",
                     "card-number", "cvv", "github_token", "dbPassword", "my_secret_value", "credentialsFile", "authCookie"
                 })
        {
            Require(sanitizer.IsSensitiveKey(key), $"Expected {key} to be sensitive.");
        }

        foreach (var key in new[] { "user", "email", "session_id", "trace_id", "route", "status", "code", "signature_count", "" })
        {
            Require(!sanitizer.IsSensitiveKey(key), $"Expected {key} to be allowed.");
        }

        var custom = new LogisterValueSanitizer(new[] { "tenant-key" });
        Require(custom.IsSensitiveKey("TenantKey") && custom.IsSensitiveKey("tenant_key") && !sanitizer.IsSensitiveKey("tenant_key"), "Custom keys must normalize.");

        Equal("Authorization failed for Bearer [REDACTED] today", sanitizer.ScrubString("Authorization failed for Bearer abc.def-123/x== today"));
        Equal("basic [REDACTED]", sanitizer.ScrubString("basic dXNlcjpwYXNz"));
        Equal("jwt [REDACTED].", sanitizer.ScrubString("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnX2FiYw."));
        Equal("key [REDACTED] used", sanitizer.ScrubString("key lgst_live_AbC123 used"));
        Equal("GET https://api.test/cb?code=1&access_token=[REDACTED]&state=ok failed",
            sanitizer.ScrubString("GET https://api.test/cb?code=1&access_token=abc&state=ok failed"));
        Equal("postgres://app:[REDACTED]@db.test/main", sanitizer.ScrubString("postgres://app:hunter2@db.test/main"));
        Equal("https://u:[REDACTED]@h.test/a?x=1&api_key=[REDACTED]", sanitizer.RedactUrl("https://u:p@h.test/a?x=1&api_key=k#frag"));
        Equal("/p?refresh%5Ftoken=[REDACTED]&page=2", sanitizer.RedactUrl("/p?refresh%5Ftoken=r&page=2"));
        Equal("?password=[REDACTED]&page=2&flag", sanitizer.RedactQueryString("?password=hunter2&page=2&flag"));
        Equal("password=[REDACTED]", sanitizer.RedactQueryString("password=hunter2"));

        const string sample = "Bearer abc https://x.test/?token=1 lgst_x eyJa.eyJb.c";
        Equal(sanitizer.ScrubString(sample), sanitizer.ScrubString(sanitizer.ScrubString(sample)));

        var tree = sanitizer.Sanitize(new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?> { ["Client-Secret"] = "s", ["list"] = new List<object?> { "Bearer t", 3 } },
            ["referer"] = "https://app.test/?sessionToken=abc#x"
        }) as IDictionary<string, object?>;
        var nested = (IDictionary<string, object?>)tree!["nested"]!;
        Equal(Redacted, nested["Client-Secret"]);
        Equal("Bearer [REDACTED]", ((IList)nested["list"]!)[0]);
        Equal("https://app.test/?sessionToken=[REDACTED]", tree["referer"]);
        return Task.CompletedTask;
    }

    internal static async Task ClientRedactsCapturedValues()
    {
        var handler = new RecordingHandler();
        var options = Options();
        options.RedactKeys.Add("tenant-key");
        using var client = new LogisterClient(options, new HttpClient(handler));
        const string jwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.c2ln";

        var exception = new InvalidOperationException("calling https://x.test/?token=abc with Bearer zzz failed");
        exception.Data["password"] = "hunter2";
        exception.Data["note"] = "lgst_abc";
        exception.Data["safe"] = "ok";
        await client.CaptureExceptionAsync(exception, new CaptureOptions
        {
            SessionId = jwt,
            Context = new Dictionary<string, object?>
            {
                ["user"] = new Dictionary<string, object?> { ["email"] = "a@b.test", ["apiKey"] = "k" },
                ["tenantKey"] = "t",
                ["headers"] = new Dictionary<string, object?> { ["Authorization"] = "Bearer x" },
                ["url"] = "https://a.test/p?refresh_token=r#f",
                ["tags"] = new[] { "plain", "Basic dXNlcjpwYXNz" }
            }
        });

        var payload = handler.LastJson.RootElement.GetProperty("event");
        Equal("calling https://x.test/?token=[REDACTED] with Bearer [REDACTED] failed", payload.GetProperty("message").GetString());
        var context = payload.GetProperty("context");
        var captured = context.GetProperty("exception");
        Equal(payload.GetProperty("message").GetString(), captured.GetProperty("message").GetString());
        Equal(Redacted, captured.GetProperty("data").GetProperty("password").GetString());
        Equal(Redacted, captured.GetProperty("data").GetProperty("note").GetString());
        Equal("ok", captured.GetProperty("data").GetProperty("safe").GetString());
        Equal(Redacted, context.GetProperty("user").GetProperty("apiKey").GetString());
        Equal("a@b.test", context.GetProperty("user").GetProperty("email").GetString());
        Equal(Redacted, context.GetProperty("tenantKey").GetString());
        Equal(Redacted, context.GetProperty("headers").GetProperty("Authorization").GetString());
        Equal("https://a.test/p?refresh_token=[REDACTED]", context.GetProperty("url").GetString());
        Equal("Basic [REDACTED]", context.GetProperty("tags")[1].GetString());
        Equal(jwt, context.GetProperty("session_id").GetString());
        Require(payload.TryGetProperty("uuid", out _), "Envelope uuid missing.");

        await client.CaptureMessageAsync("password reset via https://a.test/r?token=abc");
        Equal("password reset via https://a.test/r?token=[REDACTED]", handler.LastJson.RootElement.GetProperty("event").GetProperty("message").GetString());
    }

    internal static async Task CyclicContextIsBounded()
    {
        var handler = new RecordingHandler();
        using var client = new LogisterClient(Options(), new HttpClient(handler));
        var cyclic = new Dictionary<string, object?> { ["name"] = "root" };
        cyclic["self"] = cyclic;
        var list = new List<object?>();
        list.Add(list);
        var exception = new InvalidOperationException("cycle");
        exception.Data["self"] = exception;

        await client.CaptureExceptionAsync(exception, new CaptureOptions
        {
            Context = new Dictionary<string, object?> { ["cycle"] = cyclic, ["list"] = list }
        });

        var context = handler.LastJson.RootElement.GetProperty("event").GetProperty("context");
        Equal("root", context.GetProperty("cycle").GetProperty("name").GetString());
        Equal("[Circular]", context.GetProperty("cycle").GetProperty("self").GetString());
        Equal("[Circular]", context.GetProperty("list")[0].GetString());
        Equal("[Circular]", context.GetProperty("exception").GetProperty("data").GetProperty("self").GetString());

        var prepared = client.PrepareEvent("log", "info", "nested exception", new Dictionary<string, object?> { ["error"] = exception });
        var nested = JsonDocument.Parse(prepared.Envelope).RootElement.GetProperty("event").GetProperty("context").GetProperty("error");
        Equal("[Circular]", nested.GetProperty("data").GetProperty("self").GetString());
    }

    internal static Task LazySequencesAreNotEnumerated()
    {
        using var client = new LogisterClient(Options(), new HttpClient(new RecordingHandler()));
        var queryable = new TrackingQueryable();
        var lazyEnumerated = false;
        IEnumerable<int> Lazy()
        {
            lazyEnumerated = true;
            yield return 1;
        }

        var prepared = client.PrepareEvent("log", "info", "lazy", new Dictionary<string, object?>
        {
            ["query"] = queryable,
            ["lazy"] = Lazy(),
            ["set"] = new HashSet<string> { "a" },
            ["array"] = new[] { 1, 2 },
            ["nan"] = double.NaN
        });

        Require(!queryable.Enumerated, "IQueryable was executed.");
        Require(!lazyEnumerated, "Lazy IEnumerable was enumerated.");
        var context = JsonDocument.Parse(prepared.Envelope).RootElement.GetProperty("event").GetProperty("context");
        Equal($"[{nameof(TrackingQueryable)}]", context.GetProperty("query").GetString());
        Equal(JsonValueKind.String, context.GetProperty("lazy").ValueKind);
        Equal("a", context.GetProperty("set")[0].GetString());
        Equal(2, context.GetProperty("array").GetArrayLength());
        Equal("NaN", context.GetProperty("nan").GetString());
        return Task.CompletedTask;
    }

    internal static Task NormalizerBoundsDepthAndSize()
    {
        using var client = new LogisterClient(Options(), new HttpClient(new RecordingHandler()));
        object? deep = "leaf";
        for (var i = 0; i < 40; i++) deep = new Dictionary<string, object?> { ["child"] = deep };

        var prepared = client.PrepareEvent("log", "info", "bounded", new Dictionary<string, object?>
        {
            ["deep"] = deep,
            ["large"] = Enumerable.Range(0, 1000).ToList()
        });

        var context = JsonDocument.Parse(prepared.Envelope).RootElement.GetProperty("event").GetProperty("context");
        var node = context.GetProperty("deep");
        var levels = 0;
        while (node.ValueKind == JsonValueKind.Object)
        {
            node = node.GetProperty("child");
            levels++;
        }

        Equal("[MaxDepth]", node.GetString());
        Require(levels <= 12, $"Depth was {levels}.");
        var large = context.GetProperty("large");
        Equal(201, large.GetArrayLength());
        Equal("[Truncated]", large[200].GetString());
        return Task.CompletedTask;
    }

    internal static Task HttpContextRedactsRequestData()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.test");
        http.Request.Path = "/reset/s3cr3t-value";
        http.Request.QueryString = new QueryString("?token=abc&page=2&sig=x");
        http.Request.RouteValues["token"] = "s3cr3t-value";
        http.Request.RouteValues["id"] = "42";
        http.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        var options = new LogisterAspNetCoreOptions { CaptureRequestHeaders = true };

        var request = (IDictionary<string, object?>)LogisterHttpContext.BuildContext(http, options)["request"]!;
        Equal("?token=[REDACTED]&page=2&sig=x", request["query_string"]);
        Equal("https://app.test/reset/[REDACTED]?token=[REDACTED]&page=2&sig=x", request["url"]);
        Equal("/reset/[REDACTED]", request["path"]);
        var route = (IDictionary<string, object?>)request["route"]!;
        Equal(Redacted, route["token"]);
        Equal("42", route["id"]);
        Require(!request.ContainsKey("client_ip"), "client_ip must be opt-in.");
        Require(!((IDictionary<string, object?>)request["headers"]!).ContainsKey("X-Forwarded-For"), "Forwarding headers must be opt-in.");
        Equal("GET /reset/[REDACTED]", LogisterHttpContext.TransactionName(http, LogisterValueSanitizer.Default));

        options.CaptureClientIp = true;
        request = (IDictionary<string, object?>)LogisterHttpContext.BuildContext(http, options)["request"]!;
        Equal("203.0.113.9", request["client_ip"]);
        Require(((IDictionary<string, object?>)request["headers"]!).ContainsKey("X-Forwarded-For"), "Opt-in should keep forwarding headers.");
        return Task.CompletedTask;
    }

    internal static Task SensitiveHeadersAreRedacted()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.test");
        var headers = http.Request.Headers;
        foreach (var name in new[]
                 {
                     "Authorization", "Proxy-Authorization", "X-Api-Key", "X-Functions-Key", "X-MS-CLIENT-PRINCIPAL",
                     "X-MS-CLIENT-PRINCIPAL-ID", "X-MS-TOKEN-AAD-ID-TOKEN", "X-CSRF-TOKEN", "X-Custom-Secret", "X-Tenant", "Cookie"
                 })
        {
            headers[name] = "value";
        }

        headers.Accept = "text/html";
        headers.Referer = "https://app.test/login?password=x&next=/home";
        headers["X-Debug"] = "Bearer abc";
        var options = new LogisterAspNetCoreOptions { CaptureRequestHeaders = true };
        options.SensitiveRequestHeaderNames.Add("X-Tenant");

        var request = (IDictionary<string, object?>)LogisterHttpContext.BuildContext(http, options)["request"]!;
        var captured = (IDictionary<string, object?>)request["headers"]!;
        foreach (var name in new[]
                 {
                     "Authorization", "Proxy-Authorization", "X-Api-Key", "X-Functions-Key", "X-MS-CLIENT-PRINCIPAL",
                     "X-MS-CLIENT-PRINCIPAL-ID", "X-MS-TOKEN-AAD-ID-TOKEN", "X-CSRF-TOKEN", "X-Custom-Secret", "X-Tenant", "Cookie"
                 })
        {
            Equal(Redacted, captured[name]);
        }

        Equal("text/html", captured["Accept"]);
        Equal("https://app.test/login?password=[REDACTED]&next=/home", captured["Referer"]);
        Equal("Bearer [REDACTED]", captured["X-Debug"]);
        return Task.CompletedTask;
    }

    internal static Task AuthCookiesAreRedacted()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.test");
        var sensitive = new[]
        {
            ".AspNetCore.Cookies", ".AspNetCore.CookiesC1", ".AspNetCore.CookiesC2", ".AspNetCore.Identity.Application",
            ".AspNetCore.Identity.ApplicationC1", "__Host-session", "__Secure-id", ".AspNetCore.Antiforgery.Xyz", "XSRF-TOKEN",
            "MyAppAuthC1", "checkoutC3", "refresh_token"
        };
        http.Request.Headers.Cookie = string.Join("; ", sensitive.Select(name => $"{name}=v").Append("theme=dark").Append("lang=en"));
        var options = new LogisterAspNetCoreOptions { CaptureRequestCookies = true };
        options.SensitiveRequestCookieNames.Add("checkout");
        foreach (var name in sensitive.Append("theme")) options.AllowedRequestCookieNames.Add(name);

        var request = (IDictionary<string, object?>)LogisterHttpContext.BuildContext(http, options)["request"]!;
        var cookies = (IDictionary<string, object?>)request["cookies"]!;
        foreach (var name in sensitive) Equal(Redacted, cookies[name]);
        Equal("dark", cookies["theme"]);
        Equal(Redacted, cookies["lang"]);
        return Task.CompletedTask;
    }

    internal static Task BaseUrlIsRequiredAndValidated()
    {
        Environment.SetEnvironmentVariable("LOGISTER_BASE_URL", null);
        var missing = new LogisterOptions { ApiKey = "k" };
        Require(missing.BaseUrl is null, "There must be no default base URL.");
        ThrowsConfiguration(() => new LogisterClient(missing, new HttpClient()), "base URL");
        ThrowsConfiguration(() => new LogisterClient(new LogisterOptions { BaseUrl = new Uri("https://logister.test") }, new HttpClient()), "API key");
        ThrowsConfiguration(() => new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("http://logister.example.com") }, new HttpClient()), "http://");
        ThrowsConfiguration(() => new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("ftp://logister.example.com") }, new HttpClient()), "https://");
        ThrowsConfiguration(() => new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("https://user:pw@logister.example.com") }, new HttpClient()), "credentials");
        using (new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("http://localhost:3000") }, new HttpClient())) { }
        using (new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("http://127.0.0.1:3000") }, new HttpClient())) { }
        using (new LogisterClient(new LogisterOptions { ApiKey = "k", BaseUrl = new Uri("http://logister.internal"), AllowInsecureHttp = true }, new HttpClient())) { }

        try
        {
            Environment.SetEnvironmentVariable("LOGISTER_BASE_URL", "not a url");
            ThrowsConfiguration(() => LogisterOptions.FromEnvironment(), "LOGISTER_BASE_URL");
            Environment.SetEnvironmentVariable("LOGISTER_BASE_URL", "https://logister.example.com");
            Environment.SetEnvironmentVariable("LOGISTER_REDACT_KEYS", "tenant-key, internal_ref");
            var fromEnvironment = LogisterOptions.FromEnvironment();
            Equal("https://logister.example.com/", fromEnvironment.BaseUrl?.ToString());
            Require(fromEnvironment.RedactKeys.SetEquals(new[] { "tenant-key", "internal_ref" }), "LOGISTER_REDACT_KEYS was not read.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOGISTER_BASE_URL", null);
            Environment.SetEnvironmentVariable("LOGISTER_REDACT_KEYS", null);
        }

        return Task.CompletedTask;
    }

    internal static async Task AspNetCoreConfigurationValidatesBaseUrl()
    {
        Environment.SetEnvironmentVariable("LOGISTER_BASE_URL", null);
        ThrowsConfiguration(() => new ServiceCollection().AddLogister(Configuration(("ApiKey", "k"), ("BaseUrl", "not a url"))), "Logister:BaseUrl");

        await using var missing = Services(new RecordingHandler(), Configuration(("ApiKey", "k")));
        var service = missing.GetServices<IHostedService>().OfType<LogisterDeliveryService>().Single();
        try
        {
            await service.StartAsync(CancellationToken.None);
            throw new Exception("A missing base URL must fail at startup.");
        }
        catch (LogisterConfigurationException error)
        {
            Require(error.Message.Contains("base URL", StringComparison.Ordinal), error.Message);
        }

        await using var configured = Services(new RecordingHandler(), Configuration(
            ("ApiKey", "k"), ("BaseUrl", "https://logister.test"), ("RedactKeys", "tenant-key"),
            ("AllowedRequestCookieNames:0", "theme"), ("TransactionSampleRate", "0.25"), ("CaptureClientIp", "true")));
        var options = configured.GetRequiredService<LogisterAspNetCoreOptions>();
        Require(options.Client.RedactKeys.Contains("tenant-key") && options.AllowedRequestCookieNames.Contains("theme"), "List settings were not bound.");
        Equal(0.25, options.TransactionSampleRate);
        Require(options.CaptureClientIp, "CaptureClientIp was not bound.");
    }

    internal static async Task HostIdentityIsOptIn()
    {
        var handler = new RecordingHandler();
        var options = Options();
        using (var client = new LogisterClient(options, new HttpClient(handler)))
        {
            await client.CaptureMessageAsync("hello");
        }

        var context = handler.LastJson.RootElement.GetProperty("event").GetProperty("context");
        Require(!context.TryGetProperty("machine_name", out _) && !context.TryGetProperty("process_id", out _), "Host identity sent by default.");

        options.CaptureHostIdentity = true;
        using (var client = new LogisterClient(options, new HttpClient(handler)))
        {
            await client.CaptureMessageAsync("hello");
        }

        context = handler.LastJson.RootElement.GetProperty("event").GetProperty("context");
        Equal(Environment.MachineName, context.GetProperty("machine_name").GetString());
    }

    internal static async Task NonIngestCallsHonourTimeout()
    {
        var options = Options();
        options.Timeout = TimeSpan.FromMilliseconds(200);
        // A caller-supplied HttpClient keeps its 100-second default timeout; the client timeout must still apply.
        using var client = new LogisterClient(options, new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return Accepted();
        })));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.CheckInAsync("job");
            throw new Exception("Expected the check-in to time out.");
        }
        catch (OperationCanceledException)
        {
        }

        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Check-in took {stopwatch.Elapsed}.");
    }

    internal static async Task RetryBackoffIsJittered()
    {
        var delays = new List<double>();
        for (var trial = 0; trial < 8; trial++)
        {
            var stamps = new List<long>();
            var options = Options();
            options.RetryPolicy = new RetryPolicy(maximumAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(200), maximumDelay: TimeSpan.FromSeconds(5));
            using var client = new LogisterClient(options, new HttpClient(new DelegateHandler((_, _) =>
            {
                stamps.Add(Stopwatch.GetTimestamp());
                return Task.FromResult(stamps.Count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Accepted());
            })));
            await client.CaptureMessageAsync("retry");
            delays.Add(Stopwatch.GetElapsedTime(stamps[0], stamps[1]).TotalMilliseconds);
        }

        Require(delays.All(delay => delay >= 90 && delay < 800), $"Delays out of bounds: {string.Join(", ", delays)}.");
        Require(delays.Max() - delays.Min() > 30, $"Delays were not jittered: {string.Join(", ", delays)}.");
    }

    internal static async Task RequestIsNotDelayedBySlowServer()
    {
        var arrived = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async (request, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), token);
            arrived.TrySetResult(await BatchLines(request, token));
            return Accepted();
        });
        await using var provider = Services(handler, Configuration(("ApiKey", "k"), ("BaseUrl", "https://logister.test")));
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);

        var middleware = ActivatorUtilities.CreateInstance<LogisterExceptionMiddleware>(
            provider, (RequestDelegate)(_ => throw new InvalidOperationException("boom")));
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("app.test");
        http.Request.Path = "/slow";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await middleware.InvokeAsync(http);
            throw new Exception("Expected the application exception.");
        }
        catch (InvalidOperationException error) when (error.Message == "boom")
        {
        }

        Require(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Request waited {stopwatch.Elapsed} for delivery.");
        var lines = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(1, lines.Length);
        Equal("boom", JsonDocument.Parse(lines[0]).RootElement.GetProperty("event").GetProperty("message").GetString());
        foreach (var service in hosted) await service.StopAsync(CancellationToken.None);
    }

    internal static async Task TransactionSamplingSkipsRequests()
    {
        using var client = new LogisterClient(Options(), new HttpClient(new RecordingHandler()));
        var logger = new RecordingLogger<LogisterRequestTransactionMiddleware>();
        foreach (var (rate, expected) in new[] { (0.0, 0), (1.0, 2) })
        {
            var queue = new LogisterEventQueue(10);
            var middleware = new LogisterRequestTransactionMiddleware(
                _ => Task.CompletedTask,
                client,
                new LogisterAspNetCoreOptions { CaptureRequestTransactions = true, CaptureRequestSpans = true, TransactionSampleRate = rate },
                queue,
                logger);
            await middleware.InvokeAsync(new DefaultHttpContext());
            Require(logger.Errors.Count == 0, string.Join("\n", logger.Errors));
            Equal(expected, queue.Count);
        }

        try
        {
            _ = new LogisterAspNetCoreOptions { TransactionSampleRate = 1.5 };
            throw new Exception("Sample rates above 1 must be rejected.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    internal static async Task MiddlewareContainsCaptureFailures()
    {
        using var client = new LogisterClient(Options(), new HttpClient(new RecordingHandler()));
        var options = new LogisterAspNetCoreOptions { CaptureRequestTransactions = true, CaptureRequestSpans = true };
        var http = new DefaultHttpContext();
        http.Request.RouteValues["bad"] = new ThrowingToString();

        var queue = new LogisterEventQueue(1);
        var exceptions = new LogisterExceptionMiddleware(
            _ => throw new InvalidOperationException("application failure"),
            client, options, queue, new ThrowingLogger<LogisterExceptionMiddleware>());
        try
        {
            await exceptions.InvokeAsync(http);
            throw new Exception("Expected the application exception.");
        }
        catch (InvalidOperationException error) when (error.Message == "application failure")
        {
        }

        var transactions = new LogisterRequestTransactionMiddleware(
            _ => Task.CompletedTask, client, options, queue, new ThrowingLogger<LogisterRequestTransactionMiddleware>());
        await transactions.InvokeAsync(http);
        Equal(0, queue.Count);

        // A full queue drops events instead of throwing or blocking.
        http.Request.RouteValues.Remove("bad");
        await transactions.InvokeAsync(http);
        await transactions.InvokeAsync(http);
        Equal(1, queue.Count);
        Equal(3L, queue.DroppedCount);
    }

    internal static async Task DeliveryServiceSurvivesFailures()
    {
        var calls = 0;
        var delivered = 0;
        var fail = true;
        var handler = new DelegateHandler(async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (fail) throw new HttpRequestException("network down");
            Interlocked.Add(ref delivered, (await BatchLines(request, token)).Length);
            return Accepted();
        });
        await using var provider = Services(handler, Configuration(("ApiKey", "k"), ("BaseUrl", "https://logister.test")),
            options => options.Client.RetryPolicy = new RetryPolicy(maximumAttempts: 1));
        var queue = provider.GetRequiredService<LogisterEventQueue>();
        var service = provider.GetServices<IHostedService>().OfType<LogisterDeliveryService>().Single();
        using var client = new LogisterClient(Options(), new HttpClient(new RecordingHandler()));

        await service.StartAsync(CancellationToken.None);
        queue.TryEnqueue(client.PrepareEvent("log", "info", "lost"));
        await WaitUntil(() => Volatile.Read(ref calls) > 0);
        await Task.Delay(50);
        Require(service.ExecuteTask is { IsCompleted: false }, "Delivery loop stopped after a failure.");

        fail = false;
        for (var i = 0; i < 3; i++) queue.TryEnqueue(client.PrepareEvent("log", "info", $"kept {i}"));
        await service.StopAsync(CancellationToken.None);
        Equal(3, Volatile.Read(ref delivered));
        Require(service.ExecuteTask is { IsFaulted: false }, "Delivery loop faulted.");
    }

    private static LogisterOptions Options() => new()
    {
        ApiKey = "test-key",
        BaseUrl = new Uri("https://logister.test"),
        RetryPolicy = new RetryPolicy(maximumAttempts: 1)
    };

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair => new KeyValuePair<string, string?>($"Logister:{pair.Key}", pair.Value)))
            .Build();

    private static ServiceProvider Services(HttpMessageHandler handler, IConfiguration configuration, Action<LogisterAspNetCoreOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLogister(configuration, configure);
        services.AddHttpClient<LogisterClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Accepted() =>
        new(HttpStatusCode.Accepted) { Content = new StringContent("{\"status\":\"accepted\"}") };

    private static async Task<string[]> BatchLines(HttpRequestMessage request, CancellationToken token)
    {
        Require(request.RequestUri!.AbsolutePath.EndsWith("/batch", StringComparison.Ordinal), "Queued events must use the batch endpoint.");
        using var stream = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(token));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return (await reader.ReadToEndAsync(token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Condition was not met.");
            await Task.Delay(10);
        }
    }

    private static void ThrowsConfiguration(Action action, string expectedText)
    {
        try
        {
            action();
        }
        catch (LogisterConfigurationException error)
        {
            Require(error.Message.Contains(expectedText, StringComparison.Ordinal), $"Unexpected message: {error.Message}");
            return;
        }

        throw new Exception($"Expected LogisterConfigurationException mentioning {expectedText}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TrackingQueryable : IQueryable<int>
    {
        public bool Enumerated { get; private set; }
        public Type ElementType => typeof(int);
        public Expression Expression => throw new NotSupportedException();
        public IQueryProvider Provider => throw new NotSupportedException();

        public IEnumerator<int> GetEnumerator()
        {
            Enumerated = true;
            return Enumerable.Empty<int>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            Enumerated = true;
            return "executed";
        }
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("ToString failed");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Errors.Add($"{formatter(state, exception)} {exception}");
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failed");
    }
}
