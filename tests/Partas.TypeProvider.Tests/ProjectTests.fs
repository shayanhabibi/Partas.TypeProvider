module Partas.TypeProvider.Tests.ProjectTests

open System.IO
open Expecto
open Partas.TypeProvider.BuildHelper
open Partas.TypeProvider.Runtime

/// The project provider is opt-in, so it needs its own instantiation. Project
/// discovery is scoped to the root rather than walking up to find a solution
/// the way GitProvider walks up to find a repository, so this points at the
/// repository root explicitly instead of defaulting to the test project.
type Repo = BuildHelperProvider<"../..", capabilityFullOverride=true>

let private write (root: string) (relative: string) (contents: string) =
    let path = Path.Combine (root, relative.Replace ('/', Path.DirectorySeparatorChar))

    Directory.CreateDirectory (Path.GetDirectoryName path)
    |> ignore

    File.WriteAllText (path, contents)
    path

let private withTemp f =
    let root =
        Path.Combine (
            Path.GetTempPath (),
            "partas-proj-"
            + Path.GetRandomFileName ()
        )

    Directory.CreateDirectory root
    |> ignore

    try
        f root
    finally
        try
            Directory.Delete (root, true)
        with _ ->
            ()

let private emptyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"

[<Tests>]
let discoveryTests =
    testList
        "Project.discover"
        [ test "reads projects out of an .slnx, including nested folders" {
              withTemp (fun root ->
                  write root "Build.fsproj" emptyProject
                  |> ignore

                  write root "src/Lib/Lib.fsproj" emptyProject
                  |> ignore

                  write
                      root
                      "Repo.slnx"
                      "<Solution>\n  <Project Path=\"Build.fsproj\" />\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Lib/Lib.fsproj\" />\n  </Folder>\n</Solution>"
                  |> ignore

                  let found = Project.discover root

                  Expect.equal
                      (found
                       |> List.map (fun p -> p.Name))
                      [ "Build"; "Lib" ]
                      "both projects, nesting flattened"

                  Expect.equal
                      (found
                       |> List.map (fun p -> p.RelativePath))
                      [ "Build.fsproj"; "src/Lib/Lib.fsproj" ]
                      "forward-slashed relative paths")
          }

          test "reads projects out of a classic .sln and ignores solution folders" {
              withTemp (fun root ->
                  write root "src/Lib/Lib.csproj" emptyProject
                  |> ignore

                  write
                      root
                      "Repo.sln"
                      ("Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"src\\Lib\\Lib.csproj\", \"{A}\"\nEndProject\n"
                       + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"docs\", \"docs\", \"{B}\"\nEndProject\n")
                  |> ignore

                  let found = Project.discover root

                  Expect.equal
                      (found
                       |> List.map (fun p -> p.Name))
                      [ "Lib" ]
                      "the solution folder is not a project")
          }

          test "prefers .slnx when both solution formats are present" {
              withTemp (fun root ->
                  write root "FromSlnx.fsproj" emptyProject
                  |> ignore

                  write root "FromSln.fsproj" emptyProject
                  |> ignore

                  write root "Repo.slnx" "<Solution><Project Path=\"FromSlnx.fsproj\" /></Solution>"
                  |> ignore

                  write root "Repo.sln" "Project(\"{X}\") = \"FromSln\", \"FromSln.fsproj\", \"{A}\"\nEndProject\n"
                  |> ignore

                  Expect.equal
                      (Project.discover root
                       |> List.map (fun p -> p.Name))
                      [ "FromSlnx" ]
                      "slnx wins")
          }

          test "walks the tree when there is no solution, skipping bin and obj" {
              withTemp (fun root ->
                  write root "src/Lib/Lib.fsproj" emptyProject
                  |> ignore

                  write root "src/Lib/obj/Ghost.fsproj" emptyProject
                  |> ignore

                  write root "src/Lib/bin/Debug/Ghost.fsproj" emptyProject
                  |> ignore

                  Expect.equal
                      (Project.discover root
                       |> List.map (fun p -> p.Name))
                      [ "Lib" ]
                      "build output ignored")
          }

          test "skips solution entries whose file is missing" {
              withTemp (fun root ->
                  write root "Repo.slnx" "<Solution><Project Path=\"Gone/Gone.fsproj\" /></Solution>"
                  |> ignore

                  Expect.isEmpty (Project.discover root) "a stale solution entry is not a project")
          }

          test "returns an empty list for a directory with nothing in it" {
              withTemp (fun root -> Expect.isEmpty (Project.discover root) "nothing to find")
          } ]

