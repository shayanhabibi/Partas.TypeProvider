#r "nuget: Partas.TypeProvider.BuildHelper"
#r "nuget: Fake.IO.FileSystem"
#r "nuget: Partas.Build"

open Partas.Build
open Partas.Build.Internal
open Fake.IO.Globbing.Operators
open Fake.IO
open Partas.TypeProvider.BuildHelper

type Repo = BuildHelperProvider<__SOURCE_DIRECTORY__, "bin/", capabilityFullOverride=true>
let inline funApply value fn = fn value

let sourceFiles =
    !! "**/*.fs"
    -- "**/obj/**/*.*"
    -- "**/AssemblyInfo.fs"

let sln = Repo.Project.SolutionFile

let projects = [
    "build-helper", Repo.Project.``Partas.TypeProvider.Runtime``.Path
]
let projectMap = Map.ofList projects

module Options =
    let project =
        Input.option<string list> "--project"
        |> Input.alias "-p"
        |> Input.desc "Target project"
        |> Input.def [ Repo.Project.``Partas.TypeProvider.Runtime``.Path ]
        |> Input.allowMultipleArgumentsPerToken
        |> Input.acceptOnlyFromAmong (projects |> List.map fst)
        |> Input.customParser (fun res ->
            match Seq.toList res.Tokens with
            | [] -> []
            | projects -> projects |> List.map (fun p -> Map.find p.Value projectMap)
            )
        |> Input.arity Arity.OneOrMore
    let config =
        InputSpec.ofInput Baked.Input.DotNet.configString
        |> InputSpec.map (Option.defaultValue "Release")
    let quick =
        Input.option<bool> "--quick"
        |> Input.alias "-q"
        |> Input.desc "Skips installations, linting, and other checks"
    let skipTests =
        Input.option<bool> "--skip-tests"
        |> Input.desc "Skips running tests"
    let watch =
        Input.option<bool> "--watch"
        |> Input.desc "Runs the operation in watch mode."
    let dry =
        Input.option<bool> "--dry-run"
        |> Input.alias "--dry"
        |> Input.desc "Runs the operation in dry-run mode."

let restore = input {
    let! quick = Options.quick
    return stage "restore" {
        when' (not quick)
        quiet
        parallel' 2
        run "dotnet tool restore --verbosity q"
        run (cmd $"dotnet restore {sln} --verbosity q")
    }
}

let clean = input {
    let! quick = Options.quick
    return stage "clean" {
        when' (not quick)
        run (fun _ ->
            !! "**/**/bin"
            ++ "temp"
            -- "bin"
            |> Shell.cleanDirs
            )
    }
}
let format = input {
    let! dry = Options.dry
    let sourceFiles = sourceFiles |> Seq.map (sprintf "\"%s\"") |> String.concat " "
    return stage "format" {
        stage "check" {
            when' dry
            run (cmd $"dotnet fantomas {sourceFiles} --check")
        }
        stage "execute" {
            when' (not dry)
            run (cmd $"dotnet fantomas {sourceFiles}")
        }
    }
}
let build (project: InputSpec<string>) = input {
    let! project = project
    and! config = Options.config
    return stage $"build-{project}" {
        quiet
        run (cmd $"dotnet build {project} -c {config} --verbosity q ")
    }
}
let pack (project: InputSpec<string>) = input {
    let! project = project
    return stage "pack" {
        run (cmd $"dotnet pack {project} --no-restore -o {Repo.VirtualFileSystem.bin.``.``}")
    }
}
let publish (project: InputSpec<string>) = input {
    let! key = Baked.Input.NuGet.apiKeyOrEnv
    and! project = project
    return stage "publish" {
        when' key.IsSome
        failIfIgnored
        runSensitive
            $"dotnet nuget push {project} -s https://api.nuget.org/v3/index.json --api-key {key.Value} --skip-duplicate"
    }
}
let runTests = input {
    let! skipTests = Options.skipTests
    and! config = Options.config
    and! ci = Baked.Input.CI.isCI
    return stage "test" {
        when' (not skipTests)
        run (Cmd.ofList "dotnet" (
            Repo.Project.``Partas.TypeProvider.Tests``.Run[
                "-c"; config
                if ci then "--summary"
                "--colours"; "256"
            ])
        )
    }
}

let docs = input {
    let! watch = Options.watch
    return stage "docs" {
        stage "build" {
            when' (not watch)
            run "dotnet fsdocs build --eval --clean"
        }
        stage "watch" {
            when' watch
            run "dotnet fsdocs watch --eval"
        }
    }
}

rootCommand (Array.tail fsi.CommandLineArgs) {
    description "Partas.TypeProvider"
    command "format" {
        description "Formats all source files"
        restore
        clean
        format
    }
    command "build" {
        description "Builds the solution"
        restore
        clean
        InputSpec.ret Repo.Project.``Partas.TypeProvider.Runtime``.Path
        |> build
    }
    command "publish" {
        description "Publishes to NuGet"
        let path = Repo.Project.``Partas.TypeProvider.Runtime``.Path |> InputSpec.ret
        restore
        clean
        build path
        runTests
        pack path
        Path.combine (Repo.VirtualFileSystem.bin.ToString()) "*.nupkg"
        |> InputSpec.ret
        |> publish
    }
    command "test" {
        description "Runs the test suite"
        restore
        clean
        runTests
    }
    command "docs" {
        description "Builds the documentation"
        restore
        clean
        docs
    }
    command "bump" {
        description "Bumps the version"
        Baked.Pipelines.bumpArgument (projects |> List.map snd) Options.project
    }
}
