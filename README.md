# Partas.TypeProvider

> Composite provider which includes edits of [`EasyBuild.FileSystemProvider`'s](https://github.com/easybuild-org/EasyBuild.FileSystemProvider)
> `AbsoluteFileSystem` and `VirtualFileSystem` providers.
>
> All credit to the original authors and contributors.

## Partas.TypeProvider.BuildHelper

A composite type provider for common script tasks that combines
`EasyBuild.FileSystemProvider`'s with a `GitProvider` and `ProjectProvider`.

```fsharp
open Partas.TypeProvider.BuildHelper

type Build = BuildHelperProvider<"..", capabilityFullOverride = true>

// Each provider is shelled from a separate property, to keep tooling suggestions
// as relevant and helpful.
type FileProvider = Build.FileSystem
type GitProvider = Build.Git
type ProjectProvider = Build.Project

// Virtual file system would require a second literal string passed
// to the `BuildHelperProvider` as the configuration.
// type VirtualProvider = Build.VirtualSystem
```

### GitProvider

The git provider provides nested types when there is some guarantee to their existence,
such as branch names (divided between local and remote), remotes, and tags.

Other information can be queried through the type provider, and it will provide the value
at runtime by shelling `git` commands.

### ProjectProvider

The project provider provides nested types to detected project files, with helpers to
scaffold cli commands for common operations routed with those specific projects
as the targets.

Also provides utility extraction of properties, such as `Version`, and provides
the current value within the member documentation as a design time helper.

## Type Parameters

See the xml documentation on the type provider.

---

## Build CLI

Every repository task runs through the `Build` project rather than a script, so
the tasks are typed, debuggable, and discoverable:

```shell
dotnet run --project Build.fsproj -- --help
```

| Command | What it does |
|---------|--------------|
| `build` | Restores and builds the solution |
| `test` | Builds and runs the Expecto suite |
| `format` | Formats every source file with Fantomas (`--dry-format` checks instead) |
| `lint` | Fails if any file needs formatting |
| `publish` | Packs and pushes to NuGet (`--nuget-key`; falls back to the `local` feed) |
| `docs` | Builds the fsdocs site (`--watch` to serve it) |

Global flags: `--quick` skips restores and checks,
`--format` formats before building, `--dry-format` checks formatting before building,
`--skip-tests` skips the suite.

## Layout

```
Build.fsproj              the build CLI
Build/
  Spec.fs                 typed repository paths, CLI options, process wrappers
  Program.fs              the stages and the commands
src/Partas.TypeProvider/      the library
tests/Partas.TypeProvider.Tests/  the Expecto suite
```

### Adding a project

`Spec.fs` addresses the repository through `EasyBuild.FileSystemProvider`, so
paths are checked when the build project compiles. After adding a project,
register it in `Spec.fs`:

```fsharp
module Projects =
    module Directory =
        type Solution = Root.src.``Partas.TypeProvider``
        type NewThing = Root.src.``Partas.NewThing``
```

A typo, or a project renamed without updating the build, then fails at compile
time rather than halfway through a release.

### Adding a step

A step is a stage of a pipeline. A stage that needs a flag binds it in an
`inputs { }` block, which is also what puts the flag into `--help`:

```fsharp
let myStep = inputs {
    let! quick = Options.quick

    return stage "my step" {
        when' (not quick)
        run (fun (_: StageContext) -> dotnet [ "..." ] root)
    }
}
```

Add it to any command's `pipeline { }`. Because the condition lives in the
stage, the command carries no flags of its own — and adding the stage to a
second command registers `--quick` there too.
