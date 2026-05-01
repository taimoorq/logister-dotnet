# Changelog

All notable changes to `logister-dotnet` will be documented in this file.

## v0.1.1 - 2026-05-01

This release improves the ASP.NET Core error payloads that power Logister's .NET inbox detail view. Exception reports now show a more accurate failed-request status, and teams can opt in to cookie context while keeping common authentication and session cookies redacted by default.

- Corrected ASP.NET Core exception reports so requests that throw before setting a response status are captured as HTTP 500 instead of the pre-exception status.
- Added opt-in request cookie capture for ASP.NET Core reports, including default redaction for common auth and session cookie names.
- Documented the new cookie capture settings and application-specific redaction list.

## v0.1.0 - 2026-04-30

- Initial .NET SDK with error, log, metric, transaction, and check-in capture.
- Added ASP.NET Core service registration plus exception and request transaction middleware.
