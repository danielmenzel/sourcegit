# Instructions for GitHub Copilot

## Project Overview

SourceGit is a cross-platform Git GUI client built with **Avalonia UI** targeting **.NET 10.0**. The application follows an **MVVM architecture** with compiled bindings enabled by default.

## Architecture

### Core Layers
- **Commands/** - Git command execution layer. All git operations inherit from `Command.cs` which handles process execution and output capture
- **Models/** - Data models and business logic, interfaces like `ICommandLog` for command output handling
- **ViewModels/** - MVVM view models that connect Views to Commands/Models
- **Views/** - Avalonia XAML UI components with compiled bindings
- **Native/** - Platform-specific implementations (OS integration via `Native.OS`)

### Key Patterns
- Git operations are async and return `Command.Result` with `IsSuccess`, `StdOut`, and `StdErr`
- Commands support cancellation tokens and logging via `Models.ICommandLog`
- Views use Avalonia's compiled bindings: `<PropertyGroup><AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault></PropertyGroup>`
- App configuration stored in platform-specific paths: `%APPDATA%\SourceGit` (Windows), `~/.config/SourceGit` (Linux), `~/Library/Application Support/SourceGit` (macOS)

### Naming Conventions (from .editorconfig)
- Private static fields: `s_camelCase`
- Private/internal fields: `_camelCase`
- Constants: `PascalCase`
- Use `var` for built-in types and when type is apparent

## Development Workflow

### Build and Run
- Build: Run task `build` or `dotnet build src/SourceGit.csproj`
- Publish: Run task `publish` for release builds with AOT compilation
- Watch mode: Run task `watch` for hot-reload development

### Release Configuration
- Release builds use **PublishAot** and **PublishTrimmed** (unless `DisableAOT=true`)
- Version read from `VERSION` file in root directory
- Package scripts in `build/scripts/`: `package.win.ps1`, `package.linux.sh`, `package.osx-app.sh`

### Testing
- No formal unit test framework - test manually via watch mode
- Localization status tracked in [TRANSLATION.md](TRANSLATION.md)

## Code Style

### C# Conventions
- New line before all opening braces (Allman style)
- Prefer expression-bodied members for properties, indexers, and accessors
- Use pattern matching over `is`/`as` with null checks
- Never use `var` for primitive types - be explicit or use `var` when type is obvious
- Use language keywords (`string`, `int`) over BCL types (`String`, `Int32`)
- Sort using directives with `System.*` first

### File Organization
- Group related functionality: Commands in `Commands/`, UI in `Views/`, business logic in `Models/`
- ViewModels mirror View structure
- Partial classes split across files: `App.axaml.cs`, `App.Commands.cs`, `App.Extensions.cs`, `App.JsonCodeGen.cs`

## Special Features

### Supported Git Features
- Full Git workflow: clone, fetch, pull, push, merge, rebase, cherry-pick
- Interactive rebase, submodules, worktrees, Git LFS, bisect
- AI-powered commit message generation (C# port of commitollama)
- GitFlow integration, custom actions, workspace management

### Cross-Platform Support
- Targets Windows, macOS (13.0+), Linux (Debian 12 tested on X11/Wayland)
- Uses Avalonia for cross-platform UI rendering
- Requires Git >=2.25.1 (MSYS Git not supported on Windows)

## Common Tasks

### Adding a New Git Command
1. Create class in `Commands/` inheriting from `Command`
2. Set `Args` property with git command arguments
3. Call `ExecAsync()` for async execution with logging or `ReadToEndAsync()` for synchronous output
4. Handle `Command.Result` with success/error states

### Adding a View
1. Create `.axaml` and `.axaml.cs` in `Views/`
2. Create corresponding ViewModel in `ViewModels/`
3. Use `x:DataType` in XAML for compiled bindings
4. Follow Avalonia's MVVM patterns with ReactiveUI if needed

### Localization
- Just use English, no need for translations yet
- Resource strings in `Resources/Locales/*.axaml`
- Use `Text.*` keys in XAML bindings
- Track missing translations in [TRANSLATION.md](TRANSLATION.md)

## Dependencies

Key packages:
- **Avalonia 11.3.9** - UI framework
- **Avalonia.Desktop** - Desktop platform support
- **Avalonia.Themes.Fluent** - Modern theme system

## Important Files

- [src/SourceGit.csproj](src/SourceGit.csproj) - Project configuration
- [src/Commands/Command.cs](src/Commands/Command.cs) - Base command execution
- [src/App.axaml.cs](src/App.axaml.cs) - Application entry point
- [.editorconfig](.editorconfig) - Code style rules
- [VERSION](VERSION) - Current version string

## Comparison to GitExtensions

 - Look in the gitextensions repository in the current workspace for inspiration on how to implement features in SourceGit.
