namespace Partas.TypeProvider.BuildHelper.DesignTime

open System
open System.Reflection
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices
open System.IO

[<TypeProvider>]
type BuildHelperProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ "Partas.TypeProvider.BuildHelper.DesignTime", "Partas.TypeProvider.BuildHelper" ])
    let [<Literal>] namespaceName = "Partas.TypeProvider.BuildHelper"
    let [<Literal>] name = "BuildHelperProvider"
    let assembly = Assembly.GetExecutingAssembly()
    let thisType =
        ProvidedTypeDefinition(
            assembly,
            namespaceName,
            name,
            Some typeof<obj>,
            hideObjectMethods = true
        )
    let staticParameters = [
        let addStaticSummary (xmlDoc: string) (parameter: ProvidedStaticParameter) =
            parameter.AddXmlDoc $"<summary>{xmlDoc}</summary>"
            parameter
        ProvidedStaticParameter("rootPath", typeof<string>)
        |> addStaticSummary "The repository root path. Defaults to the current working directory."
        ProvidedStaticParameter("virtualPathConfig", typeof<string>, "")
        |> addStaticSummary "Virtual path configuration to provide compile time safety for paths that may not exist at design time."
        ProvidedStaticParameter("capabilityGit", typeof<bool>, false)
        |> addStaticSummary "Whether to provide a <c>GitProvider</c> instance."
        ProvidedStaticParameter("capabilityFileSystem", typeof<bool>, true)
        |> addStaticSummary "Whether to provide a <c>FileProvider</c> instance."
        ProvidedStaticParameter("capabilityProject", typeof<bool>, false)
        |> addStaticSummary "Whether to provide a <c>ProjectProvider</c> instance. Off by default: its property members shell out to <c>dotnet msbuild</c> at runtime, so enabling it makes the consuming program depend on the SDK being installed."
        ProvidedStaticParameter("capabilityFullOverride", typeof<bool>, false)
        |> addStaticSummary "Whether to override all capability switches to <c>true</c>. Off by default."
    ]
    // language=xml
    let thisTypeXmlDoc = """
<summary>
<para>
TypeProvider for build scripts and projects, providing compile-time literals,
hints, and scaffolding for common tasks.
</para>
<para>
The <c>BuildHelperProvider</c> provides several distinct providers which are optional,
but share a common feature of being defined at the root of a repository.
</para>
</summary>
<typeparam name="rootPath">
Path to the repository root. Absolute path, or relative.
</typeparam>
<typeparam name="virtualPathConfig">
<c>EasyBuild.FileSystemProvider</c> <c>VirtualFileSystem</c> type provider configuration
shelled from the repository root.
</typeparam>
<typeparam name="capabilityGit">
Whether to provide a <c>GitProvider</c> instance. Defaults to <c>false</c>.
</typeparam>
<typeparam name="capabilityFileSystem">
Whether to provide a <c>FileProvider</c> and/or <c>VirtualFileProvider</c> instance. Defaults to <c>true</c>.
</typeparam>
<typeparam name="capabilityProject">
Whether to provide a <c>ProjectProvider</c> instance. Defaults to <c>false</c>.
</typeparam>
<typeparam name="capabilityFullOverride">
A switch to override all capability switches to <c>true</c>. Defaults to <c>false</c>.
</typeparam>
"""
    do thisType.AddXmlDoc thisTypeXmlDoc
    let getCapabilityOverride (parametersValue: obj array) = parametersValue[5] :?> bool
    let getCapability (position: int) (parametersValue: obj array)=
        parametersValue[position] :?> bool
        || getCapabilityOverride parametersValue
    let getCapabilityGit = getCapability 2
    let getCapabilityFileSystem = getCapability 3
    let getCapabilityProject = getCapability 4

    let getRootDirectory (parametersValue: obj array) =
        let rootPath = parametersValue[0] :?> string
        let rootDirectory =
            match rootPath with
            | "" | "." -> Path.GetFullPath(config.ResolutionFolder)
            | _ -> Path.Combine(config.ResolutionFolder, rootPath)
            |> DirectoryInfo
        if not rootDirectory.Exists then
            failwith $"Directory '{rootDirectory.FullName}' does not exist."
        rootDirectory
    let getVirtualFileConfig (parametersValue: obj array) =
        let configText = parametersValue[1] :?> string
        if String.IsNullOrWhiteSpace configText
        then ValueNone
        else ValueSome configText
    do
        thisType.DefineStaticParameters(
            parameters = staticParameters,
            instantiationFunction = fun typeName parametersValue ->
                let configText = getVirtualFileConfig parametersValue
                let rootDirectory = getRootDirectory parametersValue
                let rootType =
                    ProvidedTypeDefinition(
                        assembly,
                        namespaceName,
                        typeName,
                        Some typeof<obj>,
                        hideObjectMethods = true
                        )
                rootType.AddXmlDoc
                    $"Interface representing directory '{rootDirectory.FullName}'"

                if getCapabilityFileSystem parametersValue then
                    FileSystemProvider.make rootType rootDirectory

                    match configText |> ValueOption.map (VirtualFileProvider.Parser.parse rootDirectory.FullName) with
                    | ValueSome rootNode when rootNode.Children.Count = 0 -> ()
                    | ValueNone -> ()
                    | ValueSome rootNode ->
                        VirtualFileProvider.make rootType rootDirectory rootNode
                if getCapabilityGit parametersValue then
                    GitProvider.make rootType rootDirectory

                if getCapabilityProject parametersValue then
                    ProjectProvider.make rootType rootDirectory

                rootType
            )

    do this.AddNamespace(namespaceName, [ thisType ])
