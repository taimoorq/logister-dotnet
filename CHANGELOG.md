# Changelog

## v0.5.0 - 2026-10-23

Security hardening release. It contains breaking changes; read the upgrade notes in the README.

Breaking and default changes:

- **Breaking:** `LogisterOptions.BaseUrl` no longer defaults to `https://logister.org` and is now `Uri?`. A missing base URL throws `LogisterConfigurationException` when the client is created (or at host startup for ASP.NET Core). An unparsable `LOGISTER_BASE_URL` or `Logister:BaseUrl` now throws instead of silently falling back to the vendor host.
- **Breaking:** `http://` base URLs are rejected unless the host is loopback or `AllowInsecureHttp = true`. Base URLs with embedded credentials are rejected.
- **Breaking:** ASP.NET Core middleware no longer awaits delivery. Events are prepared on the request thread, put on a bounded `LogisterEventQueue` (default 1,000 events, then dropped), and sent in batches of up to 100 by a hosted `LogisterDeliveryService` through `SendEventsAsync`. Both middleware constructors now take a `LogisterEventQueue`; `AddLogister` registers both.
- Captured values are redacted by default with the shared Logister rules: sensitive keys (after lowercasing and removing non-alphanumerics) become `[REDACTED]`, and strings are scrubbed for `Bearer`/`Basic` credentials, JWTs, `lgst_` keys, URL passwords and sensitive query parameters. This applies to context, the event message, exception messages, `Exception.Data`, request URL, query string, referer, headers, cookies and route values. Add keys with `LogisterOptions.RedactKeys`, `Logister:RedactKeys` or `LOGISTER_REDACT_KEYS`.
- `client_ip` and forwarding headers (`X-Forwarded-For`, `Forwarded`, `CF-Connecting-IP` and similar) are no longer captured unless `CaptureClientIp` is enabled.
- `machine_name` and `process_id` are no longer sent unless `CaptureHostIdentity` is enabled.
- Captured cookie values are redacted unless the name is in `AllowedRequestCookieNames`. `.AspNetCore.*`, chunked `…C1`/`…C2` cookies, `__Host-`/`__Secure-`, antiforgery, XSRF/CSRF, session and auth cookies stay redacted even when allowlisted. `RedactedCookieValue` now defaults to `[REDACTED]`.
- Header capture applies the shared key rule to header names plus `x-functions-key`, `x-ms-client-principal*`, `x-ms-token-*`, IAP/Cloudflare JWT assertions and webhook signatures. Sensitive header values become `[REDACTED]`; add names with `SensitiveRequestHeaderNames`.

Fixes and additions:

- Normalization of captured values is bounded: depth 12, 5,000 values per captured value, 200 items per collection, and reference cycles become `[Circular]`. A self-referencing context or `Exception.Data` no longer crashes the process with a stack overflow. Only materialized collections are enumerated; `IQueryable` and lazy `IEnumerable` values are reported by type name or `ToString()` and never executed. `NaN`/`Infinity` no longer break serialization.
- Added `TransactionSampleRate` (0–1) for ASP.NET Core request transactions and spans.
- Retry backoff now has ±50% jitter (an explicit `Retry-After` is still honored as sent).
- Every HTTP attempt, including check-ins and deployments, is bounded by `LogisterOptions.Timeout` even with a caller-supplied `HttpClient`.
- Added public `PrepareException`, `PrepareTransaction` and `PrepareSpan` alongside `PrepareEvent`, and a public `LogisterValueSanitizer`.
- Capture failures inside the middleware, including failing log providers, never replace the application's exception or response. The delivery service catches all delivery errors and flushes queued events for up to `ShutdownFlushTimeout` (default 5 s) on shutdown.
- Packages are built deterministically with SourceLink, embedded PDBs, `ContinuousIntegrationBuild` in GitHub Actions and NuGet package validation.

## v0.4.0 - 2026-09-24

- Reuse native W3C Activity identities across ASP.NET Core automatic and manual telemetry.
- Add immutable request snapshots and allowlisted header helpers; preserve application response behavior on failed requests.

## v0.3.0 - 2026-09-11

- Added immutable prepared events with stable UUIDs and capture timestamps, ingestion-only bounded retries, Retry-After handling, and cancellation-aware replay.
- Added bounded gzip/NDJSON batches, deterministic batch IDs, 413 splitting, legacy fallback, and ordered per-event outcomes without skipping later chunks after a failure.
- Restored explicit publication dispatch and immutable-tag recovery for both NuGet packages.
- Made vulnerability reports fail CI when transitive dependencies are affected and verified NuGet signatures plus package content instead of comparing signed and unsigned ZIP bytes.

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
