module private Partas.TypeProvider.BuildHelper.DesignTime.ProjectProvider

open System
open System.IO
open Partas.TypeProvider.BuildHelper.Runtime
open ProviderImplementation.ProvidedTypes


/// The `dotnet` verbs given a command builder on every provided project.
let private projectVerbs =
    [ "restore", "restores"
      "build", "builds"
      "run", "runs"
      "test", "tests"
      "pack", "packs"
      "publish", "publishes"
      "clean", "cleans" ]

/// The doc for a property member: what it is, plus the value read out of the
/// project XML at compile time. The hint is advisory - it is produced without
/// evaluating conditions or imports, so the runtime value is the one to trust.
let private propertyDoc (name: string) (hintValue: string) =
    summary {
        "The MSBuild "; c { name }; " property, evaluated at runtime."
        if hintValue = "" then
            br; br; i { "Not declared in the project at compile time." }
        elif name = "Version" && hintValue = Project.DefaultVersion then
            br; br; b { "DesignTime Hint:" }; " "; c { hintValue }; ". "
            "This is also the MSBuild default, so it may equally mean the project declares no "
            c { "Version" }
            " at all - for instance when the version is supplied on the command line at pack time."
        else
            br; br; b { "DesignTime Hint:" }; " "; c { hintValue }
            ", read from the project XML without evaluating conditions. The runtime value is authoritative."
    }

