# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`Partas.TypeProvider.BuildHelper` — an F# type provider for build scripts that exposes filesystem, git, and MSBuild project metadata as static types at compile time.

## Build Commands

All build operations run through the `Build.fsproj` CLI:

```bash
dotnet run --project Build.fsproj -- build         # Restore + build
dotnet run --project Build.fsproj -- test          # Build + run tests
dotnet run --project Build.fsproj -- format        # Format with Fantomas
dotnet run --project Build.fsproj -- pack          # Build + test + pack to bin/
dotnet run --project Build.fsproj -- publish --nuget-key KEY  # Pack + push NuGet
dotnet run --project Build.fsproj -- publish local # Pack + push to local feed
```

Global flags: `--quick` (skip restore/checks), `-c Debug` (default Release), `--skip-tests`, `--dry-format`.

Direct dotnet commands also work:
```bash
dotnet build Partas.TypeProvider.slnx
dotnet test tests/Partas.TypeProvider.Tests/Partas.TypeProvider.Tests.fsproj
```

Version is parsed from `docs/RELEASE_NOTES.md` via `Fake.Core.ReleaseNotes`.

## Architecture: Two-Assembly Type Provider Pattern

The standard F# type provider split:

- **Design-time** (`src/Partas.TypeProvider/`, `netstandard2.0`): Contains all `ProvidedTypeDefinition` logic. Compiled with `IsFSharpDesignTimeProvider=true` and `DefineConstants=IS_DESIGNTIME`. Directly `<Compile Include>` links the Runtime `.fs` source files so they are available to provider logic without a project reference.
- **Runtime** (`src/Partas.TypeProvider.Runtime/`, `net8.0;netstandard2.0`): The NuGet deliverable. Each `.fs` file ends with `[<assembly: TypeProviderAssembly "Partas.TypeProvider.BuildHelper.DesignTime">]` to tell F# tooling which DLL has the provider logic.
- The `assemblyReplacementMap` in `TypeProviderForNamespaces` remaps erased types from the design-time assembly name to the runtime assembly name.

## Architecture: Design-time vs Runtime Data

A core invariant of this codebase — understand it before making changes:

- **Gathered at design time (no process spawning):** directory trees, `.git` ref files (loose + `packed-refs`), git config parsing, `.slnx`/`.sln` XML parsing, `Directory.Build.props` chain reading. This is what lets the types appear in IDE intellisense instantly.
- **Deferred to consuming program runtime (quotation expressions):** git SHAs, dirty state, MSBuild property values. These are embedded as `<@@ Git.Runtime.xxx @@>` or `<@@ Project.Runtime.xxx @@>` getter quotations so they execute in the consuming assembly, not the compiler.

All provided sub-types use `AddMemberDelayed` so the compiler only expands tree branches actually referenced — critical for large repos.

## Key Source Files

| File | Role |
|---|---|
| `src/Partas.TypeProvider/Library.fs` | The `[<TypeProvider>]` class; `makeFileProvider`, `makeGitProvider`, `makeProjectProvider` |
| `src/Partas.TypeProvider/VirtualDirParser.fs` | Parses the virtual path config DSL into an `INode` tree |
| `src/Partas.TypeProvider.Runtime/Git.fs` | Design-time `.git` filesystem reader + `Git.Runtime` module (shells `git`) |
| `src/Partas.TypeProvider.Runtime/Project.fs` | Design-time `.slnx`/`.sln` parser + `Project.Runtime` module (shells `dotnet msbuild`) |
| `src/Partas.TypeProvider.Runtime/Proc.fs` | Safe process runner: concurrent stdout/stderr drain, timeout, no stdin |

## Static Parameters

`BuildHelperProvider` has six static parameters:
- `rootPath` — path to the root of the repository
- `virtualPathConfig` — indented text DSL for virtual file paths
- `capabilityGit` (default `false`) — expose `Root.Git.*`
- `capabilityFileSystem` (default `true`) — expose `Root.FileSystem.*`
- `capabilityProject` (default `false`) — expose `Root.Project.*`
- `capabilityFullOverride` (default `false`) — override all capabilities to true

`Revision` is a nested type inside each git branch type with its own string static parameter (a git revision expression like `"HEAD"` or `"HEAD~1"`), generating per-expression provided types with SHA/date/author/message members.

## Tests

- **Framework:** Expecto with `YoloDev.Expecto.TestSdk` adapter
- `tests/Partas.TypeProvider.Tests/Tests.fs` — Git layer (config parsing, ref resolution, live provider against this repo)
- `tests/Partas.TypeProvider.Tests/ProjectTests.fs` — Project layer (`.slnx`/`.sln` parsing, MSBuild evaluation)
- Tests create real temp directories for fixture isolation
- Run a single test by name: `dotnet test --filter "test name substring"`

## Coding Conventions

- Fantomas formatting: Stroustrup braces, space before uppercase invocations, max line 150
- `.editorconfig` enforces CRLF, 4-space indent
- `constString`/`constBool` helpers in Library.fs for properties baked as constants into consuming assemblies
- Runtime caching uses `ConcurrentDictionary` in both `Git.Runtime` and `Project.Runtime`
