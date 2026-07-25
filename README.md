# logister-dotnet

.NET SDK for sending errors, logs, metrics, transactions, spans, and scheduled-job check-ins to Logister.

This repo contains two packages:

- `Logister`: the base client for services, workers, console apps, and custom integrations.
- `Logister.AspNetCore`: dependency injection plus exception and request-timing middleware for ASP.NET Core.

Both packages target .NET 8 and .NET 10. .NET 9 applications can consume the .NET 8 target.

## Quick start

Create a project in Logister and generate a project API key under **Project settings → API keys**, then install the base package:

```shell
dotnet add package Logister
```

Keep the key in your secret store or environment, not in source control:

```shell
export LOGISTER_API_KEY="<project-api-key>"
export LOGISTER_BASE_URL="https://logister.example.com"
export LOGISTER_ENVIRONMENT="development"
```

Send a test event from an async entry point:

```csharp
using Logister;

using var client = new LogisterClient(LogisterOptions.FromEnvironment());

await client.CaptureExceptionAsync(
    new InvalidOperationException("README test error"),
    new CaptureOptions
    {
        Fingerprint = "readme-test-error",
        Context = new Dictionary<string, object?>
        {
            ["component"] = "checkout"
        }
    });
```

Open the project inbox and confirm that **README test error** appears. A `401` response usually means the API key or base URL is wrong; use the [.NET integration guide](https://logister.org/docs/integrations/dotnet/) for the complete setup and troubleshooting path.

## Package Links

- NuGet `Logister`: https://www.nuget.org/packages/Logister
- NuGet `Logister.AspNetCore`: https://www.nuget.org/packages/Logister.AspNetCore
- GitHub releases: https://github.com/taimoorq/logister-dotnet/releases
- Source repository: https://github.com/taimoorq/logister-dotnet
- Integration docs: https://logister.org/docs/integrations/dotnet/

## Which package should I install?

| App type | Package |
|---|---|
| Worker, console app, service, or custom framework | `Logister` |
| ASP.NET Core app that needs automatic request and exception capture | `Logister.AspNetCore` (which depends on `Logister`) |

The base package uses the built-in `HttpClient` and `System.Text.Json`; it does not add a third-party HTTP, logging, or retry stack to your application.

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

Project Insights guide: https://logister.org/docs/product/#insights

Do not commit real API keys to this repo or your application repo. Use environment variables, .NET user secrets, your hosting provider's secret store, or another deployment secret manager.

## ASP.NET Core

Add configuration:

```json
{
  "Logister": {
    "ApiKey": "your-project-api-token",
    "BaseUrl": "https://your-logister-host.example",
    "Environment": "production",
    "Release": "checkout@2026.04.30",
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
    options.Client.DefaultContext["service"] = "checkout-web";
    options.CaptureRequestCookies = true;
    options.SensitiveRequestCookieNames.Add("checkout_auth");
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

## Using project Insights

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

## GitHub source context and deployments

When a Logister project is connected to a GitHub repository, set source context once so events can resolve stack frames to the exact deployed code:

```csharp
var options = LogisterOptions.FromEnvironment();
options.Repository = "acme/checkout";
options.CommitSha = "4f8c2d1a9b7e6c5d4a3b2c1d0e9f8a7b6c5d4e3f";
options.Branch = "main";

using var client = new LogisterClient(options);
```

ASP.NET Core apps can also use `Logister:Repository`, `Logister:CommitSha`, and `Logister:Branch` configuration keys. `LogisterOptions.FromEnvironment()` reads `LOGISTER_REPOSITORY`, `LOGISTER_COMMIT_SHA`, and `LOGISTER_BRANCH`, falling back to GitHub Actions variables when present.

CI/CD can record the release-to-commit mapping directly:

```csharp
await client.RecordDeploymentAsync(new DeploymentOptions
{
    Release = "checkout@2026.06.18",
    Environment = "production",
    Repository = "acme/checkout",
    CommitSha = "4f8c2d1a9b7e6c5d4a3b2c1d0e9f8a7b6c5d4e3f",
    Branch = "main",
    WorkflowRunUrl = "https://github.com/acme/checkout/actions/runs/123"
});
```

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
- `LOGISTER_REPOSITORY`
- `LOGISTER_COMMIT_SHA`
- `LOGISTER_BRANCH`
- `LOGISTER_TIMEOUT`

## Development

```shell
dotnet restore Logister.sln
dotnet list Logister.sln package --vulnerable --include-transitive --no-restore
dotnet build Logister.sln --configuration Release --no-restore
dotnet run --project tests/Logister.Tests/Logister.Tests.csproj --framework net8.0 --configuration Release --no-restore
dotnet run --project tests/Logister.Tests/Logister.Tests.csproj --framework net10.0 --configuration Release --no-restore
```

## Publishing

Pull requests and pushes to `main` restore, audit, build, test, and pack both target frameworks. After CI passes on `main`, the release-from-main workflow creates the matching `vX.Y.Z` tag. The tag workflow publishes both NuGet packages before creating the GitHub Release.

Repository setup:

- Add a GitHub Actions secret named `NUGET_API_KEY` with permission to publish the `Logister` and `Logister.AspNetCore` packages.
- The NuGet package IDs are `Logister` and `Logister.AspNetCore`.
- The release workflow configuration lives at `config/release.yml`.

Release process:

1. Bump the `<Version>` value in both package project files to the next NuGet version.
2. Add a matching `CHANGELOG.md` section named `## vX.Y.Z - YYYY-MM-DD`.
3. Merge the change to `main` and let the release-from-main workflow create and dispatch the matching tag, or create the tag manually:

```shell
git tag vX.Y.Z
git push origin vX.Y.Z
```

The release workflow verifies that the tag matches both `.csproj` package versions, audits and tests both frameworks, packs both packages, publishes missing NuGet packages, and then uses the matching changelog section as the GitHub release notes. NuGet versions are immutable; if either package has been published, make corrections in a new patch version.

Verify both package IDs and the GitHub Release before calling a release complete:

```shell
curl -fsSL https://api.nuget.org/v3-flatcontainer/logister/index.json
curl -fsSL https://api.nuget.org/v3-flatcontainer/logister.aspnetcore/index.json
gh release view vX.Y.Z
```