[<Tests>]
let hintTests =
    testList
        "Project.hints"
        [ test "inherits from Directory.Build.props, with the project winning" {
              withTemp (fun root ->
                  write
                      root
                      "Directory.Build.props"
                      "<Project><PropertyGroup><Authors>me</Authors><Version>9.9.9</Version></PropertyGroup></Project>"
                  |> ignore

                  write
                      root
                      "src/Lib/Lib.fsproj"
                      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>"
                  |> ignore

                  let reference =
                      Project.discover root
                      |> List.exactlyOne

                  let table = Project.hints root reference
                  Expect.equal (Project.hint table "Authors") "me" "inherited from the props file"
                  Expect.equal (Project.hint table "Version") "1.2.3" "the project overrides the props file")
          }

          test "the nearer Directory.Build.props wins" {
              withTemp (fun root ->
                  write root "Directory.Build.props" "<Project><PropertyGroup><Authors>outer</Authors></PropertyGroup></Project>"
                  |> ignore

                  write root "src/Directory.Build.props" "<Project><PropertyGroup><Authors>inner</Authors></PropertyGroup></Project>"
                  |> ignore

                  write root "src/Lib/Lib.fsproj" emptyProject
                  |> ignore

                  let reference =
                      Project.discover root
                      |> List.exactlyOne

                  Expect.equal (Project.hint (Project.hints root reference) "Authors") "inner" "nearest wins")
          }

          test "expands property references" {
              withTemp (fun root ->
                  write
                      root
                      "Lib.fsproj"
                      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Moniker>net10</Moniker><TargetFramework>$(Moniker).0</TargetFramework></PropertyGroup></Project>"
                  |> ignore

                  let reference =
                      Project.discover root
                      |> List.exactlyOne

                  Expect.equal (Project.hint (Project.hints root reference) "TargetFramework") "net10.0" "composed moniker")
          }

          test "leaves an unresolvable reference as written" {
              withTemp (fun root ->
                  write
                      root
                      "Lib.fsproj"
                      "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>$(MSBuildProjectName).Extra</AssemblyName></PropertyGroup></Project>"
                  |> ignore

                  let reference =
                      Project.discover root
                      |> List.exactlyOne

                  Expect.equal
                      (Project.hint (Project.hints root reference) "AssemblyName")
                      "$(MSBuildProjectName).Extra"
                      "an unresolved hint reads as unresolved rather than as blank")
          }

          test "an undeclared Version hints the MSBuild default, other properties hint nothing" {
              withTemp (fun root ->
                  write root "Lib.fsproj" emptyProject
                  |> ignore

                  let reference =
                      Project.discover root
                      |> List.exactlyOne

                  let table = Project.hints root reference
                  Expect.equal (Project.hint table "Version") Project.DefaultVersion "the value a runtime read will report"
                  Expect.equal (Project.hint table "PackageId") "" "no hint for an undeclared property")
          } ]

