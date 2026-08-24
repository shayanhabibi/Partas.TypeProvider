# Partas.TypeProvider.BuildHelper

An F# erasing type provider for build scripts and tooling. At compile time it walks your filesystem, reads `.git` directly, and parses your solution file. The resulting types give you IDE completion over real repo structure, and every runtime-evaluated member (SHAs, MSBuild properties) surfaces its **current value as a doc-comment hint while you author** — so hovering over `Git.Head.Sha` shows the actual commit hash, and `Project.MyApp.Version` shows `2.1.0`, without running anything.

Suited to FAKE pipelines, Bullseye scripts, custom build CLIs — anywhere you're wiring up CI/CD in F# and want to catch path and name errors at compile time.

```
dotnet add package Partas.TypeProvider.BuildHelper
```

---

## Setup

```fsharp
open Partas.TypeProvider.BuildHelper

// rootPath is relative to the consuming .fsproj, not cwd.
type Root = BuildHelperProvider<"..", capabilityFullOverride = true>

type FS      = Root.FileSystem
type Git     = Root.Git
type Project = Root.Project
```

| Parameter                | Default | Notes |
|--------------------------|---------|-------|
| `rootPath`               | —       | Relative to the consuming `.fsproj`; empty string = project directory |
| `virtualPathConfig`      | `""`    | Indented DSL for paths that don't exist at design time |
| `capabilityFileSystem`   | `true`  | |
| `capabilityGit`          | `false` | |
| `capabilityProject`      | `false` | Requires the .NET SDK at runtime |
| `capabilityFullOverride` | `false` | Enables all three above |

---

## FileSystem

Walks the directory tree at compile time. Directories become nested types; files become static `FileInfo` properties. Each directory also exposes `.` (`DirectoryInfo`), `GetInfo()`, and `ToString()`.

```fsharp
let file : FileInfo = FS.src.``MyProject``.``Program.fs``
let dir  : string   = FS.src.ToString()
```

**VirtualFileSystem** generates the same shape from an indented-text DSL — for output paths that don't exist until after the build:

```fsharp
type Root = BuildHelperProvider<"..", virtualPathConfig = """
artifacts/
  nupkg/
  bin/
""">
// Root.VirtualFileSystem.artifacts.nupkg — no disk presence required at compile time
```

---

## Git

Reads `.git` directly at design time — no `git` binary needed during compilation. Branch names, remote names, submodule paths, and the working-tree root are **baked as constants** into the consuming assembly. Volatile values execute `git` at runtime and are cached.

**Every runtime member shows its current value as a tooltip hint while you write.** Hover over `Git.HeadBranch.Name` mid-sentence and your IDE shows `main`; hover over `Git.Head.Sha` and it shows the actual hash. This is the repo's live state surfaced without leaving the editor.

```fsharp
// Constants — zero runtime cost, baked at compile time:
let remote = Git.Remotes.origin.FetchUrl   // e.g. "https://github.com/org/repo.git"
let branch = Git.Branches.main.Name       // "main"
let ref    = Git.Branches.main.RefName    // "refs/heads/main"
let up     = Git.Branches.main.Upstream   // "origin/main"

// Runtime (shells git, with design-time hint in tooltip):
let sha   = Git.Branches.main.Commit      // tooltip: DesignTime Hint: 'a3f1c9...'
let head  = Git.Head.Sha
let dirty = Git.IsDirty()

if Git.HeadBranch.IsAvailable() then
    printfn "on %s" Git.HeadBranch.Name

// Revision<"expr"> — static parameter, each expression is its own type:
type Prev = Root.Git.Branches.main.Revision<"HEAD~1">
let prevSha = Prev.Sha   // tooltip shows the actual SHA at author time
```

`Git.Run(args)` executes an arbitrary read-only `git` command — never prompts, never touches the network, 2-second timeout, returns stdout or `""`.

---

## Project

Discovers projects from the solution file (`.slnx` preferred over `.sln`, directory-walk if absent). Path members are baked as constants; MSBuild properties are evaluated at runtime via `dotnet msbuild -getProperty` and cached per project per process.

**Hovering over `Version` while wiring a publish step shows the actual current version** — you see `2.1.0` in the tooltip before the first `dotnet pack` has run.

```fsharp
// Constants:
let path = Project.``MyProject``.Path
let rel  = Project.``MyProject``.RelativePath

// Runtime — first call ~30s cold (SDK load); cached after:
let ver  = Project.``MyProject``.Version          // tooltip: DesignTime Hint: '2.1.0'
let tfm  = Project.``MyProject``.TargetFramework

// Arbitrary property:
let aot  = Project.``MyProject``.Property("PublishAot")

// Scaffolded command lines — returns args only, nothing executes:
let args = Project.``MyProject``.Pack(["--no-build"])
// → ["pack"; "/path/to/MyProject.fsproj"; "--no-build"]

// Clear the per-process cache if a build changes the project mid-run:
Project.``MyProject``.Invalidate()
```

Built-in property members: `Version`, `TargetFramework`, `TargetFrameworks`, `AssemblyName`, `RootNamespace`, `OutputType`, `PackageId`, `IsPackable`.

---

## Cautions

**Everything is a compile-time snapshot.** New branches, files, and projects don't appear until you rebuild the consuming project.

**Property hints skip condition evaluation.** Hints are extracted from raw project XML. A property inside `<Condition="'$(Configuration)'=='Release'">` may show the wrong value. The runtime result from `dotnet msbuild` is always authoritative.

**`Version` hint `1.0.0` is ambiguous.** That is also the MSBuild default, so it may mean the property is undeclared rather than set explicitly. The tooltip calls this out.

**`capabilityProject` requires the SDK at runtime.** Without it, every property returns `""`. Check `Project.IsDotnetAvailable()` when deployment context is uncertain.

**Annotated tag `.Commit` is the tag object, not the commit.** Use `Git.Run("rev-parse v1.0.0^{}")` to peel it.

**Detached or unborn HEAD.** `HeadBranch.*` and `Head.*` expose `IsAvailable()` — check it before reading commit data.

**Ref names with backticks are silently dropped.** They cannot be expressed as F# identifiers and are excluded at design time with no diagnostic.

---

## Build CLI

```bash
dotnet run --project Build.fsproj -- build
dotnet run --project Build.fsproj -- test
dotnet run --project Build.fsproj -- pack
dotnet run --project Build.fsproj -- publish --nuget-key KEY
dotnet run --project Build.fsproj -- publish local
```

---

## Acknowledgements

The `FileSystem` and `VirtualFileSystem` providers are based on [EasyBuild.FileSystemProvider](https://github.com/easybuild-org/EasyBuild.FileSystemProvider).
