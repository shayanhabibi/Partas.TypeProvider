/// <summary>
/// The build CLI.
///
/// A step is a stage of a pipeline, and a command is the pipelines it runs.
/// A stage that needs a flag binds it in an <c>inputs { }</c> block, which is
/// what puts the flag into the <c>--help</c> of every command running a
/// pipeline that contains it — nothing below registers an option by hand.
///
/// Because the condition lives in the stage, adding a stage to a command never
/// means threading a flag through the caller.
///
///     dotnet run --project Build.fsproj -- --help
/// </summary>
module Build

open Fake.Core.Context
open Fake.DotNet
open Spec
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open Partas.Build
open Partas.Build.Internal

// disable warning of implicit conversion of ops to string
#nowarn 3391

let execContext = FakeExecutionContext.Create false "build.fsx" []
setExecutionContext (RuntimeContext.Fake execContext)

let private root = Root.``.``

/// Release notes drive the assembly and package version.
let release = lazy ReleaseNotes.load "docs/RELEASE_NOTES.md"

let private getConfig: string -> DotNet.BuildConfiguration =
    function
    | "Debug" -> DotNet.BuildConfiguration.Debug
    | _ -> DotNet.BuildConfiguration.Release

module Stages =
    let restoreTools quick = stage "restore tools" {
        when' (not quick)
        run (fun _ -> dotnet [ "tool"; "restore"; "--verbosity"; "q" ] root)
    }
    let restoreSolution quick = stage "restore solution" {
        when' (not quick)
        run (fun (_: StageContext) ->
            DotNet.restore (fun p -> { p with DotNet.RestoreOptions.MSBuildParams.DisableInternalBinLog = true }) Solutions.Main)
    }

    let format (dry: bool) = stage "format" {
        let sourceFiles = sourceFiles |> Seq.map (sprintf "\"%s\"") |> String.concat " "
        stage "check" {
            when' dry
            run (cmd $"fantomas {sourceFiles} --check")
        }
        stage "execute" {
            when' (not dry)
            run (cmd $"fantomas {sourceFiles}")
        }
    }
    let flaggedFormat = input {
        let! shouldFormat = Options.format
        return stage "format" {
            when' shouldFormat
            format false
        }}

    let private commonMsBuildParams  = fun msBuildParams -> {
        msBuildParams with
            MSBuild.CliArguments.DisableInternalBinLog = true
            MSBuild.CliArguments.Properties = [
                "PackageVersion", release.Value.AssemblyVersion
                "Version", release.Value.AssemblyVersion
            ]
    }
    let build project config = stage $"build-{project}" {
        run (fun _ ->
            project |> DotNet.build (fun p ->
                { p with
                    Configuration = config
                    MSBuildParams = commonMsBuildParams p.MSBuildParams })
            )
    }

    let pack project output = stage $"pack-{project}" {
        run (fun _ ->
            project |> DotNet.pack (fun p ->
                {
                    p with
                        NoRestore = true
                        OutputPath = Some output
                        MSBuildParams = commonMsBuildParams p.MSBuildParams
                }
                )
            )
    }

    let push source nupkg apiKey = stage $"publish-{nupkg}" {
        echo $"Publishing {nupkg} to {source}."
        run (fun _ ->
            nupkg
            |> DotNet.nugetPush  (fun p ->
                {
                    p with
                        PushParams.ApiKey = apiKey
                        PushParams.Source = Some source
                        Common.CustomParams = Some "--skip-duplicate"
                }
                )
            )
    }

    let publishLocal nupkg = push "local" nupkg None
    let publishNuget nupkg apiKey = push "https://api.nuget.org/v3/index.json" nupkg (Some apiKey)

    let expecto path = stage "expecto" {
        run (fun _ ->
            path |> Testing.Expecto.run (fun p ->
                { p with CustomArgs = "--colours 256" :: p.CustomArgs }
                )
            )
    }

    let buildDocs watchMode = stage "docs" {
        run (fun ctx ->
            let wdir = StageContext.getWorkingDir ctx |> ValueOption.defaultValue root
            if watchMode
            then dotnet [ "fsdocs"; "watch"; "--eval" ] wdir
            else dotnet [ "fsdocs"; "build"; "--eval"; "--clean" ] wdir
            )
    }



