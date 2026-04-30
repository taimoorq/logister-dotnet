# logister-dotnet

.NET SDK for sending errors, logs, metrics, transactions, and check-ins to Logister.

This repo contains two packages:

- `Logister`: base client for any .NET 8+ app.
- `Logister.AspNetCore`: service registration and middleware for ASP.NET Core apps.

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

Until the packages are published, reference the projects locally:

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
    "CaptureRequestTransactions": true
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
});

var app = builder.Build();

app.UseLogisterExceptionReporting();
app.UseLogisterRequestTransactions();

app.Run();
```

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

await client.CheckInAsync("nightly-import", "ok", new CheckInOptions
{
    DurationMs = 122.5,
    ExpectedIntervalSeconds = 3600
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
- `LOGISTER_TIMEOUT`

## Development

```shell
dotnet restore
dotnet build
dotnet run --project tests/Logister.Tests
dotnet pack -c Release
```

## Publishing

Pull requests and pushes to `main` run CI: restore, build, tests, and package creation. NuGet publishing happens from `v*` release tags so GitHub Releases and NuGet package versions stay aligned.

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

The publish workflow verifies that the tag version matches both `.csproj` package versions before pushing to NuGet. The GitHub release workflow uses the matching changelog section as the release notes. The NuGet push uses `--skip-duplicate`, so rerunning a workflow for an already-published version will not republish the package.
