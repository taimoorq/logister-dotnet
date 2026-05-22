# logister-dotnet

.NET SDK for sending errors, logs, metrics, transactions, spans, and check-ins to Logister.

This repo contains two packages:

- `Logister`: base client for any .NET 8+ app.
- `Logister.AspNetCore`: service registration and middleware for ASP.NET Core apps.

## Package Links

- NuGet `Logister`: https://www.nuget.org/packages/Logister
- NuGet `Logister.AspNetCore`: https://www.nuget.org/packages/Logister.AspNetCore
- GitHub releases: https://github.com/taimoorq/logister-dotnet/releases
- Source repository: https://github.com/taimoorq/logister-dotnet
- Integration docs: https://docs.logister.org/integrations/dotnet/

## Package strategy

Use NuGet.org as the public package registry:

- `Logister` should stay dependency-light and use the built-in `HttpClient`, `System.Text.Json`, and runtime APIs.
- `Logister.AspNetCore` should use Microsoft's ASP.NET Core shared framework and `IHttpClientFactory` through `AddHttpClient`.
- Avoid third-party HTTP, JSON, logging, or retry packages until the SDK has a concrete need. That keeps installation predictable for ASP.NET Core apps like QuriaTime and avoids dependency conflicts for library consumers.

## Install

```shell
dotnet add package Logister
dotnet add package Logister.AspNetCore
```

For local development, forks, or unreleased SDK changes, reference the projects locally:

```xml
<ProjectReference Include="../logister-dotnet/src/Logister/Logister.csproj" />
<ProjectReference Include="../logister-dotnet/src/Logister.AspNetCore/Logister.AspNetCore.csproj" />
```

## Connect to Logister

In the Logister web app:

1. Create or open a project.
2. Set the integration type to `.NET / ASP.NET Core`.
3. Generate a project API key from project settings.
4. Configure your .NET app with that API key and your Logister base URL.

Project Insights beta guide: https://docs.logister.org/product/#insights-beta

Do not commit real API keys to this repo or your application repo. Use environment variables, .NET user secrets, your hosting provider's secret store, or another deployment secret manager.

## ASP.NET Core

Add configuration:

```json
{
  "Logister": {
    "ApiKey": "your-project-api-token",
    "BaseUrl": "https://your-logister-host.example",
    "Environment": "production",
    "Release": "quriatime@2026.04.30",
    "CaptureRequestTransactions": true,
    "CaptureRequestSpans": true,
    "CaptureRequestHeaders": true,
    "CaptureRequestCookies": false
  }
}
```

For local development, prefer user secrets or environment variables for the real token:

```shell
dotnet user-secrets set "Logister:ApiKey" "your-project-api-token"
dotnet user-secrets set "Logister:BaseUrl" "https://your-logister-host.example"
```

Wire it into `Program.cs`:

```csharp
using Logister.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogister(builder.Configuration, options =>
{
    options.Client.DefaultContext["service"] = "quriatime-web";
    options.CaptureRequestCookies = true;
    options.SensitiveRequestCookieNames.Add("quriatime_auth");
});

var app = builder.Build();

app.UseLogisterExceptionReporting();
app.UseLogisterRequestTransactions();

app.Run();
```

Cookie capture is disabled by default because cookie values often contain authentication or session material. When enabled, common ASP.NET Core auth and session cookie names are redacted automatically. Add application-specific cookie names to `SensitiveRequestCookieNames` when you want the cookie name to appear in Logister without storing its value.

## Direct client

```csharp
using Logister;

var client = new LogisterClient(new LogisterOptions
{
    ApiKey = Environment.GetEnvironmentVariable("LOGISTER_API_KEY"),
    BaseUrl = new Uri("https://your-logister-host.example"),
    Environment = "production",
    Release = "worker@2026.04.30"
});

try
{
    RunImport();
}
catch (Exception exception)
{
    await client.CaptureExceptionAsync(exception, new CaptureOptions
    {
        Context = new Dictionary<string, object?>
        {
            ["job"] = "nightly-import"
        }
    });
}

await client.CaptureMetricAsync("timesheet.approvals.pending", 7, new MetricOptions
{
    Unit = "count"
});

await client.CaptureSpanAsync("render checkout", 82.1, new SpanOptions
{
    Kind = "render",
    Status = "ok",
    TraceId = "trace-123",
    ParentSpanId = "span-root",
    Context = new Dictionary<string, object?>
    {
        ["route"] = "POST /checkout"
    }
});

await client.CheckInAsync("nightly-import", "ok", new CheckInOptions
{
    Release = "worker@2026.04.30",
    DurationMs = 122.5,
    ExpectedIntervalSeconds = 3600,
    TraceId = "trace-123",
    RequestId = "req-123"
});
```

