/// <summary>
/// Everything the build commands are written against: typed paths into the
/// repository, the CLI option set, and thin process wrappers.
///
/// Program.fs holds the steps and the commands; this file holds the nouns.
/// </summary>
module Spec

open EasyBuild.FileSystemProvider
open Partas.Build
open Fake.Core
open Fake.Core.Context

[<Literal>]
let __REPOSITORY_DIRECTORY__ =
    __SOURCE_DIRECTORY__
  + "/.."

/// <summary>
/// Typed view of the repository on disk. Every path below is checked at
/// compile time, so renaming a project without updating the build breaks the
/// build project rather than failing halfway through a release.
/// </summary>
type Root = AbsoluteFileSystem<__REPOSITORY_DIRECTORY__>

let inline funApply value fn = fn value

[<AutoOpen>]
module DirectoryManagement =
    open Fake.IO.Globbing.Operators

    /// Source files considered by formatting and linting.
    let sourceFiles =
        !! "**/*.fs"
     -- "**/obj/**/*.*"
     -- "**/AssemblyInfo.fs"

    module Projects =
        module Directory =
            type DesignTime = Root.src.``Partas.TypeProvider``
            type Runtime = Root.src.``Partas.TypeProvider.Runtime``

        module FsProj =
            [<Literal>]
            let DesignTime = Directory.DesignTime.``Partas.TypeProvider.fsproj``
            [<Literal>]
            let Runtime = Directory.Runtime.``Partas.TypeProvider.Runtime.fsproj``

    module Tests =
        module Directory =
            type BuildHelper = Root.tests.``Partas.TypeProvider.Tests``

        module FsProj =
            [<Literal>]
            let BuildHelper = Directory.BuildHelper.``Partas.TypeProvider.Tests.fsproj``

    module Solutions =
        [<Literal>]
        let Main = Root.``Partas.TypeProvider.slnx``

[<AutoOpen>]
module GitManagement =
    [<Literal>]
    let githubUsername = "GitHub Action"

    [<Literal>]
    let githubEmail = "41898282+github-actions[bot]@users.noreply.github.com"

    [<Literal>]
    let gitCiPrefix =
        "-c user.name=\""
      + githubUsername
      + "\" -c user.email=\""
      + githubEmail
      + "\""

    [<Literal>]
    let gitCiCommand =
        "git "
      + gitCiPrefix

    let gitCiArgs =
        [ "-c"
          $"user.name=\"{githubUsername}\""
          "-c"
          $"user.email=\"{githubEmail}\"" ]

#nowarn 3391

[<AutoOpen>]
module CliApiManagement =
    module Options =
        let config =
            Input.option<string> "--configuration"
            |> Input.alias "-c"
            |> Input.desc "Build/pack configuration"
            |> Input.def "Release"
            |> Input.arity Arity.ExactlyOne
            |> Input.helpName "Debug|Release"
            |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

        let quick =
            Input.option<bool> "--quick"
            |> Input.alias "-q"
            |> Input.desc "Skips installations, linting, and other checks"

        let format =
            Input.option<bool> "--format"
            |> Input.alias "-f"
            |> Input.desc "Formats the code"

        let dryFormat =
            Input.option<bool> "--dry-format"
            |> Input.desc "Checks style for errors"

        let skipTests =
            Input.option<bool> "--skip-tests"
            |> Input.desc "Skips running tests"

        let watch =
            Input.option<bool> "--watch"
            |> Input.desc "Runs the operation in watch mode."

        module NuGet =
            let key =
                Input.optionMaybe<string> "--nuget-key"
                |> Input.alias "--nuget"
                |> Input.arity Arity.ExactlyOne
                |> Input.desc "NuGet API key"
                |> Input.helpName "APIKEY"

        module GitHub =
            let key =
                Input.optionMaybe<string> "--github-key"
                |> Input.alias "--github"
                |> Input.arity Arity.ExactlyOne
                |> Input.desc "GitHub API key"
                |> Input.helpName "APIKEY"


[<AutoOpen>]
module Utilities =
    let private createProcess exe args dir =
        CreateProcess.fromRawCommand exe args
        |> CreateProcess.withWorkingDirectory dir
        |> CreateProcess.ensureExitCode

    let dotnet args dir =
        createProcess "dotnet" args dir
        |> Proc.run
        |> ignore

    let private gitCi args dir =
        createProcess gitCiCommand args dir
        |> Proc.run
        |> ignore

    module Git =
        open Fake.Tools.Git

        let inline private run command =
            CommandHelper.directRunGitCommandAndFail Root.``.`` command

        let pushTags pass =
            run $"{gitCiPrefix} push --tags origin"
            pass

        let pushBranch branchName pass =
            run $"{gitCiPrefix} push origin {branchName}"
            pass

        let pushBranchAndTags branchName pass =
            pushBranch branchName pass
            |> pushTags

        let branchName () =
            Information.getBranchName Root.``.``

        let pushCurrentBranch pass =
            branchName ()
            |> pushBranch
            |> funApply pass

        let pushCurrentBranchAndTags pass =
            branchName ()
            |> pushBranchAndTags
            |> funApply pass

        let commitFiles msg files =
            files
            |> List.iter (
                   Staging.stageFile Root.``.``
                >> ignore
                   )

            Commit.exec Root.``.`` msg

        let tagBranch tag =
            Branches.tag Root.``.`` tag
