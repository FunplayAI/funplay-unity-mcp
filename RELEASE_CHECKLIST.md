# Release Checklist

Use this checklist before publishing a new open-source release of Funplay MCP for Unity.

The local helper `scripts/release.sh <version>` automates the high-risk mechanical steps:
version bumping, Unity EditMode tests, `.unitypackage` export, pathname validation, SHA/manifest generation,
wrapper packing, and opt-in publishing flags for GitHub, NuGet, and the MCP Registry.

## 1. Repository Hygiene

- [ ] `git status` is clean except for the files intended for the release
- [ ] No local junk is present (`.idea/`, `.DS_Store`, temporary exports, local test files)
- [ ] `package.json` version matches the intended release
- [ ] `CHANGELOG.md` includes the release notes for the target version, with explicit links to every included PR and fixed/closed issue (or an explicit "None") and credits for contributors/reporters
- [ ] `README.md`, `README_CN.md`, and `Documentation~/index.md` match the current product behavior

## 2. Unity Smoke Test

- [ ] Test in a clean Unity `2022.3+` project
- [ ] Install the package from the Git URL
- [ ] Open `Funplay > MCP Server`
- [ ] Start the MCP server successfully
- [ ] Confirm the configured local endpoint is reachable from an MCP client
- [ ] If the configured port is already in use, verify the server reports the startup failure clearly
- [ ] Run a read-only tool such as `get_scene_info`
- [ ] Run a scene-changing tool such as `create_game_object`
- [ ] Verify interaction logs appear in the MCP Server window
- [ ] Trigger a script recompile and confirm the MCP server recovers correctly

## 3. MCP Client Verification

- [ ] Verify at least one primary client can connect (`Claude Code`, `Cursor`, `Codex`, etc.)
- [ ] Confirm `tools/list` returns the expected tool set
- [ ] Confirm a tool call succeeds end-to-end from the external client
- [ ] Verify the one-click config output still matches the documented config snippets

## 4. Docs and API Surface

- [ ] Built-in tool names in the README match exported tool names
- [ ] Custom tool examples use `[ToolProvider]`
- [ ] Any newly added tool has a clear `[Description]` and `[ToolParam]` metadata
- [ ] Any user-visible behavior change is documented

## 5. GitHub Release Readiness

- [ ] `.github/workflows/ci.yml` passes
- [ ] PR checklist still reflects the current release process
- [ ] License file is present and correct
- [ ] Repository description/topics are set on GitHub
- [ ] Initial tags or release tags follow the chosen versioning scheme
- [ ] The `.unitypackage` was exported from an explicit filtered asset list, without `IncludeDependencies` or `IncludeLibraryAssets`
- [ ] The `.unitypackage` contains only `Assets/unity-mcp` paths; verify there are no `ProjectSettings/`, `Packages/`, `Library/`, `Tests/`, `CLAUDE.md`, or local token entries before upload

## 6. Publish

- [ ] Commit the release changes with a clear release-oriented message
- [ ] Create and push the release tag
- [ ] Create the GitHub Release notes and verify they preserve the Changelog's PR/issue links and contributor/reporter credits
- [ ] Verify the public repository renders the README and package documentation correctly

## 7. Post-Release

- [ ] Re-test installation from the public Git URL
- [ ] Check the GitHub release page for broken links or formatting issues
- [ ] Announce the release where appropriate