/// <summary>Stages every command opens with. All are skipped by <c>--quick</c>.</summary>
module Prelude =
    let restoreTools =
        input {
            let! quick = Options.quick
            return stage "restore tools" {
                when' (not quick)
                run (fun _ -> dotnet [ "tool"; "restore"; "--verbosity"; "q" ] root)
            }
        }

    let restoreSolution =
        input {
            let! quick = Options.quick
            return Stages.restoreSolution quick
        }

module Pipelines =
    let setup = pipeline "setup" {
        input {
            let! quick = Options.quick
            return stage "setup" {
                parallel'
                when' (not quick)
                Stages.restoreSolution true
                Stages.restoreTools true
                run (fun _ ->
                    !! "**/**/bin"
                    ++ "temp"
                    -- "bin"
                    |> Shell.cleanDirs
                    )
            }
        }
    }

    let executeTests = pipeline "test" {
        input {
            let! skipTests = Options.skipTests
            and! config = Options.config

            let config = getConfig config

            return stage "" {
                let tests = [ Tests.FsProj.BuildHelper ]
                for test in tests do
                    Stages.build test config
                let glob = !! "**/bin/**/*.Tests.dll"
                Stages.expecto glob
            }
        }
    }

    let build = input {
        let! config = Options.config
        let config = getConfig config
        return pipeline "build" {
            let projects = [ Projects.FsProj.Runtime ]
            stage "parallelise" {
                parallel'
                for project in projects do Stages.build project config
            }
        }
    }

    let pack = pipeline "pack" {
        let project = Projects.FsProj.Runtime
        let config = DotNet.BuildConfiguration.Release
        Stages.build project config
        Stages.pack project "bin"
    }

    let push = input {
        let! apiKey = Options.NuGet.key
        return pipeline "publish" {
            stage "push-local" {
                when' apiKey.IsNone
                for nupkg in !! "bin/*.nupkg" do
                    Stages.publishLocal nupkg
            }
            stage "push-nuget" {
                when' apiKey.IsSome
                for nupkg in !! "bin/*.nupkg" do
                    Stages.publishNuget nupkg apiKey.Value
            }

        }
    }

module Commands =
    let format = command "format" {
        description "Formats all source files"
        Pipelines.setup
        Command.pipeline {
            input {
                let! dryFormat = Options.dryFormat
                return Stages.format dryFormat
            }
        }
    }

    let build = command "build" {
        description "Builds the solution"
        Pipelines.setup
        Command.pipeline { Stages.flaggedFormat }
        Pipelines.build
    }

    let pack = command "pack" {
        description "Packs the solution"
        Pipelines.setup
        Command.pipeline { Stages.flaggedFormat }
        Pipelines.build
        Pipelines.executeTests
        Pipelines.pack
    }

    let publish = command "publish" {
        description "Publishes the solution to a local or remote feed"
        Pipelines.setup
        Command.pipeline { Stages.flaggedFormat }
        Pipelines.build
        Pipelines.executeTests
        Pipelines.pack
        Pipelines.push
    }

    let test = command "test" {
        description "Builds and runs the test suite"
        Pipelines.setup
        Command.pipeline { Stages.flaggedFormat }
        Pipelines.executeTests
    }


let mainBuilder argsv =
    rootCommand argsv {
        description "Partas.TypeProvider"
        // One `addCommand` per line rather than one `addCommands [ ... ]`: the
        // template engine deletes the marker lines, and a conditional block at
        // the edge of a list literal changes what Fantomas formats it to.
        addCommand Commands.format
        addCommand Commands.test
        addCommand Commands.build
        addCommand Commands.pack
        addCommand Commands.publish
    }

[<EntryPoint>]
let main argsv =
    mainBuilder argsv