let make
    (typ: ProvidedTypeDefinition)
    (rootDirectory: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
    let projectProvider =
        ProvidedTypeDefinition("Project", Some typeof<obj>, hideObjectMethods = true)
    let addendumValue (text: string) = docs { br; br; b { "DesignTime Hint:" }; " "; c { squote { text } } }
    let root = rootDirectory.FullName
    let projects = Project.discover root
    let solution = Project.solutionFile root |> Option.defaultValue ""

    projectProvider.setXmlDoc {
        summary {
            "The projects belonging to "; squote { root }; ", "
            "with prefilled "; c { "dotnet" }; " command lines and a runtime view of their MSBuild properties."
        }
        remarks {
            "Structure is fixed when this assembly is compiled; property values are read by shelling out to "
            c { "dotnet msbuild -getProperty" }
            " when the consuming program runs, so the SDK must be present."
        }
    }

    projectProvider.AddMembers [
        constString "SolutionFile" solution <| summary { "The solution file the projects were taken from. Empty when they were found by walking the directory tree instead."; addendumValue solution }
        constBool "HasProjects" (not projects.IsEmpty) <| summary { "Whether any project was found at compile time."; addendumValue <| string (not projects.IsEmpty) }
    ]

    let isAvailableMethod =
        ProvidedMethod(
            "IsDotnetAvailable",
            [],
            typeof<bool>,
            isStatic = true,
            invokeCode = fun _ -> <@@ Project.Runtime.isAvailable () @@>
        )

    isAvailableMethod.AddXmlDoc <| summary { "Whether a usable <c>dotnet</c> is on PATH at runtime. Every property member returns an empty string without it." }

    projectProvider.AddMember isAvailableMethod

    // Two projects can share a file name in different directories; those fall
    // back to their path so both stay reachable.
    let occurrences = projects |> List.countBy _.Name |> Map.ofList

    let memberName (reference: Project.ProjectRef) =
        match occurrences.TryFind reference.Name with
        | Some 1 -> reference.Name
        | _ -> reference.RelativePath

    for reference in projects do
        let name = memberName reference

        if isUsableMemberName name then
            projectProvider.AddMemberDelayed <| fun () ->
                let projectType =
                    ProvidedTypeDefinition(name, Some typeof<obj>, hideObjectMethods = true)

                projectType.AddXmlDoc $"<summary>The project '{reference.RelativePath}'.</summary>"

                let path = reference.Path

                projectType.AddMembers [
                    constString "Name" reference.Name $"<summary>The project file name without its extension.{addendumValue reference.Name}</summary>"
                    constString "Path" path $"<summary>The absolute path to the project file.{addendumValue path}</summary>"
                    constString "RelativePath" reference.RelativePath
                        $"<summary>The path to the project file relative to the provider root.{addendumValue reference.RelativePath}</summary>"
                    constString "Directory" reference.Directory
                        $"<summary>The absolute path to the directory containing the project file.{addendumValue reference.Directory}</summary>"
                ]

                // Read once for all the property members below.
                let hints = Project.hints root reference

                for propertyName in Project.defaultProperties do
                    let property =
                        ProvidedProperty(
                            propertyName,
                            typeof<string>,
                            isStatic = true,
                            getterCode = fun _ -> <@@ Project.Runtime.property path propertyName @@>
                        )

                    property.AddXmlDoc(propertyDoc propertyName (Project.hint hints propertyName))
                    projectType.AddMember property

                let propertyMethod =
                    ProvidedMethod(
                        "Property",
                        [ ProvidedParameter("name", typeof<string>) ],
                        typeof<string>,
                        isStatic = true,
                        invokeCode = fun args -> <@@ Project.Runtime.property path (%%args[0]: string) @@>
                    )

                propertyMethod.AddXmlDoc
                    "<summary>Evaluates any MSBuild property by name, for properties without a member of their own. Returns an empty string when the property is undeclared or the project cannot be evaluated.</summary>"

                let invalidateMethod =
                    ProvidedMethod(
                        "Invalidate",
                        [],
                        typeof<unit>,
                        isStatic = true,
                        invokeCode = fun _ -> <@@ Project.Runtime.invalidate path @@>
                    )

                invalidateMethod.AddXmlDoc
                    "<summary>Discards the cached property values for this project, so the next read re-evaluates it. Needed only after a build has changed the project.</summary>"

                projectType.AddMembers [ propertyMethod; invalidateMethod ]

                for verb, description in projectVerbs do
                    let methodName = string (Char.ToUpperInvariant verb[0]) + verb.Substring 1

                    let listDocString = docs {
                        summary { "The argument list for a"; c { "dotnet" }; "command that"; description; "this project." }
                        remarks { "Returns the arguments only - nothing is executed. Extra arguments are appended." }
                    }
                    let bare =
                        ProvidedMethod(
                            methodName,
                            [],
                            typeof<string list>,
                            isStatic = true,
                            invokeCode = fun _ -> <@@ Project.Runtime.command verb path [] @@>
                        ).addXmlDoc { listDocString }

                    let withArguments =
                        ProvidedMethod(
                            methodName,
                            [ ProvidedParameter("arguments", typeof<string list>) ],
                            typeof<string list>,
                            isStatic = true,
                            invokeCode = fun args -> <@@ Project.Runtime.command verb path (%%args[0]: string list) @@>
                        ).addXmlDoc { listDocString }

                    let withString =
                        ProvidedMethod(
                            methodName,
                            [ ProvidedParameter("arguments", typeof<string>) ],
                            typeof<string>,
                            isStatic = true,
                            invokeCode = fun args -> <@@ Project.Runtime.commandString verb path [ (%%args[0]: string) ] @@>
                        ).addXmlDoc {
                            summary { "The args for a"; c { "dotnet" }; "command that"; description; "this project." }
                            remarks { "Returns the arguments only - nothing is executed. Extra arguments are appended." }
                        }

                    let withPropSetters =
                        ProvidedMethod(
                            methodName,
                            [
                                ProvidedParameter("arguments", typeof<string list>)
                                ProvidedParameter("propertyOverrides", typeof<(string * string) list>)
                            ],
                            typeof<string list>,
                            isStatic = true,
                            invokeCode = fun args -> <@@ Project.Runtime.commandWithPropSetters verb path (%%args[0]: string list) (%%args[1]: (string * string) list) @@>
                        ).addXmlDoc {
                            summary { "The args for a"; c { "dotnet" }; "command that"; description; "this project." }
                            remarks { "Returns the arguments only - nothing is executed. Extra arguments are appended. Property overrides are applied to the command line after arguments." }
                        }
                    let withStringPropSetters =
                        ProvidedMethod(
                            methodName,
                            [
                                ProvidedParameter("arguments", typeof<string>)
                                ProvidedParameter("propertyOverrides", typeof<(string * string) list>)
                            ],
                            typeof<string>,
                            isStatic = true,
                            invokeCode = fun args -> <@@ Project.Runtime.commandStringWithPropSetters verb path (%%args[0]: string) (%%args[1]: (string * string) list) @@>
                        ).addXmlDoc {
                            summary { "The args for a"; c { "dotnet" }; "command that"; description; "this project." }
                            remarks { "Returns the arguments only - nothing is executed. Extra arguments are appended. Property overrides are applied to the command line after arguments." }
                        }

                    projectType.AddMembers [ bare; withArguments; withString; withPropSetters; withStringPropSetters ]

                projectType

    let getAllProjectsMethod =
        ProvidedMethod(
            "AllProjects",
            [ ProvidedParameter("searchRuntime", typeof<bool>, optionalValue = false) ],
            typeof<Project.ProjectRef list>,
            isStatic = true,
            invokeCode = fun args -> <@@ if not (%%args[0]: bool) then projects else Project.discover root @@>
            ).addXmlDoc {
            summary { "All projects found. Pass"; c { "true" }; "to retrieve the projects at runtime." }
            param "searchRuntime" { "Whether to only return projects that have been evaluated at runtime." }
        }
    projectProvider.AddMember getAllProjectsMethod

    projectProvider
