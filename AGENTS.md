# Logister .NET SDK Agent Notes

This is a public NuGet package repository. Never commit API keys, private
telemetry, customer data, or local package-source credentials.

## Dependency and framework maintenance

- `src/Logister/Logister.csproj` and
  `src/Logister.AspNetCore/Logister.AspNetCore.csproj` are joint package-version
  sources of truth. Their `<Version>` and `<TargetFrameworks>` values must match.
- Keep the test project on the same target frameworks. CI currently restores,
  audits, builds, tests, and packs both `net8.0` and `net10.0`.
- Audit transitive packages, not only direct references.
- Keep NuGet and GitHub Actions Dependabot updates enabled. Pin Actions to full
  commit SHAs and retain a readable version comment.

## Verification

Run before handoff or release:

```bash
dotnet restore Logister.sln
dotnet list Logister.sln package --vulnerable --include-transitive --no-restore
dotnet build Logister.sln --configuration Release --no-restore
dotnet run --project tests/Logister.Tests/Logister.Tests.csproj --framework net8.0 --configuration Release --no-restore
dotnet run --project tests/Logister.Tests/Logister.Tests.csproj --framework net10.0 --configuration Release --no-restore
dotnet pack src/Logister/Logister.csproj --configuration Release --output artifacts/packages
dotnet pack src/Logister.AspNetCore/Logister.AspNetCore.csproj --configuration Release --output artifacts/packages
```

## Release contract

- Update both package versions and `CHANGELOG.md` together. Run
  `scripts/verify-release-version.sh vX.Y.Z` before tagging.
- Merging a new version to `main` runs CI, creates `vX.Y.Z`, and explicitly
  dispatches `release.yml`. Keep the explicit dispatch because tags pushed with
  `GITHUB_TOKEN` do not start tag-push workflows.
- Build and test both frameworks, publish both NuGet packages, and only then
  create the GitHub Release. Treat a release as incomplete until both package
  IDs have propagated.
- NuGet versions are immutable. Never reuse an accepted version.
- Verify all release surfaces:

```bash
curl -fsSL https://api.nuget.org/v3-flatcontainer/logister/index.json
curl -fsSL https://api.nuget.org/v3-flatcontainer/logister.aspnetcore/index.json
gh release view vX.Y.Z
```
