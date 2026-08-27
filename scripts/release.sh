#!/usr/bin/env bash
set -Eeuo pipefail

REPO="FunplayAI/funplay-unity-mcp"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_ROOT="${UNITY_PROJECT_ROOT:-$(cd "$ROOT/../.." && pwd)}"
ARTIFACT_ROOT="${RELEASE_ARTIFACT_DIR:-$PROJECT_ROOT/release-artifacts}"
WRAPPER_ROOT="${MCP_WRAPPER_ROOT:-}"
VALIDATION_TMP=""

if [[ -z "$WRAPPER_ROOT" && -d "$PROJECT_ROOT/mcp-release/funseaai-unity-mcp" ]]; then
  WRAPPER_ROOT="$PROJECT_ROOT/mcp-release/funseaai-unity-mcp"
fi

usage() {
  cat <<'EOF'
Usage: scripts/release.sh <version> [options]

Local release helper for Funplay Unity MCP.

Default behavior:
  - bump package.json and local wrapper versions when the wrapper workspace exists
  - run Unity EditMode tests
  - export Assets/unity-mcp as a filtered unitypackage without local-only files or tests
  - validate all unitypackage pathnames stay under Assets/unity-mcp
  - generate release notes, SHA256SUMS, and a release manifest
  - dotnet pack the stdio wrapper when the wrapper workspace exists
  - archive local artifacts under release-artifacts/<version>

Publishing is opt-in:
  --publish-github     Create or update the GitHub Release asset
  --publish-nuget      Push the wrapper nupkg to NuGet; requires NUGET_API_KEY
  --publish-registry   Publish server.json with mcp-publisher after NuGet indexing
  --verify-openupm     Wait for OpenUPM to index the Unity package version

Options:
  --no-tests           Skip Unity EditMode tests
  --no-export          Skip unitypackage export; validates an existing package if present
  --no-wrapper         Skip wrapper version bump and dotnet pack
  --dry-run            Print major actions without mutating files or publishing
  -h, --help           Show this help

Environment:
  UNITY_BIN            Full path to the Unity executable. Auto-detected on macOS if omitted.
  UNITY_PROJECT_ROOT   Unity project root. Defaults to the parent project containing Assets/.
  MCP_WRAPPER_ROOT     Private stdio wrapper workspace, if available.
  MCP_PUBLISHER_BIN    mcp-publisher binary for registry publishing.
  NUGET_API_KEY        NuGet API key for --publish-nuget.
EOF
}

fail() {
  echo "release: $*" >&2
  exit 1
}

info() {
  echo "==> $*"
}

VERSION="${1:-}"
if [[ -z "$VERSION" || "$VERSION" == "-h" || "$VERSION" == "--help" ]]; then
  usage
  exit 0
fi
shift || true

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || fail "invalid version: $VERSION"

RUN_TESTS=1
EXPORT_PACKAGE=1
USE_WRAPPER=1
PUBLISH_GITHUB=0
PUBLISH_NUGET=0
PUBLISH_REGISTRY=0
VERIFY_OPENUPM=0
DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-tests) RUN_TESTS=0 ;;
    --no-export) EXPORT_PACKAGE=0 ;;
    --no-wrapper) USE_WRAPPER=0 ;;
    --publish-github) PUBLISH_GITHUB=1 ;;
    --publish-nuget) PUBLISH_NUGET=1 ;;
    --publish-registry) PUBLISH_REGISTRY=1 ;;
    --verify-openupm) VERIFY_OPENUPM=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown option: $1" ;;
  esac
  shift
done

OUT_DIR="$ARTIFACT_ROOT/$VERSION"
PACKAGE_OUTPUT="$OUT_DIR/Funplay.UnityMcp.v$VERSION.unitypackage"
NOTES_FILE="$OUT_DIR/release-notes.md"
SHA_FILE="$OUT_DIR/SHA256SUMS.txt"
MANIFEST_FILE="$OUT_DIR/release-manifest.json"

