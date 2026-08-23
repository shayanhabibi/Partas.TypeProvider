module Partas.TypeProvider.FileSystemProviders

open System
open System.Reflection
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices
open System.IO

let private createFileLiterals
    (directoryInfo: DirectoryInfo)
    (rootType: ProvidedTypeDefinition)
    =

    for file in directoryInfo.EnumerateFiles() do
        let adjustedFieldPath = file.FullName

        let pathFieldProperty =
            ProvidedProperty(
                file.Name,
                typeof<FileInfo>,
                isStatic = true,
                getterCode = fun args -> <@@ FileInfo(adjustedFieldPath) @@>
            )

        pathFieldProperty.AddXmlDoc
            $"<summary><c>System.IO.FileInfo</c> for '{file.FullName}'</summary>"

        rootType.AddMember pathFieldProperty

let rec private createDirectoryProperties
    (directoryInfo: DirectoryInfo)
    (rootType: ProvidedTypeDefinition)
    =

    // Extract the full path in a variable so we can use it in the ToString method
    let currentFolderFullName = directoryInfo.FullName

    let currentFolderProperty =
        ProvidedProperty(
            ".",
            typeof<DirectoryInfo>,
            isStatic = true,
            getterCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
        )

    let toStringMethod =
        ProvidedMethod(
            "ToString",
            [],
            typeof<string>,
            isStatic = true,
            invokeCode = fun args -> <@@ currentFolderFullName @@>
        )

    let getInfoMethod =
        ProvidedMethod(
            "GetInfo",
            [],
            typeof<DirectoryInfo>,
            isStatic = true,
            invokeCode = fun args -> <@@ DirectoryInfo(currentFolderFullName) @@>
        )

    let xmlDocText = $"Get the full path to '{currentFolderFullName}'"

    let xmlDocInfoText =
        $"<summary>Get the <c>System.IO.DirectoryInfo</c> to '{currentFolderFullName}'</summary>"

    currentFolderProperty.AddXmlDoc xmlDocInfoText
    getInfoMethod.AddXmlDoc xmlDocInfoText
    toStringMethod.AddXmlDoc xmlDocText

    rootType.AddMember currentFolderProperty
    rootType.AddMember toStringMethod
    rootType.AddMember getInfoMethod
    createFileLiterals directoryInfo rootType

    // Add parent directory
    rootType.AddMemberDelayed(fun () ->
        let directoryType =
            ProvidedTypeDefinition("..", Some typeof<obj>, hideObjectMethods = true)

        directoryType.AddXmlDoc $"Interface representing directory '{directoryInfo.FullName}'"

        createDirectoryProperties directoryInfo.Parent directoryType
        directoryType
    )

    for folder in directoryInfo.EnumerateDirectories() do
        // Build the folder member on demand as we can have a lot of folders/files
        rootType.AddMemberDelayed(fun () ->
            let folderType =
                ProvidedTypeDefinition(folder.Name, Some typeof<obj>, hideObjectMethods = true)

            folderType.AddXmlDoc $"Interface representing folder '{folder.FullName}'"

            // Walk through the folder
            createDirectoryProperties folder folderType
            // Store the folder type in the member
            folderType
        )

let private watchDir (directoryInfo: DirectoryInfo) =
    let watcher = new FileSystemWatcher(directoryInfo.FullName)
    watcher.EnableRaisingEvents <- true

    watcher