`CaptureOptions` supports per-event `Environment`, `Release`, `TraceId`, `RequestId`, `SessionId`, and `UserId` for errors, logs, metrics, and transactions. `MetricOptions` adds `Unit`; `SpanOptions` adds `SpanId`, `ParentSpanId`, `Kind`, `Status`, `StartedAt`, and `EndedAt`; and `CheckInOptions` supports `Release`, `DurationMs`, `ExpectedIntervalSeconds`, `TraceId`, and `RequestId` so monitor records line up with the Logister API.

## Using project Insights beta

The Logister project Insights tab combines Inbox, Activity, and Performance data into live dashboard views. .NET services get the most useful Insights view when they send consistent `Environment`, `Release`, and stable top-level context attributes.

Set deployment context once through configuration or environment variables, then attach low-cardinality dimensions to metrics, transactions, logs, and check-ins:

```csharp
using Logister;

var options = LogisterOptions.FromEnvironment();
options.DefaultContext["service"] = "billing-api";
options.DefaultContext["region"] = "us-east-1";

using var client = new LogisterClient(options);

await client.CaptureMetricAsync("queue.depth", 42, new MetricOptions
{
    Unit = "jobs",
    Context = new Dictionary<string, object?>
    {
        ["service"] = "billing-worker",
        ["queue"] = "billing",
        ["tenant_tier"] = "enterprise"
    }
});

await client.CaptureTransactionAsync("POST /checkout", 182.4, new CaptureOptions
{
    RequestId = "req_123",
    Context = new Dictionary<string, object?>
    {
        ["route"] = "POST /checkout",
        ["feature_flag"] = "new_checkout",
        ["tenant_tier"] = "enterprise"
    }
});

await client.CaptureSpanAsync("render checkout", 82.1, new SpanOptions
{
    Kind = "render",
    Status = "ok",
    TraceId = "trace_123",
    ParentSpanId = "span_root",
    Context = new Dictionary<string, object?>
    {
        ["route"] = "POST /checkout"
    }
});

await client.CaptureMessageAsync("payment provider retry", new CaptureOptions
{
    Level = "warn",
    Context = new Dictionary<string, object?>
    {
        ["service"] = "billing-worker",
        ["provider"] = "stripe",
        ["queue"] = "billing"
    }
});

await client.CheckInAsync("nightly-reconcile", "ok", new CheckInOptions
{
    ExpectedIntervalSeconds = 3600,
    DurationMs = 842.7,
    Context = new Dictionary<string, object?>
    {
        ["service"] = "billing-worker",
        ["queue"] = "reconcile"
    }
});
```

Practical Insights recipes:

- Release validation: set `LOGISTER_RELEASE` or `Logister:Release`, then filter Insights to the new release and compare error count, transaction P95, and custom metrics.
- Queue monitoring: report metrics such as `queue.depth`, `queue.latency`, `jobs.retry_count`, and `worker.active_jobs` with stable `queue` and `service` context keys.
- ASP.NET Core performance triage: enable `CaptureRequestSpans` to feed request load waterfall charts, then add matching `route`, `tenant_tier`, or `feature_flag` context to custom logs and metrics.
- Instrumentation audit: open Insights after deploy and confirm errors, logs, metrics, transactions, spans, and check-ins all appear in the recent stream.

Keep custom attributes stable and low-cardinality. Good top-level context keys include `service`, `region`, `queue`, `route`, `tenant_tier`, `provider`, and `feature_flag`. Avoid raw IDs, emails, request bodies, SQL text, and per-user values as Insights dimensions.

## Environment variables

The base client can be created from environment variables:

```csharp
var client = new LogisterClient(LogisterOptions.FromEnvironment());
```

Supported variables:

- `LOGISTER_API_KEY`
- `LOGISTER_BASE_URL`
- `LOGISTER_ENVIRONMENT`
- `LOGISTER_RELEASE`
- `LOGISTER_TIMEOUT`

## Development

```shell
dotnet restore
dotnet build
dotnet run --project tests/Logister.Tests
dotnet pack -c Release
```

## Publishing

Pull requests and pushes to `main` run CI: restore, build, tests, and package creation. A commit or merge alone does not publish to NuGet. NuGet publishing happens from `v*` release tags in the same workflow that creates the GitHub Release, so NuGet package versions and GitHub Releases stay aligned.

Repository setup:

- Add a GitHub Actions secret named `NUGET_API_KEY` with permission to publish the `Logister` and `Logister.AspNetCore` packages.
- The NuGet package IDs are `Logister` and `Logister.AspNetCore`.
- The release workflow configuration lives at `config/release.yml`.

Release process:

1. Bump the `<Version>` value in both package project files to the next NuGet version.
2. Add a matching `CHANGELOG.md` section named `## vX.Y.Z - YYYY-MM-DD`.
3. Merge the change to `main`.
4. Create and push a matching tag:

```shell
git tag vX.Y.Z
git push origin vX.Y.Z
```

The release workflow verifies that the tag version matches both `.csproj` package versions, runs tests, packs both packages, publishes missing NuGet packages, and then uses the matching changelog section as the GitHub release notes. The NuGet push uses `--skip-duplicate`, so rerunning a workflow for an already-published version will not republish the package.