run() {
  if [[ "$DRY_RUN" == 1 ]]; then
    printf '+'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

run_unity_export() {
  if [[ "$DRY_RUN" == 1 ]]; then
    printf '+'
    printf ' %q' "$@"
    printf '\n'
    return
  fi

  local log_file="$OUT_DIR/unitypackage-export.log"
  local timeout_seconds="${UNITY_EXPORT_TIMEOUT_SECONDS:-180}"
  local grace_seconds="${UNITY_EXPORT_EXIT_GRACE_SECONDS:-5}"
  local pid
  local elapsed=0
  local grace_elapsed=0

  "$@" &
  pid=$!

  while kill -0 "$pid" >/dev/null 2>&1; do
    if [[ -f "$PACKAGE_OUTPUT" ]] &&
       [[ -f "$log_file" ]] &&
       rg -q "Exported unitypackage to " "$log_file"; then
      if (( grace_elapsed >= grace_seconds )); then
        info "Unity export completed; terminating lingering batchmode process"
        kill "$pid" >/dev/null 2>&1 || true
        wait "$pid" >/dev/null 2>&1 || true
        return 0
      fi

      sleep 1
      grace_elapsed=$((grace_elapsed + 1))
      elapsed=$((elapsed + 1))
      continue
    fi

    if (( elapsed >= timeout_seconds )); then
      kill "$pid" >/dev/null 2>&1 || true
      wait "$pid" >/dev/null 2>&1 || true
      fail "Unity export timed out after ${timeout_seconds}s"
    fi

    sleep 1
    elapsed=$((elapsed + 1))
  done

  wait "$pid"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "missing required command: $1"
}

find_unity_bin() {
  if [[ -n "${UNITY_BIN:-}" ]]; then
    [[ -x "$UNITY_BIN" ]] || fail "UNITY_BIN is not executable: $UNITY_BIN"
    printf '%s\n' "$UNITY_BIN"
    return
  fi

  if [[ "$(uname -s)" == "Darwin" ]]; then
    local candidate
    for candidate in /Applications/Unity/Hub/Editor/*/Unity.app/Contents/MacOS/Unity; do
      [[ -x "$candidate" ]] && { printf '%s\n' "$candidate"; return; }
    done
  fi
}

bump_versions() {
  info "Bumping Unity package version to $VERSION"
  run node - "$ROOT/package.json" "$VERSION" <<'NODE'
const fs = require("fs");
const path = process.argv[2];
const version = process.argv[3];
const text = fs.readFileSync(path, "utf8");
const updated = text.replace(/("version"\s*:\s*")[^"]+(")/, `$1${version}$2`);
if (updated === text && !text.includes(`"version": "${version}"`)) {
  throw new Error("Unable to update version in " + path);
}
fs.writeFileSync(path, updated);
NODE

  info "Updating OpenUPM documentation snippets"
  run node - "$ROOT/README.md" "$ROOT/README_CN.md" "$VERSION" <<'NODE'
const fs = require("fs");
const files = process.argv.slice(2, -1);
const version = process.argv[process.argv.length - 1];
for (const file of files) {
  if (!fs.existsSync(file)) continue;
  const text = fs.readFileSync(file, "utf8");
  const updated = text.replace(
    /("com\.gamebooom\.unity\.mcp"\s*:\s*")[^"]+(")/g,
    `$1${version}$2`
  );
  fs.writeFileSync(file, updated);
}
NODE

  if [[ "$USE_WRAPPER" == 1 && -n "$WRAPPER_ROOT" && -d "$WRAPPER_ROOT" ]]; then
    info "Bumping wrapper version in $WRAPPER_ROOT"
    run perl -0pi -e "s#<Version>[^<]+</Version>#<Version>$VERSION</Version>#" "$WRAPPER_ROOT/FunseaAI.Unity.Mcp.csproj"
    run perl -0pi -e "s#funplay-unity-mcp [0-9]+\\.[0-9]+\\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?#funplay-unity-mcp $VERSION#g" "$WRAPPER_ROOT/Program.cs"
    run node - "$WRAPPER_ROOT/server.json" "$VERSION" <<'NODE'
const fs = require("fs");
const path = process.argv[2];
const version = process.argv[3];
const json = JSON.parse(fs.readFileSync(path, "utf8"));
json.version = version;
for (const pkg of json.packages || []) pkg.version = version;
fs.writeFileSync(path, JSON.stringify(json, null, 2) + "\n");
NODE
  elif [[ "$USE_WRAPPER" == 1 ]]; then
    info "Wrapper workspace not found; skipping wrapper bump and pack"
  fi
}

run_git_checks() {
  info "Running git diff check"
  run git -C "$ROOT" diff --check
}

run_unity_tests() {
  [[ "$RUN_TESTS" == 1 ]] || { info "Skipping Unity tests"; return; }

  local unity_bin
  if [[ "$DRY_RUN" == 1 ]]; then
    unity_bin="${UNITY_BIN:-Unity}"
  else
    unity_bin="$(find_unity_bin)"
    [[ -n "$unity_bin" ]] || fail "Unity executable not found; set UNITY_BIN or pass --no-tests"
  fi

  info "Running Unity EditMode tests"
  run "$unity_bin" \
    -batchmode \
    -projectPath "$PROJECT_ROOT" \
    -runTests \
    -testPlatform EditMode \
    -testResults "$OUT_DIR/editmode-results.xml" \
    -logFile "$OUT_DIR/unity-editmode-tests.log"
}

cleanup_exporter() {
  if [[ -n "${EXPORTER_FILE:-}" ]]; then
    rm -f "$EXPORTER_FILE" "$EXPORTER_FILE.meta"
  fi
  if [[ "${CREATED_EDITOR_DIR:-0}" == 1 ]]; then
    rmdir "$PROJECT_ROOT/Assets/Editor" 2>/dev/null || true
  fi
}

export_unitypackage() {
  [[ "$EXPORT_PACKAGE" == 1 ]] || { info "Skipping unitypackage export"; return; }

  local unity_bin
  if [[ "$DRY_RUN" == 1 ]]; then
    unity_bin="${UNITY_BIN:-Unity}"
  else
    unity_bin="$(find_unity_bin)"
    [[ -n "$unity_bin" ]] || fail "Unity executable not found; set UNITY_BIN or pass --no-export"
  fi

  local editor_dir="$PROJECT_ROOT/Assets/Editor"
  CREATED_EDITOR_DIR=0
  if [[ ! -d "$editor_dir" ]]; then
    run mkdir -p "$editor_dir"
    CREATED_EDITOR_DIR=1
  fi

  EXPORTER_FILE="$editor_dir/ExportFunplayUnityPackage.cs"
  trap cleanup_exporter EXIT

  local escaped_output
  escaped_output="$(printf '%s' "$PACKAGE_OUTPUT" | sed 's/\\/\\\\/g; s/"/\\"/g')"

  info "Creating temporary Unity export method"
  if [[ "$DRY_RUN" == 0 ]]; then
    cat > "$EXPORTER_FILE" <<EOF
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ExportFunplayUnityPackage
{
    public static void Export()
    {
        const string assetPath = "Assets/unity-mcp";
        const string outputPath = "$escaped_output";

        if (!AssetDatabase.IsValidFolder(assetPath))
        {
            Debug.LogError("Asset path not found: " + assetPath);
            EditorApplication.Exit(1);
            return;
        }

        var assetPaths = AssetDatabase.GetAllAssetPaths()
            .Where(IsAllowedPackagePath)
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (assetPaths.Length == 0)
        {
            Debug.LogError("No exportable assets found under " + assetPath);
            EditorApplication.Exit(1);
            return;
        }

        AssetDatabase.ExportPackage(assetPaths, outputPath, ExportPackageOptions.Default);
        Debug.Log("Exported unitypackage to " + outputPath);
    }

    private static bool IsAllowedPackagePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = path.Replace('\\\\', '/').TrimEnd('/');
        if (normalized != "Assets/unity-mcp" &&
            !normalized.StartsWith("Assets/unity-mcp/", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileName(normalized);
        if (string.Equals(fileName, ".DS_Store", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, ".DS_Store.meta", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(fileName, "CLAUDE.md", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "CLAUDE.md.meta", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".mcpregistry_", StringComparison.OrdinalIgnoreCase))
            return false;

        if (ContainsPathSegment(normalized, ".git") ||
            ContainsPathSegment(normalized, ".idea") ||
            ContainsPathSegment(normalized, ".claude") ||
            ContainsPathSegment(normalized, ".codex"))
            return false;

        if (ContainsPathSegment(normalized, ".github"))
            return false;

        if (ContainsPathSegment(normalized, "Tests"))
            return false;

        return !ContainsPathSegment(normalized, "ProjectSettings") &&
               !ContainsPathSegment(normalized, "Packages") &&
               !ContainsPathSegment(normalized, "Library");
    }

    private static bool ContainsPathSegment(string path, string segment)
    {
        return path.Split('/').Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }
}
EOF
  fi

  info "Exporting unitypackage"
  run_unity_export "$unity_bin" \
    -batchmode -quit \
    -projectPath "$PROJECT_ROOT" \
    -executeMethod ExportFunplayUnityPackage.Export \
    -logFile "$OUT_DIR/unitypackage-export.log"
  cleanup_exporter
}

validate_unitypackage() {
  [[ -f "$PACKAGE_OUTPUT" ]] || fail "unitypackage not found: $PACKAGE_OUTPUT"
  require_command tar

  info "Validating unitypackage pathnames"
  VALIDATION_TMP="$(mktemp -d)"
  trap '[[ -z "${VALIDATION_TMP:-}" ]] || rm -rf "$VALIDATION_TMP"; cleanup_exporter' EXIT

  tar -xzf "$PACKAGE_OUTPUT" -C "$VALIDATION_TMP"
  find "$VALIDATION_TMP" -name pathname -print0 \
    | while IFS= read -r -d '' file; do tr -d '\000' < "$file"; printf '\n'; done \
    | sort > "$OUT_DIR/unitypackage-pathnames.txt"

  local bad_paths
  bad_paths="$(awk '$0 !~ /^Assets\/unity-mcp(\/|$)/ {print}' "$OUT_DIR/unitypackage-pathnames.txt" || true)"
  [[ -z "$bad_paths" ]] || fail "unitypackage contains paths outside Assets/unity-mcp:\n$bad_paths"

  local blocked_paths
  blocked_paths="$(rg '(^|/)(ProjectSettings|Packages|Library|Tests|\.git|\.idea|\.claude|\.codex|\.github)(/|$)|(^|/)(CLAUDE\.md|\.DS_Store)(\.meta)?$|(^|/)\.mcpregistry_' "$OUT_DIR/unitypackage-pathnames.txt" || true)"
  [[ -z "$blocked_paths" ]] || fail "unitypackage contains blocked paths:\n$blocked_paths"
}

generate_notes_and_sums() {
  info "Generating release notes and SHA256SUMS"
  {
    if awk -v version="$VERSION" '
      $0 ~ "^## \\[" version "\\]" { found = 1; next }
      found && /^## \[/ { exit }
      found { print }
      END { exit found ? 0 : 1 }
    ' "$ROOT/CHANGELOG.md"; then
      true
    else
      echo "Release v$VERSION"
      echo
      git -C "$ROOT" log --oneline -10
    fi
  } > "$NOTES_FILE"

  (
    cd "$OUT_DIR"
    : > "$SHA_FILE"
    for file in Funplay.UnityMcp.v"$VERSION".unitypackage *.nupkg server.json; do
      [[ -f "$file" ]] || continue
      shasum -a 256 "$file" >> "$SHA_FILE"
    done
  )

  cat > "$MANIFEST_FILE" <<EOF
{
  "version": "$VERSION",
  "gitSha": "$(git -C "$ROOT" rev-parse HEAD)",
  "unityProjectRoot": "$PROJECT_ROOT",
  "unityPackage": "$PACKAGE_OUTPUT",
  "wrapperRoot": "$WRAPPER_ROOT",
  "generatedAtUtc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
}
EOF
}

pack_wrapper() {
  [[ "$USE_WRAPPER" == 1 ]] || { info "Skipping wrapper pack"; return; }
  [[ -n "$WRAPPER_ROOT" && -d "$WRAPPER_ROOT" ]] || { info "Wrapper workspace not found; skipping wrapper pack"; return; }
  require_command dotnet

  info "Packing wrapper"
  run rm -rf "$WRAPPER_ROOT/bin" "$WRAPPER_ROOT/obj" "$WRAPPER_ROOT/nupkg"
  run dotnet pack "$WRAPPER_ROOT/FunseaAI.Unity.Mcp.csproj" -c Release

  local nupkg="$WRAPPER_ROOT/nupkg/funplay.unity.mcp.$VERSION.nupkg"
  [[ -f "$nupkg" ]] || fail "expected nupkg not found: $nupkg"
  run cp "$nupkg" "$OUT_DIR/"
  run cp "$WRAPPER_ROOT/server.json" "$OUT_DIR/server.json"
}

publish_github() {
  [[ "$PUBLISH_GITHUB" == 1 ]] || return 0
  require_command gh

  local assets=("$PACKAGE_OUTPUT")
  local nupkg="$OUT_DIR/funplay.unity.mcp.$VERSION.nupkg"
  [[ -f "$nupkg" ]] && assets+=("$nupkg")
  [[ -f "$SHA_FILE" ]] && assets+=("$SHA_FILE")

  info "Publishing GitHub Release v$VERSION"
  if gh release view "v$VERSION" -R "$REPO" >/dev/null 2>&1; then
    run gh release upload "v$VERSION" "${assets[@]}" -R "$REPO" --clobber
    run gh release edit "v$VERSION" -R "$REPO" --notes-file "$NOTES_FILE"
  else
    run gh release create "v$VERSION" "${assets[@]}" -R "$REPO" --title "v$VERSION" --notes-file "$NOTES_FILE"
  fi
}

publish_nuget() {
  [[ "$PUBLISH_NUGET" == 1 ]] || return 0
  require_command dotnet
  [[ -n "${NUGET_API_KEY:-}" ]] || fail "NUGET_API_KEY is required for --publish-nuget"

  local nupkg="$OUT_DIR/funplay.unity.mcp.$VERSION.nupkg"
  [[ -f "$nupkg" ]] || fail "nupkg not found: $nupkg"

  info "Publishing NuGet package"
  run dotnet nuget push "$nupkg" \
    -k "$NUGET_API_KEY" \
    -s https://api.nuget.org/v3/index.json \
    --skip-duplicate
}

wait_for_nuget_index() {
  [[ "$PUBLISH_REGISTRY" == 1 ]] || return 0
  require_command curl

  info "Waiting for NuGet flat-container index"
  local url="https://api.nuget.org/v3-flatcontainer/funplay.unity.mcp/index.json"
  for _ in {1..30}; do
    if curl -fsSL "$url" | rg -q "\"$VERSION\""; then
      return
    fi
    sleep 20
  done
  fail "NuGet index did not show $VERSION in time"
}

publish_registry() {
  [[ "$PUBLISH_REGISTRY" == 1 ]] || return 0

  local publisher="${MCP_PUBLISHER_BIN:-$PROJECT_ROOT/mcp-release/bin/mcp-publisher}"
  [[ -x "$publisher" ]] || fail "mcp-publisher not found or not executable: $publisher"
  [[ -f "$OUT_DIR/server.json" ]] || fail "server.json not found: $OUT_DIR/server.json"

  info "Publishing MCP Registry entry"
  run "$publisher" publish "$OUT_DIR/server.json"
}

wait_for_openupm_index() {
  [[ "$VERIFY_OPENUPM" == 1 ]] || return 0
  require_command npm

  info "Waiting for OpenUPM package index"
  for _ in {1..30}; do
    if npm view com.gamebooom.unity.mcp --registry https://package.openupm.com versions --json \
        | rg -q "\"$VERSION\""; then
      return
    fi
    sleep 20
  done
  fail "OpenUPM index did not show $VERSION in time"
}

archive_artifacts() {
  info "Release artifacts"
  find "$OUT_DIR" -maxdepth 1 -type f -print | sort
}

main() {
  require_command node
  require_command rg
  require_command shasum
  run mkdir -p "$OUT_DIR"

  bump_versions
  run_git_checks
  run_unity_tests
  export_unitypackage
  if [[ "$DRY_RUN" == 1 ]]; then
    info "Dry run complete; skipped artifact validation, packing, and publishing"
    return
  fi
  validate_unitypackage
  pack_wrapper
  generate_notes_and_sums
  publish_github
  publish_nuget
  wait_for_nuget_index
  publish_registry
  wait_for_openupm_index
  archive_artifacts
}

main "$@"