[<Tests>]
let jsonTests =
    testList
        "Project.Json"
        [ test "reads the shape dotnet msbuild -getProperty emits" {
              let text =
                  "\n\n{\"Properties\":{\"TargetFramework\":\"netstandard2.0\",\"AssemblyName\":\"Lib\"}}\n\n"

              Expect.equal
                  (Project.Json.readFlatObject "Properties" text)
                  [ "TargetFramework", "netstandard2.0"; "AssemblyName", "Lib" ]
                  "both pairs, in order"
          }

          test "decodes escapes" {
              let text = "{\"Properties\":{\"Path\":\"c:\\\\dir\\ttab\",\"Unicode\":\"\\u0041\"}}"

              let pairs =
                  Project.Json.readFlatObject "Properties" text
                  |> Map.ofList

              Expect.equal pairs.["Path"] "c:\\dir\ttab" "backslash and tab"
              Expect.equal pairs.["Unicode"] "A" "unicode escape"
          }

          test "handles an empty object and an absent key" {
              Expect.isEmpty (Project.Json.readFlatObject "Properties" "{\"Properties\":{}}") "empty object"
              Expect.isEmpty (Project.Json.readFlatObject "Properties" "{\"Items\":{\"a\":\"b\"}}") "absent key"
              Expect.isEmpty (Project.Json.readFlatObject "Properties" "not json at all") "garbage"
          } ]

[<Tests>]
let commandTests =
    testList
        "Project.Runtime.command"
        [ test "puts the project straight after the verb" {
              Expect.equal (Project.Runtime.command "build" "/p/Lib.fsproj" []) [ "build"; "/p/Lib.fsproj" ] "bare build"
          }

          test "appends extra arguments" {
              Expect.equal
                  (Project.Runtime.command "pack" "/p/Lib.fsproj" [ "-c"; "Release" ])
                  [ "pack"; "/p/Lib.fsproj"; "-c"; "Release" ]
                  "extras last"
          }

          test "run takes its project behind --project" {
              Expect.equal
                  (Project.Runtime.command "run" "/p/Lib.fsproj" [ "--"; "--help" ])
                  [ "run"; "--project"; "/p/Lib.fsproj"; "--"; "--help" ]
                  "run is the special case"
          }

          test "a missing project evaluates to no properties rather than throwing" {
              Expect.isEmpty (Project.Runtime.readProperties "/no/such/project.fsproj" [ "Version" ]) "no answer"
          } ]

[<Tests>]
let providerTests =
    testList
        "ProjectProvider"
        [ test "finds this repository's projects through its .slnx" {
              Expect.isTrue Repo.Project.HasProjects "projects found"

              Expect.equal (Path.GetExtension Repo.Project.SolutionFile) ".slnx" "discovered via the solution file"
          }

          test "provides compile-time structure" {
              Expect.equal Repo.Project.``Partas.TypeProvider``.Name "Partas.TypeProvider" "project name"

              Expect.equal
                  Repo.Project.``Partas.TypeProvider``.RelativePath
                  "src/Partas.TypeProvider/Partas.TypeProvider.fsproj"
                  "relative path"
          }

          test "builds command lines without running anything" {
              Expect.equal
                  (Repo.Project.``Partas.TypeProvider``.Pack [ "-c"; "Release" ])
                  [ "pack"; Repo.Project.``Partas.TypeProvider``.Path; "-c"; "Release" ]
                  "pack arguments"

              Expect.equal (Repo.Project.Build.Run ()) [ "run"; "--project"; Repo.Project.Build.Path ] "run arguments"
          }

          test "reads real property values through msbuild" {
              Expect.isTrue (Repo.Project.IsDotnetAvailable ()) "dotnet is on PATH in this environment"

              Expect.equal Repo.Project.``Partas.TypeProvider``.TargetFramework "netstandard2.0" "evaluated, not read from XML"

              Expect.equal
                  (Repo.Project.``Partas.TypeProvider``.Property "RepositoryUrl")
                  "https://github.com/shayanhabibi/Partas.TypeProvider"
                  "the escape hatch reaches Directory.Build.props"

              Expect.equal
                  (Repo.Project.``Partas.TypeProvider``.Property "NoSuchPropertyAnywhere")
                  ""
                  "an undeclared property is empty, not an error"
          } ]
