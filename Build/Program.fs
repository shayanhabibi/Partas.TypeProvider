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

open Fake.DotNet
open Spec
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open Partas.Build
open Partas.Build.Internal

// disable warning of implicit conversion of ops to string
#nowarn 3391

let private root = Root.``.``

/// Release notes drive the assembly and package version.
let release = lazy ReleaseNotes.load "docs/RELEASE_NOTES.md"

let private getConfig: string -> DotNet.BuildConfiguration =
    function
    | "Debug" -> DotNet.BuildConfiguration.Debug
    | _ -> DotNet.BuildConfiguration.Release

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

            return stage "restore" {
                when' (not quick)
                run (fun (_: StageContext) ->
                    DotNet.restore
                        (fun p -> { p with DotNet.RestoreOptions.MSBuildParams.DisableInternalBinLog = true }) Solutions.Main)
            }
        }

module HouseKeeping =
    let clean =
        input {
            let! quick = Options.quick

            return stage "clean" {
                when' (not quick)

                run (fun (_: StageContext) ->
                    !! "**/**/bin"
                    ++ "temp"
                    -- "bin"
                    |> Shell.cleanDirs)
            }
        }

    let private formatImpl () =
        sourceFiles
        |> Seq.map (sprintf "\"%s\"")
        |> String.concat " "
        |> DotNet.exec id "fantomas"
        |> function
        | result when result.OK -> ()
        | result -> Trace.log $"Errors while formatting all files: %A{result.Messages}"

    let private formatCheckImpl () =
        sourceFiles
        |> Seq.map (sprintf "\"%s\"")
        |> String.concat " "
        |> sprintf "%s --check"
        |> DotNet.exec id "fantomas"
        |> function
        | result when result.OK -> ()
        | result when result.ExitCode = 99 -> failwith "Some files need formatting"
        | result -> failwith $"Errors while checking formatting of all files: %A{result.Messages}"

    /// The `format` command: formats, or checks only under --dry-format.
    let formatCommand =
        input {
            let! dryFormat = Options.dryFormat
            return stage "format" { run (fun _ -> if dryFormat then formatCheckImpl () else formatImpl ()) }
        }

    /// Opt-in formatting inside another command, via --format.
    let format =
        input {
            let! format = Options.format

            return stage "format" {
                when' format
                run (fun _ -> formatImpl ())
            }
        }

    /// The `lint` command: always checks.
    let dryFormatCommand =
        stage "lint" { run (fun _ -> formatCheckImpl ()) }

    /// Opt-in format checking inside another command, via --dry-format.
    /// Suppressed when --format was passed, which has already fixed the files.
    let dryFormat =
        input {
            let! format = Options.format
            and! dryFormat = Options.dryFormat

            return stage "lint" {
                when' (
                    dryFormat
                 && not format
                    )

                run (fun _ -> formatCheckImpl ())
            }
        }

module ProjectManagement =
    let build =
        input {
            let! config = Options.config

            return stage "build" {
                run (fun _ ->
                    let config = getConfig config

                    [ Projects.FsProj.Solution ]
                    |> List.iter (
                           DotNet.build (fun p ->
                               { p with
                                     Configuration = config
                                     DotNet.BuildOptions.MSBuildParams.Properties =
                                         [ "PackageVersion", release.Value.AssemblyVersion
                                           "Version", release.Value.AssemblyVersion ]
                                     // Keep an unconditional field last: the template engine deletes
                                     // the marker lines above, and a conditional block at the edge of
                                     // a bracket changes what Fantomas formats the result to.
                                     DotNet.BuildOptions.MSBuildParams.DisableInternalBinLog = true })
                           ))
            }
        }

    let pack =
        stage "pack" {
            run (fun _ ->
                [ Projects.FsProj.Solution ]
                |> List.iter (
                       DotNet.pack (fun p ->
                           { p with
                                 NoRestore = true
                                 OutputPath = Some "bin"
                                 DotNet.PackOptions.MSBuildParams.DisableInternalBinLog = true
                                 DotNet.PackOptions.MSBuildParams.Properties =
                                     [ "PackageVersion", release.Value.AssemblyVersion
                                       "Version", release.Value.AssemblyVersion ] })
                       ))
        }

    let publish =
        input {
            let! apiKey = Options.NuGet.key

            let inline publishToSourceWithKey apiKey source =
                !! "bin/*.nupkg"
                |> Seq.iter (
                       DotNet.nugetPush (fun p ->
                           { p with
                                 DotNet.NuGetPushOptions.PushParams.ApiKey = apiKey
                                 DotNet.NuGetPushOptions.PushParams.Source = Some source
                                 DotNet.NuGetPushOptions.Common.CustomParams = Some "--skip-duplicate" })
                       )

            return stage "publish" {
                echo (
                    match apiKey with
                    | Some _ -> "Publishing to nuget.org."
                    | None -> "No NuGet API key provided. Publishing to local feed if it exists."
                    )

                run (fun (_: StageContext) ->
                    match apiKey with
                    | None ->
                        "local"
                        |> publishToSourceWithKey None
                    | Some _ ->
                        "https://api.nuget.org/v3/index.json"
                        |> publishToSourceWithKey apiKey)
            }
        }