let private makeFileProvider (typ: ProvidedTypeDefinition) (root: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
    let fileProvider = ProvidedTypeDefinition("FileProvider", Some typeof<obj>, hideObjectMethods = true)
    fileProvider.AddXmlDoc "Interface representing a file provider"
    createDirectoryProperties root fileProvider
    fileProvider

let private makeVirtualFileProvider
    (typ: ProvidedTypeDefinition)
    (rootDirectory: DirectoryInfo)
    (rootNode: VirtualDirectory.Parser.INode) = typ.AddMemberDelayed <| fun () ->
    let virtualProvider = ProvidedTypeDefinition("VirtualFileProvider", Some typeof<obj>, hideObjectMethods = true)
    VirtualDirectory.createInodeProperties rootNode rootDirectory.FullName virtualProvider
    virtualProvider.AddXmlDoc "Interface representing a virtual file provider"
    virtualProvider

/// A constant baked into the consuming assembly. Only used for structure that
/// is stable across builds - never for shas or working tree state.
let private constString name (value: string) doc =
    let prop =
        ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

    prop.AddXmlDoc doc
    prop

let private constBool name (value: bool) doc =
    let prop =
        ProvidedProperty(name, typeof<bool>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

    prop.AddXmlDoc doc
    prop

/// Git ref names permit characters that cannot survive backtick quoting in F#.
let private isUsableMemberName (name: string) =
    name <> "" && not (name.Contains "`") && name |> Seq.forall (fun c -> not (Char.IsControl c))

let private makeGitProvider
    (typ: ProvidedTypeDefinition)
    (rootDirectory: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
    let gitProvider = ProvidedTypeDefinition("GitProvider", Some typeof<obj>, hideObjectMethods = true)

    match Git.discover rootDirectory.FullName with
    | None ->
        // Emitted even outside a repository, so consuming code fails on a
        // legible `IsRepository = false` rather than a missing member.
        gitProvider.AddXmlDoc
            $"<summary>No git repository was found at or above '{rootDirectory.FullName}'.</summary>"

        gitProvider.AddMember(
            constBool "IsRepository" false "<summary>Whether a git repository was found at compile time.</summary>"
        )

        gitProvider
    | Some layout ->
        let gitDir = layout.GitDir
        let commonDir = layout.CommonDir
        let workTree = layout.WorkTree

        gitProvider.AddXmlDoc
            $"<summary>Interface representing the git repository at '{workTree}'.</summary>"

        // Structure, read from `.git` when the provider was compiled.
        gitProvider.AddMembers [
            constBool "IsRepository" true "<summary>Whether a git repository was found at compile time.</summary>"
            constString "GitDirectory" gitDir "<summary>The <c>.git</c> directory for this working tree.</summary>"
            constString "CommonDirectory" commonDir
                "<summary>The shared git directory holding refs and config. Differs from <c>GitDirectory</c> in linked worktrees.</summary>"
            constString "WorkingDirectory" workTree "<summary>The root of the working tree.</summary>"
        ]

        // Volatile values, resolved when the consuming program runs.
        let headProperty =
            ProvidedProperty(
                "Head",
                typeof<string>,
                isStatic = true,
                getterCode = fun _ -> <@@ Git.Runtime.headSha gitDir commonDir @@>
            )

        headProperty.AddXmlDoc
            "<summary>The commit sha <c>HEAD</c> currently resolves to, read at runtime. Empty when the repository has no commits.</summary>"

        let headBranchProperty =
            ProvidedProperty(
                "HeadBranch",
                typeof<string>,
                isStatic = true,
                getterCode = fun _ -> <@@ Git.Runtime.headBranch gitDir @@>
            )

        headBranchProperty.AddXmlDoc
            "<summary>The checked-out branch name, read at runtime. Empty when <c>HEAD</c> is detached.</summary>"

        let isDetachedProperty =
            ProvidedProperty(
                "IsDetached",
                typeof<bool>,
                isStatic = true,
                getterCode = fun _ -> <@@ Git.Runtime.isDetached gitDir @@>
            )

        isDetachedProperty.AddXmlDoc "<summary>Whether <c>HEAD</c> is detached, read at runtime.</summary>"

        gitProvider.AddMembers [ headProperty; headBranchProperty; isDetachedProperty ]

        // Methods rather than properties: each of these starts a `git` process.
        let isDirtyMethod =
            ProvidedMethod(
                "IsDirty",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.isDirty workTree @@>
            )

        isDirtyMethod.AddXmlDoc
            "<summary>Whether the working tree or index has changes. Shells out to <c>git status</c> with <c>GIT_OPTIONAL_LOCKS=0</c>, so it cannot contend on <c>index.lock</c>. Returns <c>false</c> if git is unavailable.</summary>"

        let isAvailableMethod =
            ProvidedMethod(
                "IsGitAvailable",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.isAvailable () @@>
            )

        isAvailableMethod.AddXmlDoc
            "<summary>Whether a usable <c>git</c> is on PATH at runtime. Check this before relying on <c>Run</c>.</summary>"

        let runMethod =
            ProvidedMethod(
                "Run",
                [ ProvidedParameter("arguments", typeof<string>) ],
                typeof<string>,
                isStatic = true,
                invokeCode = fun args -> <@@ Git.Runtime.exec workTree (%%args.[0]: string) @@>
            )

        runMethod.AddXmlDoc
            "<summary>Runs an arbitrary read-only <c>git</c> command in the working tree and returns stdout. Returns an empty string on non-zero exit, a two second timeout, or a missing git. Never prompts and never touches the network.</summary>"

        gitProvider.AddMembers [ isDirtyMethod; isAvailableMethod; runMethod ]

        let config = lazy Git.repoConfig layout

        /// One nested type per ref: names are fixed at compile time, the sha
        /// behind them is not.
        let makeRefGroup groupName prefix doc withUpstream =
            gitProvider.AddMemberDelayed <| fun () ->
                let group = ProvidedTypeDefinition(groupName, Some typeof<obj>, hideObjectMethods = true)
                group.AddXmlDoc doc

                Git.refsUnder layout prefix
                |> List.filter (fun r -> isUsableMemberName r.Name)
                |> List.iter (fun reference ->
                    group.AddMemberDelayed <| fun () ->
                        let refType =
                            ProvidedTypeDefinition(reference.Name, Some typeof<obj>, hideObjectMethods = true)

                        refType.AddXmlDoc $"<summary>The ref '{reference.FullName}'.</summary>"

                        let fullName = reference.FullName

                        let commit =
                            ProvidedProperty(
                                "Commit",
                                typeof<string>,
                                isStatic = true,
                                getterCode = fun _ -> <@@ Git.Runtime.resolveRef commonDir fullName @@>
                            )

                        commit.AddXmlDoc
                            "<summary>The commit sha this ref points at, resolved at runtime. Empty if the ref has since been deleted.</summary>"

                        refType.AddMembers [
                            constString "Name" reference.Name "<summary>The short ref name.</summary>"
                            constString "RefName" fullName "<summary>The fully qualified ref name.</summary>"
                            commit
                        ]

                        if withUpstream then
                            refType.AddMember(
                                constString "Upstream" (Git.upstreamOf config.Value reference.Name)
                                    "<summary>The configured upstream in <c>remote/branch</c> shorthand. Empty when none is configured.</summary>"
                            )

                        refType)

                group

        makeRefGroup "Branches" "refs/heads" "<summary>Local branches.</summary>" true

        makeRefGroup "RemoteBranches" "refs/remotes" "<summary>Remote-tracking branches.</summary>" false

        makeRefGroup "Tags" "refs/tags" "<summary>Tags. For an annotated tag <c>Commit</c> is the tag object, not the commit it peels to.</summary>" false

        gitProvider.AddMemberDelayed <| fun () ->
            let group = ProvidedTypeDefinition("Remotes", Some typeof<obj>, hideObjectMethods = true)
            group.AddXmlDoc "<summary>Remotes, read from the repository config.</summary>"

            Git.remotes layout
            |> List.filter (fun r -> isUsableMemberName r.Name)
            |> List.iter (fun remote ->
                group.AddMemberDelayed <| fun () ->
                    let remoteType =
                        ProvidedTypeDefinition(remote.Name, Some typeof<obj>, hideObjectMethods = true)

                    remoteType.AddXmlDoc $"<summary>The remote '{remote.Name}' at '{remote.FetchUrl}'.</summary>"

                    remoteType.AddMembers [
                        constString "Name" remote.Name "<summary>The remote name.</summary>"
                        constString "FetchUrl" remote.FetchUrl "<summary>The URL fetched from.</summary>"
                        constString "PushUrl" remote.PushUrl
                            "<summary>The URL pushed to, falling back to the fetch URL when no <c>pushurl</c> is set.</summary>"
                    ]

                    remoteType)

            group

        gitProvider.AddMemberDelayed <| fun () ->
            let group = ProvidedTypeDefinition("Submodules", Some typeof<obj>, hideObjectMethods = true)
            group.AddXmlDoc "<summary>Submodules, read from <c>.gitmodules</c>.</summary>"

            Git.submodules layout
            |> List.filter (fun s -> isUsableMemberName s.Name)
            |> List.iter (fun submodule ->
                group.AddMemberDelayed <| fun () ->
                    let submoduleType =
                        ProvidedTypeDefinition(submodule.Name, Some typeof<obj>, hideObjectMethods = true)

                    submoduleType.AddXmlDoc $"<summary>The submodule '{submodule.Name}' at '{submodule.Path}'.</summary>"

                    let fullPath = Path.Combine(workTree, submodule.Path)

                    submoduleType.AddMembers [
                        constString "Name" submodule.Name "<summary>The submodule name.</summary>"
                        constString "Path" submodule.Path "<summary>The path relative to the working tree root.</summary>"
                        constString "FullPath" fullPath "<summary>The absolute path to the submodule working tree.</summary>"
                        constString "Url" submodule.Url "<summary>The configured URL.</summary>"
                        constString "Branch" submodule.Branch
                            "<summary>The configured branch. Empty when none is set.</summary>"
                    ]

                    submoduleType)

            group

        gitProvider


[<TypeProvider>]
type ExperimentalProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config)
    let namespaceName = "Partas.TypeProviders"
    let name = "MyTypeProvider"
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
        ProvidedStaticParameter("rootPath", typeof<string>)
        ProvidedStaticParameter("configText", typeof<string>, "")
    ]
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

                makeFileProvider rootType rootDirectory

                match configText |> ValueOption.map (VirtualDirectory.Parser.parse rootDirectory.FullName) with
                | ValueSome rootNode when rootNode.Children.Count = 0 -> ()
                | ValueNone -> ()
                | ValueSome rootNode ->
                    makeVirtualFileProvider rootType rootDirectory rootNode

                makeGitProvider rootType rootDirectory

                rootType
            )

    do this.AddNamespace(namespaceName, [ thisType ])

[<assembly: TypeProviderAssembly>]
do ()
