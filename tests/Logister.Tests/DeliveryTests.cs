using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Logister;

internal static class DeliveryTests
{
    internal static async Task SnapshotRetries()
    {
        var bodies = new List<string>();
        var context = new Dictionary<string, object?> { ["nested"] = new Dictionary<string, object?> { ["value"] = "original" } };
        using var client = Client(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            ((Dictionary<string, object?>)context["nested"]!)["value"] = "changed";
            return Response(bodies.Count == 1 ? 503 : 202);
        });
        var prepared = client.PrepareEvent("log", "info", "hello", context);
        await client.SendPreparedEventAsync(prepared);
        await client.SendPreparedEventAsync(prepared);
        Require(bodies.Count == 3 && bodies.Distinct().Count() == 1, "Retry/replay bytes changed.");
        var payload = JsonDocument.Parse(bodies[0]).RootElement.GetProperty("event");
        Require(payload.GetProperty("uuid").GetGuid() == prepared.EventId, "UUID changed.");
        Require(payload.GetProperty("context").GetProperty("nested").GetProperty("value").GetString() == "original", "Caller mutation changed capture.");
        Require(payload.GetProperty("occurred_at").GetDateTimeOffset() == prepared.OccurredAt, "Timestamp changed.");
    }

    internal static async Task BoundedFailures()
    {
        var calls = 0;
        using var network = Client((_, _) => { calls++; throw new HttpRequestException("response lost"); });
        await Throws<HttpRequestException>(() => network.CaptureMessageAsync("hello"));
        Require(calls == 3, "Network attempts were not bounded.");
        foreach (var status in new[] { 400, 401, 403, 422 })
        {
            calls = 0;
            using var permanent = Client((_, _) => { calls++; return Task.FromResult(Response(status)); });
            await Throws<LogisterException>(() => permanent.CaptureMessageAsync("hello"));
            Require(calls == 1, "Permanent rejection retried.");
        }
        calls = 0;
        using var checkIn = Client((_, _) => { calls++; return Task.FromResult(Response(503)); });
        await Throws<LogisterException>(() => checkIn.CheckInAsync("job"));
        Require(calls == 1, "A non-ingestion write was retried.");
    }

    internal static async Task BatchFallback()
    {
        var bodies = new List<string>();
        var calls = 0;
        using var client = Client(async (request, token) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/batch")) return Response(501);
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return Response(calls == 2 ? 422 : 202);
        });
        var events = Enumerable.Range(0, 3).Select(i => client.PrepareEvent("log", "info", i.ToString())).ToArray();
        var results = await client.SendEventsAsync(events);
        Require(calls == 4 && results.Count == 3, "Fallback skipped events or retried unsupported batching.");
        Require(results[0].Error is LogisterException && results.Skip(1).All(result => result.Response is not null), "Partial outcomes were lost.");
        Require(results.Select(result => result.EventId).SequenceEqual(events.Select(item => item.EventId)), "Result identity/order changed.");
        Require(bodies.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("event").GetProperty("uuid").GetGuid()).SequenceEqual(events.Select(item => item.EventId)), "Fallback regenerated UUIDs.");
    }

    internal static async Task BatchSplittingAndChunks()
    {
        var ids = new List<Guid>();
        using var split = Client(async (request, token) =>
        {
            var lines = await Lines(request, token);
            if (lines.Length > 1) return Response(413);
            ids.Add(JsonDocument.Parse(lines[0]).RootElement.GetProperty("event").GetProperty("uuid").GetGuid());
            return Response(202);
        });
        var events = Enumerable.Range(0, 3).Select(i => split.PrepareEvent("log", "info", i.ToString())).ToArray();
        var results = await split.SendEventsAsync(events);
        Require(results.All(result => result.Response is not null) && ids.SequenceEqual(events.Select(item => item.EventId)), "413 splitting changed identity or outcomes.");

        var counts = new List<int>();
        using var chunks = Client(async (request, token) =>
        {
            counts.Add((await Lines(request, token)).Length);
            return Response(counts.Count == 1 ? 422 : 202);
        });
        var prepared = chunks.PrepareEvent("log", "info", "hello");
        results = await chunks.SendEventsAsync(Enumerable.Repeat(prepared, 101));
        Require(counts.SequenceEqual(new[] { 100, 1 }) && results.Count == 101 && results[0].Error is not null && results[100].Response is not null, "Failed chunk skipped later work.");
        await Throws<ArgumentException>(() => chunks.SendEventsAsync(Enumerable.Repeat(prepared, 1001)));
        Require(counts.Count == 2, "Batch bound was checked after side effects.");
    }

    internal static async Task CancellationAndDeadline()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = Client((_, _) =>
        {
            calls++;
            var response = Response(429);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            cancellation.CancelAfter(20);
            return Task.FromResult(response);
        }, new RetryPolicy(maximumDelay: TimeSpan.FromSeconds(2)));
        await Throws<OperationCanceledException>(() => client.CaptureMessageAsync("hello", cancellationToken: cancellation.Token));
        Require(calls == 1, "Cancellation did not stop backoff.");
        using var deadline = Client(async (_, token) =>
        {
            await Task.Delay(1000, token);
            return Response(202);
        }, new RetryPolicy(totalTimeout: TimeSpan.FromMilliseconds(30)));
        await Throws<TimeoutException>(() => deadline.CaptureMessageAsync("hello"));
    }

    private static LogisterClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, RetryPolicy? policy = null) =>
        new(new LogisterOptions { ApiKey = "test-token", BaseUrl = new Uri("https://logister.example"), RetryPolicy = policy ?? new RetryPolicy(baseDelay: TimeSpan.Zero, maximumDelay: TimeSpan.Zero) }, new HttpClient(new Handler(send)));

    private static HttpResponseMessage Response(int status) => new((HttpStatusCode)status) { Content = new StringContent("{\"status\":\"accepted\"}") };

    private static async Task<string[]> Lines(HttpRequestMessage request, CancellationToken token)
    {
        Require(request.Content!.Headers.ContentEncoding.Contains("gzip"), "Batch is not gzip encoded.");
        Require(request.Headers.Contains("X-Logister-Batch-Id"), "Batch identity missing.");
        using var stream = new MemoryStream(await request.Content.ReadAsByteArrayAsync(token));
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return (await reader.ReadToEndAsync(token)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
