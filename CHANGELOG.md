# Changelog

All notable changes to `logister-dotnet` will be documented in this file.

## v0.2.1 - 2026-08-09

- Send the canonical `occurred_at` field for monitor check-ins so Logister preserves caller-supplied timestamps.
- Keep both NuGet package versions and the default SDK user agent aligned for the release.

## v0.2.0 - 2026-07-25

- Added .NET 10 targets while retaining .NET 8 compatibility for the SDK, ASP.NET Core integration, and tests.
- Added a transitive NuGet vulnerability audit to CI and releases.
- Pinned GitHub Actions to immutable commits and removed the duplicate release dispatch path.

## v0.1.5 - 2026-06-18

- Added first-class source context fields (`Repository`, `CommitSha`, and `Branch`) with `LOGISTER_*`, GitHub Actions environment variable, and ASP.NET Core configuration support.
- Added `RecordDeploymentAsync` for posting release-to-commit deployment records to Logister.

## v0.1.4 - 2026-05-22

- Added `CaptureSpanAsync` plus opt-in ASP.NET Core request span capture for request load waterfall charts.

## v0.1.3 - 2026-05-22

- Added README guidance for using .NET reports with the Logister project Insights beta, including practical metric, transaction, log, check-in, and custom attribute examples.
- Consolidated tag releases so both NuGet packages publish before the matching GitHub release is created or updated.

## v0.1.2 - 2026-05-21

- Added top-level `release` to .NET check-in payloads so monitor records match the Logister API contract.
- Covered check-in release, expected interval, trace ID, and request ID fields in SDK tests.

## v0.1.1 - 2026-05-01

This release improves the ASP.NET Core error payloads that power Logister's .NET inbox detail view. Exception reports now show a more accurate failed-request status, and teams can opt in to cookie context while keeping common authentication and session cookies redacted by default.

- Corrected ASP.NET Core exception reports so requests that throw before setting a response status are captured as HTTP 500 instead of the pre-exception status.
- Added opt-in request cookie capture for ASP.NET Core reports, including default redaction for common auth and session cookie names.
- Documented the new cookie capture settings and application-specific redaction list.

## v0.1.0 - 2026-04-30

- Initial .NET SDK with error, log, metric, transaction, and check-in capture.
- Added ASP.NET Core service registration plus exception and request transaction middleware.
