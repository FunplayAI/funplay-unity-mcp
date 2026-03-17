# Contributing to GameBooom MCP For Unity

Thanks for your interest in contributing! Here's how to get started.

## Development Setup

1. Create or open a clean Unity `2022.3+` test project
2. Add this repository to the project as a local package or Git package
3. Open the project in Unity Editor and wait for compilation to finish
4. Open **GameBooom → MCP Server**
5. Start the server and confirm it is reachable at `http://localhost:8765/`

## Code Style

- C# with 4-space indentation (see `.editorconfig`)
- All classes are `internal` (editor-only plugin)
- Root namespace: `GameBooom.Editor`
- Comments in Chinese or English are both fine

## Adding a New Tool

1. Create or edit a class in `Editor/Tools/Builtins/`
2. Annotate the class with `[ToolProvider("CategoryName")]`
3. Add `[Description("...")]` to the method
4. Use `[ToolParam("description")]` on parameters
5. Method signature: `public static string MethodName(...)`
6. Use `Undo.*` APIs for scene-modifying tools

See [README.md](README.md#adding-custom-tools) for a full example.

## Validation

Before submitting a PR, please verify the change in a Unity test project:

1. Open **GameBooom → MCP Server**
2. Confirm the MCP server starts successfully
3. Run at least one read-only workflow such as `get_scene_info`
4. If your change affects scene editing, run at least one write workflow such as `create_game_object`
5. If your change affects scripts or recompilation, trigger a compile and confirm the server recovers correctly

## Repository Hygiene

- Do not commit local files such as `.idea/`, `.DS_Store`, temporary exports, or scratch files
- Keep Unity `.meta` files in sync with any added, moved, or deleted assets
- Keep PRs focused: one fix, feature, or cleanup per PR

## Documentation

- Update `README.md` for user-visible behavior changes
- Update `README_CN.md` when the English README changes materially
- Update `CHANGELOG.md` for changes that affect users or contributors

## Submitting a PR

1. Fork the repo and create a feature branch
2. Make and test your changes in Unity Editor
3. Run through the validation steps above
4. Submit a PR with a clear description of what changed and why

## License

By contributing, you agree that your contributions will be licensed under GPLv3.
