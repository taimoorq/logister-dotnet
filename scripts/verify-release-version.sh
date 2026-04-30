#!/usr/bin/env bash
set -euo pipefail

release_ref="${1:-${GITHUB_REF_NAME:-}}"

if [ -z "$release_ref" ]; then
  echo "Release tag is required. Pass a tag like v0.1.0." >&2
  exit 1
fi

release_version="${release_ref#v}"

if [ "$release_ref" = "$release_version" ] || [ -z "$release_version" ]; then
  echo "Release tag must start with v, for example v0.1.0." >&2
  exit 1
fi

projects=(
  "src/Logister/Logister.csproj"
  "src/Logister.AspNetCore/Logister.AspNetCore.csproj"
)

for project in "${projects[@]}"; do
  version="$(sed -nE 's:.*<Version>([^<]+)</Version>.*:\1:p' "$project" | head -n 1)"

  if [ -z "$version" ]; then
    echo "Could not find <Version> in $project." >&2
    exit 1
  fi

  if [ "$version" != "$release_version" ]; then
    echo "$project version $version does not match release tag $release_ref." >&2
    exit 1
  fi
done

echo "Release tag $release_ref matches NuGet package version $release_version."