module Tests =
    let build =
        input {
            let! skipTests = Options.skipTests
            and! config = Options.config

            return
                stage "build tests" {
                    when' (not skipTests)

                    run (fun _ ->
                        let config = getConfig config

                        [ Tests.FsProj.Solution ]
                        |> List.iter (
                               DotNet.build (fun p ->
                                   { p with Configuration = config ; DotNet.BuildOptions.MSBuildParams.DisableInternalBinLog = true })
                               ))
                }
        }

    let execute =
        input {
            let! skipTests = Options.skipTests

            return stage "test" {
                when' (not skipTests)

                run (fun _ ->
                    !! "**/bin/**/*.Tests.dll"
                    |> Testing.Expecto.run (fun p ->
                        { p with
                              Summary = true
                              CustomArgs = "--colours 256" :: p.CustomArgs }))
            }
        }

module Documentation =
    /// Serves under --watch, builds otherwise.
    let generate =
        input {
            let! watch = Options.watch

            return stage "docs" {
                run (fun _ ->
                    if watch then
                        dotnet [ "fsdocs"; "watch"; "--eval" ] root
                    else
                        dotnet [ "fsdocs"; "build"; "--eval"; "--clean" ] root)
            }
        }

module Commands =
    let format =
        command "format" {
            description "Formats all source files"

            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                HouseKeeping.formatCommand
            }
        }

    let lint =
        command "lint" {
            description "Checks formatting of all source files"
            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                HouseKeeping.dryFormatCommand
            }
        }

    let build =
        command "build" {
            description "Builds the solution"

            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                Prelude.restoreSolution
                HouseKeeping.clean
                HouseKeeping.format
                HouseKeeping.dryFormat
                ProjectManagement.build
            }
        }

    let test =
        command "test" {
            description "Builds and runs the test suite"

            // Advertised but not read by any stage below. Kept so the flags do
            // not disappear from `test --help`; wire them up or drop them.
            addInput Options.watch

            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                Prelude.restoreSolution
                HouseKeeping.clean
                HouseKeeping.format
                HouseKeeping.dryFormat
                ProjectManagement.build
                Tests.build
                Tests.execute
            }
        }

    let publish =
        command "publish" {
            description "Packs the solution and pushes it to NuGet"

            // Advertised but not read by any stage below.
            addInput Options.GitHub.key

            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                Prelude.restoreSolution
                HouseKeeping.clean
                HouseKeeping.format
                HouseKeeping.dryFormat
                ProjectManagement.build
                Tests.build
                Tests.execute
                ProjectManagement.pack
                ProjectManagement.publish
            }
        }

    let docs =
        command "docs" {
            description "Builds the documentation, or serves it with --watch"

            Command.pipeline {
                workingDir root
                Prelude.restoreTools
                Prelude.restoreSolution
                Documentation.generate
            }
        }

let mainBuilder argsv =
    rootCommand argsv {
        description "Partas.TypeProvider"

        // One `addCommand` per line rather than one `addCommands [ ... ]`: the
        // template engine deletes the marker lines, and a conditional block at
        // the edge of a list literal changes what Fantomas formats it to.
        addCommand Commands.format
        addCommand Commands.lint
        addCommand Commands.build
        addCommand Commands.test
        addCommand Commands.publish
        addCommand Commands.docs
    }

[<EntryPoint>]
let main argsv =
    mainBuilder argsv
