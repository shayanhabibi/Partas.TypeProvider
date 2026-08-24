namespace Partas.TypeProvider.BuildHelper.DesignTime

open System
open System.Reflection
open ProviderImplementation.ProvidedTypes
open Microsoft.FSharp.Core.CompilerServices
open System.IO
open Partas.TypeProvider.BuildHelper.Runtime

[<AutoOpen>]
module private Helpers =

    /// Materialises a lazy filesystem enumeration, yielding nothing for entries the
    /// compiler cannot read. `Directory.Enumerate*` defers its work, so the throw
    /// lands mid-iteration rather than at the call - a single unreadable folder
    /// anywhere in the tree would otherwise take down the entire provider.
    let private tryEnumerate (enumerate: unit -> seq<'T>) =
        try
            enumerate () |> List.ofSeq
        with
        | :? UnauthorizedAccessException
        | :? Security.SecurityException
        | :? IOException -> []

    let private createFileLiterals
        (directoryInfo: DirectoryInfo)
        (rootType: ProvidedTypeDefinition)
        =

        for file in tryEnumerate directoryInfo.EnumerateFiles do
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

        // Add parent directory, unless this is a drive root and there is none
        if not (isNull (box directoryInfo.Parent)) then
            rootType.AddMemberDelayed(fun () ->
                let directoryType =
                    ProvidedTypeDefinition("..", Some typeof<obj>, hideObjectMethods = true)

                directoryType.AddXmlDoc $"Interface representing directory '{directoryInfo.FullName}'"

                createDirectoryProperties directoryInfo.Parent directoryType
                directoryType
            )

        for folder in tryEnumerate directoryInfo.EnumerateDirectories do
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

    let makeFileProvider (typ: ProvidedTypeDefinition) (root: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
        let fileProvider = ProvidedTypeDefinition("FileSystem", Some typeof<obj>, hideObjectMethods = true)
        fileProvider.AddXmlDoc "Interface representing a file provider"
        createDirectoryProperties root fileProvider
        fileProvider

    let makeVirtualFileProvider
        (typ: ProvidedTypeDefinition)
        (rootDirectory: DirectoryInfo)
        (rootNode: VirtualDirectory.Parser.INode) = typ.AddMemberDelayed <| fun () ->
        let virtualProvider = ProvidedTypeDefinition("VirtualFileSystem", Some typeof<obj>, hideObjectMethods = true)
        VirtualDirectory.createInodeProperties rootNode rootDirectory.FullName virtualProvider
        virtualProvider.AddXmlDoc "Interface representing a virtual file provider"
        virtualProvider

    /// A constant baked into the consuming assembly. Only used for structure that
    /// is stable across builds - never for shas or working tree state.
    let constString name (value: string) doc =
        let prop =
            ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

        prop.AddXmlDoc doc
        prop

    let constBool name (value: bool) doc =
        let prop =
            ProvidedProperty(name, typeof<bool>, isStatic = true, getterCode = fun _ -> <@@ value @@>)

        prop.AddXmlDoc doc
        prop

    /// Git ref names permit characters that cannot survive backtick quoting in F#.
    let private isUsableMemberName (name: string) =
        name <> "" && not (name.Contains "`") && name |> Seq.forall (fun c -> not (Char.IsControl c))

    let makeGitProvider
        (typ: ProvidedTypeDefinition)
        (rootDirectory: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
        let gitProvider = ProvidedTypeDefinition("Git", Some typeof<obj>, hideObjectMethods = true)
        let addendumValue text = $"<br/><br/>DesignTime Hint: <i>'{text}'</i>"

        match Git.discover rootDirectory.FullName with
        | None ->
            // Emitted even outside a repository, so consuming code fails on a
            // legible `IsRepository = false` rather than a missing member.
            gitProvider.AddXmlDoc
                $"<summary>No git repository was found at or above '{rootDirectory.FullName}'.</summary>"

            gitProvider.AddMember(
                constBool "IsRepository" false "<summary><c>false</c></summary>
    <remarks>Whether a git repository was found at compile time.</remarks>"
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
                constBool "IsRepository" true "<summary><c>true</c></summary><remarks>Whether a git repository was found at compile time.</remarks>"
                constString "GitDirectory" gitDir $"<summary>The <c>.git</c> directory for this working tree.{addendumValue gitDir}</summary>"
                constString "CommonDirectory" commonDir
                    $"<summary>The shared git directory holding refs and config. Differs from <c>GitDirectory</c> in linked worktrees.{addendumValue commonDir}</summary>"
                constString "WorkingDirectory" workTree $"<summary>The root of the working tree.{addendumValue workTree}</summary>"
            ]

            // Projections over volatile HEAD state. The values shown in XML docs
            // are design-time hints only; all properties read the repository at
            // runtime.
            let headDetails = Git.Runtime.revisionDetails workTree "HEAD" "HEAD"
            let designTimeBranch = Git.Runtime.headBranch gitDir

            let addRevisionMembers
                (projection: ProvidedTypeDefinition)
                (baseRef: string)
                (expression: string)
                (details: Git.Runtime.RevisionDetails)
                =
                let addRuntimeProperty name value hint description =
                    let property =
                        ProvidedProperty(
                            name,
                            typeof<string>,
                            isStatic = true,
                            getterCode = fun _ -> value
                        )

                    property.AddXmlDoc
                        $"<summary>{description}, resolved at runtime.{addendumValue hint}</summary>"

                    property

                let sha =
                    addRuntimeProperty
                        "Sha"
                        <@@ Git.Runtime.resolveRevision workTree baseRef expression @@>
                        details.Sha
                        "The full commit SHA"

                let shortSha =
                    addRuntimeProperty
                        "ShortSha"
                        <@@ Git.Runtime.shortRevision workTree baseRef expression @@>
                        details.ShortSha
                        "The abbreviated commit SHA"

                let date =
                    addRuntimeProperty
                        "Date"
                        <@@ (Git.Runtime.revisionDetails workTree baseRef expression).Date @@>
                        details.Date
                        "The author date of the commit"

                let author =
                    addRuntimeProperty
                        "Author"
                        <@@ (Git.Runtime.revisionDetails workTree baseRef expression).Author @@>
                        details.Author
                        "The commit author"

                let message =
                    addRuntimeProperty
                        "Message"
                        <@@ (Git.Runtime.revisionDetails workTree baseRef expression).Message @@>
                        details.Message
                        "The commit subject"

                projection.AddMembers [ sha; shortSha; date; author; message ]

            let headProjection =
                ProvidedTypeDefinition("Head", Some typeof<obj>, hideObjectMethods = true)

            headProjection.AddXmlDoc
                $"<summary>A projection over the commit currently resolved by <c>HEAD</c>. Use <c>IsAvailable</c> before reading its values.{addendumValue headDetails.Sha}</summary>"

            let headAvailable =
                ProvidedMethod(
                    "IsAvailable",
                    [],
                    typeof<bool>,
                    isStatic = true,
                    invokeCode = fun _ -> <@@ Git.Runtime.headIsAvailable gitDir commonDir @@>
                )

            headAvailable.AddXmlDoc
                "<summary>Whether <c>HEAD</c> currently resolves to an available commit.</summary>"

            headProjection.AddMember headAvailable

            addRevisionMembers headProjection "HEAD" "HEAD" headDetails

            let headBranchProjection =
                ProvidedTypeDefinition("HeadBranch", Some typeof<obj>, hideObjectMethods = true)

            headBranchProjection.AddXmlDoc
                $"<summary>A projection over the checked-out branch. It is unavailable for detached or unborn <c>HEAD</c>. Use <c>IsAvailable</c> before reading its values.{addendumValue designTimeBranch}</summary>"

            let branchAvailable =
                ProvidedMethod(
                    "IsAvailable",
                    [],
                    typeof<bool>,
                    isStatic = true,
                    invokeCode = fun _ -> <@@ Git.Runtime.headBranchIsAvailable gitDir commonDir @@>
                )

            branchAvailable.AddXmlDoc
                "<summary>Whether <c>HEAD</c> currently names a branch with an available commit.</summary>"

            let branchName =
                ProvidedProperty(
                    "Name",
                    typeof<string>,
                    isStatic = true,
                    getterCode = fun _ -> <@@ Git.Runtime.headBranch gitDir @@>
                )

            branchName.AddXmlDoc
                $"<summary>The checked-out branch name, resolved at runtime.{addendumValue designTimeBranch}</summary>"

            let branchRefName =
                ProvidedProperty(
                    "RefName",
                    typeof<string>,
                    isStatic = true,
                    getterCode = fun _ ->
                        <@@
                            let branch = Git.Runtime.headBranch gitDir
                            if branch = "" then "" else "refs/heads/" + branch
                        @@>
                )

            let designTimeBranchRef =
                if designTimeBranch = "" then "" else "refs/heads/" + designTimeBranch

            branchRefName.AddXmlDoc
                $"<summary>The fully qualified checked-out branch ref name, resolved at runtime.{addendumValue designTimeBranchRef}</summary>"

            let commitsAheadUpstreamMethod =
                ProvidedMethod(
                    "CommitsAheadOfUpstream",
                    [],
                    typeof<int>,
                    isStatic = true,
                    invokeCode = fun _ -> <@@ Git.Runtime.commitsAheadOfUpstream workTree gitDir commonDir @@>
                )

            commitsAheadUpstreamMethod.AddXmlDoc
                "<summary>Commits in HEAD not yet pushed to the configured upstream. Returns 0 when HEAD is detached, no upstream is set, or git is unavailable.</summary>"

            let commitsBehindUpstreamMethod =
                ProvidedMethod(
                    "CommitsBehindUpstream",
                    [],
                    typeof<int>,
                    isStatic = true,
                    invokeCode = fun _ -> <@@ Git.Runtime.commitsBehindUpstream workTree gitDir commonDir @@>
                )

            commitsBehindUpstreamMethod.AddXmlDoc
                "<summary>Commits in the configured upstream not yet merged into HEAD. Returns 0 when HEAD is detached, no upstream is set, or git is unavailable.</summary>"

            headBranchProjection.AddMember branchAvailable
            headBranchProjection.AddMember branchName
            headBranchProjection.AddMember branchRefName
            headBranchProjection.AddMember commitsAheadUpstreamMethod
            headBranchProjection.AddMember commitsBehindUpstreamMethod
            addRevisionMembers headBranchProjection "HEAD" "HEAD" headDetails

            gitProvider.AddMember headProjection
            gitProvider.AddMember headBranchProjection

            let isDetachedProperty =
                ProvidedProperty(
                    "IsDetached",
                    typeof<bool>,
                    isStatic = true,
                    getterCode = fun _ -> <@@ Git.Runtime.isDetached gitDir @@>
                )

            isDetachedProperty.AddXmlDoc "<summary>Whether <c>HEAD</c> is detached, read at runtime.</summary>"

            gitProvider.AddMember isDetachedProperty

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

            let commitsAheadMethod =
                ProvidedMethod(
                    "CommitsAhead",
                    [ ProvidedParameter("baseRef", typeof<string>); ProvidedParameter("headRef", typeof<string>) ],
                    typeof<int>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ Git.Runtime.commitsAhead workTree (%%args.[0]: string) (%%args.[1]: string) @@>
                )

            commitsAheadMethod.AddXmlDoc
                "<summary>Commits reachable from <c>headRef</c> but not from <c>baseRef</c>. Shells out to <c>git rev-list --count</c>. Returns 0 on error.</summary>"

            let commitsBehindMethod =
                ProvidedMethod(
                    "CommitsBehind",
                    [ ProvidedParameter("baseRef", typeof<string>); ProvidedParameter("headRef", typeof<string>) ],
                    typeof<int>,
                    isStatic = true,
                    invokeCode = fun args -> <@@ Git.Runtime.commitsBehind workTree (%%args.[0]: string) (%%args.[1]: string) @@>
                )

            commitsBehindMethod.AddXmlDoc
                "<summary>Commits reachable from <c>baseRef</c> but not from <c>headRef</c>. Shells out to <c>git rev-list --count</c>. Returns 0 on error.</summary>"

            gitProvider.AddMembers [ commitsAheadMethod; commitsBehindMethod ]

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
                                $"<summary>The commit sha this ref points at, resolved at runtime. Empty if the ref has since been deleted.{addendumValue reference.Target}</summary>"

                            let revisionType =
                                ProvidedTypeDefinition("Revision", Some typeof<obj>, hideObjectMethods = true)

                            revisionType.AddXmlDoc
                                "<summary>A git revision selected relative to this ref when the expression starts with <c>~</c> or <c>^</c>.</summary>"

                            // Attach the nested type before defining its static
                            // parameters; the SDK uses the declaring type to
                            // validate nested static-parameter instantiations.
                            refType.AddMember revisionType

                            revisionType.DefineStaticParameters(
                                [ ProvidedStaticParameter("expression", typeof<string>) ],
                                fun generatedName parameters ->
                                    let expression = parameters.[0] :?> string
                                    let resolvedExpression = Git.Runtime.revisionExpression fullName expression
                                    let designTimeDetails =
                                        Git.Runtime.revisionDetails workTree fullName expression

                                    let designTimeSha = designTimeDetails.Sha

                                    let revision =
                                        ProvidedTypeDefinition(generatedName, Some typeof<obj>, hideObjectMethods = true)

                                    revision.AddXmlDoc
                                        $"<summary>Git revision <c>{resolvedExpression}</c>.{addendumValue designTimeSha}</summary>"

                                    let sha =
                                        ProvidedProperty(
                                            "Sha",
                                            typeof<string>,
                                            isStatic = true,
                                            getterCode = fun _ ->
                                                <@@ Git.Runtime.resolveRevision workTree fullName expression @@>
                                        )

                                    sha.AddXmlDoc
                                        $"<summary>The full commit SHA, resolved at runtime.{addendumValue designTimeSha}</summary>"

                                    let shortSha =
                                        ProvidedProperty(
                                            "ShortSha",
                                            typeof<string>,
                                            isStatic = true,
                                            getterCode = fun _ ->
                                                <@@ Git.Runtime.shortRevision workTree fullName expression @@>
                                        )

                                    shortSha.AddXmlDoc
                                        $"<summary>The abbreviated commit SHA, resolved at runtime.{addendumValue designTimeDetails.ShortSha}</summary>"

                                    let addRuntimeRevisionProperty name value hint description =
                                        let property =
                                            ProvidedProperty(
                                                name,
                                                typeof<string>,
                                                isStatic = true,
                                                getterCode = fun _ -> value
                                            )

                                        property.AddXmlDoc $"<summary>{description}, resolved at runtime.{addendumValue hint}</summary>"
                                        property

                                    let date =
                                        addRuntimeRevisionProperty
                                            "Date"
                                            <@@ (Git.Runtime.revisionDetails workTree fullName expression).Date @@>
                                            designTimeDetails.Date
                                            "The author date of the commit"

                                    let author =
                                        addRuntimeRevisionProperty
                                            "Author"
                                            <@@ (Git.Runtime.revisionDetails workTree fullName expression).Author @@>
                                            designTimeDetails.Author
                                            "The commit author"

                                    let message =
                                        addRuntimeRevisionProperty
                                            "Message"
                                            <@@ (Git.Runtime.revisionDetails workTree fullName expression).Message @@>
                                            designTimeDetails.Message
                                            "The commit subject"

                                    revision.AddMembers [ sha; shortSha; date; author; message ]
                                    revisionType.AddMember revision
                                    revision
                            )

                            refType.AddMembers [
                                constString "Name" reference.Name $"<summary>The short ref name.{addendumValue reference.Name}</summary>"
                                constString "RefName" fullName $"<summary>The fully qualified ref name.{addendumValue fullName}</summary>"
                                commit
                            ]

                            if withUpstream then
                                refType.AddMember(
                                    constString "Upstream" (Git.upstreamOf config.Value reference.Name)
                                        "<summary>The configured upstream in <c>remote/branch</c> shorthand. Empty when none is configured.</summary>"
                                )

                            let refCommitsAheadMethod =
                                ProvidedMethod(
                                    "CommitsAhead",
                                    [ ProvidedParameter("otherRef", typeof<string>) ],
                                    typeof<int>,
                                    isStatic = true,
                                    invokeCode = fun args ->
                                        <@@ Git.Runtime.commitsAhead workTree (%%args.[0]: string) fullName @@>
                                )

                            refCommitsAheadMethod.AddXmlDoc
                                $"<summary>Commits in <c>{fullName}</c> not reachable from <c>otherRef</c>. Shells out to <c>git rev-list --count</c>.</summary>"

                            let refCommitsBehindMethod =
                                ProvidedMethod(
                                    "CommitsBehind",
                                    [ ProvidedParameter("otherRef", typeof<string>) ],
                                    typeof<int>,
                                    isStatic = true,
                                    invokeCode = fun args ->
                                        <@@ Git.Runtime.commitsBehind workTree (%%args.[0]: string) fullName @@>
                                )

                            refCommitsBehindMethod.AddXmlDoc
                                $"<summary>Commits in <c>otherRef</c> not reachable from <c>{fullName}</c>. Shells out to <c>git rev-list --count</c>.</summary>"

                            refType.AddMembers [ refCommitsAheadMethod; refCommitsBehindMethod ]

                            refType)

                    group

            makeRefGroup "Branches" "refs/heads" "<summary>Local branches.</summary>" true

            makeRefGroup "RemoteBranches" "refs/remotes" "<summary>Remote-tracking branches.</summary>" false

            makeRefGroup "Tags" "refs/tags" "<summary>Tags. For an annotated tag <c>Commit</c> is the tag object, not the commit it peels to.</summary>" false

            gitProvider.AddMemberDelayed <| fun () ->
                let designTimeLatestTag = Git.Runtime.latestTag workTree

                let designTimeLatestTagDetails = Git.Runtime.revisionDetails workTree designTimeLatestTag ""

                let latestTagProjection =
                    ProvidedTypeDefinition("LatestTag", Some typeof<obj>, hideObjectMethods = true)

                latestTagProjection.AddXmlDoc
                    $"<summary>The nearest ancestor tag reachable from HEAD (<c>git describe --tags --abbrev=0</c>), re-evaluated at runtime.{addendumValue designTimeLatestTag}</summary>"

                let latestTagIsAvailableMethod =
                    ProvidedMethod(
                        "IsAvailable",
                        [],
                        typeof<bool>,
                        isStatic = true,
                        invokeCode = fun _ -> <@@ Git.Runtime.latestTag workTree <> "" @@>
                    )

                latestTagIsAvailableMethod.AddXmlDoc
                    "<summary>Whether any tag is reachable from HEAD at runtime.</summary>"

                let latestTagNameProperty =
                    ProvidedProperty(
                        "Name",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ Git.Runtime.latestTag workTree @@>
                    )

                latestTagNameProperty.AddXmlDoc
                    $"<summary>The tag name, resolved at runtime.{addendumValue designTimeLatestTag}</summary>"

                let latestTagSha =
                    ProvidedProperty(
                        "Sha",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ (Git.Runtime.latestTagDetails workTree).Sha @@>
                    )

                latestTagSha.AddXmlDoc
                    $"<summary>The full commit SHA, resolved at runtime.{addendumValue designTimeLatestTagDetails.Sha}</summary>"

                let latestTagShortSha =
                    ProvidedProperty(
                        "ShortSha",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ (Git.Runtime.latestTagDetails workTree).ShortSha @@>
                    )

                latestTagShortSha.AddXmlDoc
                    $"<summary>The abbreviated commit SHA, resolved at runtime.{addendumValue designTimeLatestTagDetails.ShortSha}</summary>"

                let latestTagDate =
                    ProvidedProperty(
                        "Date",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ (Git.Runtime.latestTagDetails workTree).Date @@>
                    )

                latestTagDate.AddXmlDoc
                    $"<summary>The author date of the tagged commit, resolved at runtime.{addendumValue designTimeLatestTagDetails.Date}</summary>"

                let latestTagAuthor =
                    ProvidedProperty(
                        "Author",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ (Git.Runtime.latestTagDetails workTree).Author @@>
                    )

                latestTagAuthor.AddXmlDoc
                    $"<summary>The commit author, resolved at runtime.{addendumValue designTimeLatestTagDetails.Author}</summary>"

                let latestTagMessage =
                    ProvidedProperty(
                        "Message",
                        typeof<string>,
                        isStatic = true,
                        getterCode = fun _ -> <@@ (Git.Runtime.latestTagDetails workTree).Message @@>
                    )

                latestTagMessage.AddXmlDoc
                    $"<summary>The commit subject, resolved at runtime.{addendumValue designTimeLatestTagDetails.Message}</summary>"

                latestTagProjection.AddMember latestTagIsAvailableMethod
                latestTagProjection.AddMember latestTagNameProperty
                latestTagProjection.AddMember latestTagSha
                latestTagProjection.AddMember latestTagShortSha
                latestTagProjection.AddMember latestTagDate
                latestTagProjection.AddMember latestTagAuthor
                latestTagProjection.AddMember latestTagMessage

                let latestTagRevisionType =
                    ProvidedTypeDefinition("Revision", Some typeof<obj>, hideObjectMethods = true)

                latestTagRevisionType.AddXmlDoc
                    "<summary>A git revision relative to the nearest ancestor tag. Use <c>~N</c> to step back N commits from the tag, or any git revision expression.</summary>"

                latestTagProjection.AddMember latestTagRevisionType

                latestTagRevisionType.DefineStaticParameters(
                    [ ProvidedStaticParameter("expression", typeof<string>) ],
                    fun generatedName parameters ->
                        let expression = parameters.[0] :?> string
                        let designTimeRevision = Git.Runtime.latestTagRevisionDetails workTree expression
                        let designTimeSha = designTimeRevision.Sha

                        let revision =
                            ProvidedTypeDefinition(generatedName, Some typeof<obj>, hideObjectMethods = true)

                        revision.AddXmlDoc
                            $"<summary>Revision <c>{expression}</c> relative to the nearest ancestor tag.{addendumValue designTimeSha}</summary>"

                        let addLatestTagRevisionProperty name value hint description =
                            let property =
                                ProvidedProperty(name, typeof<string>, isStatic = true, getterCode = fun _ -> value)

                            property.AddXmlDoc
                                $"<summary>{description}, resolved at runtime.{addendumValue hint}</summary>"

                            property

                        revision.AddMembers [
                            addLatestTagRevisionProperty
                                "Sha"
                                <@@ (Git.Runtime.latestTagRevisionDetails workTree expression).Sha @@>
                                designTimeSha
                                "The full commit SHA"
                            addLatestTagRevisionProperty
                                "ShortSha"
                                <@@ (Git.Runtime.latestTagRevisionDetails workTree expression).ShortSha @@>
                                designTimeRevision.ShortSha
                                "The abbreviated commit SHA"
                            addLatestTagRevisionProperty
                                "Date"
                                <@@ (Git.Runtime.latestTagRevisionDetails workTree expression).Date @@>
                                designTimeRevision.Date
                                "The author date of the commit"
                            addLatestTagRevisionProperty
                                "Author"
                                <@@ (Git.Runtime.latestTagRevisionDetails workTree expression).Author @@>
                                designTimeRevision.Author
                                "The commit author"
                            addLatestTagRevisionProperty
                                "Message"
                                <@@ (Git.Runtime.latestTagRevisionDetails workTree expression).Message @@>
                                designTimeRevision.Message
                                "The commit subject"
                        ]

                        latestTagRevisionType.AddMember revision
                        revision
                )

                latestTagProjection

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
                            constString "Name" remote.Name $"<summary>The remote name.{addendumValue remote.Name}</summary>"
                            constString "FetchUrl" remote.FetchUrl $"<summary>The URL fetched from.{addendumValue remote.FetchUrl}</summary>"
                            constString "PushUrl" remote.PushUrl
                                $"<summary>The URL pushed to, falling back to the fetch URL when no <c>pushurl</c> is set.{addendumValue remote.PushUrl}</summary>"
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
                            constString "Name" submodule.Name $"<summary>The submodule name.{addendumValue submodule.Name}</summary>"
                            constString "Path" submodule.Path $"<summary>The path relative to the working tree root.{addendumValue submodule.Path}</summary>"
                            constString "FullPath" fullPath $"<summary>The absolute path to the submodule working tree.{addendumValue fullPath}</summary>"
                            constString "Url" submodule.Url $"<summary>The configured URL.{addendumValue submodule.Url}</summary>"
                            constString "Branch" submodule.Branch
                                $"<summary>The configured branch. Empty when none is set.{addendumValue submodule.Branch}</summary>"
                        ]

                        submoduleType)

                group

            gitProvider


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
        let hintText =
            if hintValue = "" then
                "<br/><br/><i>Not declared in the project at compile time.</i></summary>"
            elif name = "Version" && hintValue = Project.DefaultVersion then
                $"<br/><br/><b>DesignTime Hint:</b> <c>{hintValue}</c>. This is also the MSBuild default, so it may equally mean the project declares no <c>Version</c> at all - for instance when the version is supplied on the command line at pack time.</summary>"
            else
                $"<br/><br/><b>DesignTime Hint:</b> <c>{hintValue}</c>, read from the project XML without evaluating conditions. The runtime value is authoritative.</summary>"

        $"<summary>The MSBuild <c>{name}</c> property, evaluated at runtime." + hintText

    let makeProjectProvider
        (typ: ProvidedTypeDefinition)
        (rootDirectory: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
        let projectProvider =
            ProvidedTypeDefinition("Project", Some typeof<obj>, hideObjectMethods = true)

        let addendumValue text = $"<br/><br/><b>DesignTime Hint:</b> <c>'{text}'</c>"
        let root = rootDirectory.FullName
        let projects = Project.discover root
        let solution = Project.solutionFile root |> Option.defaultValue ""

        projectProvider.AddXmlDoc
            $"<summary>The projects belonging to '{root}', with prefilled <c>dotnet</c> command lines and a runtime view of their MSBuild properties.</summary>
    <remarks>Structure is fixed when this assembly is compiled; property values are read by shelling out to <c>dotnet msbuild -getProperty</c> when the consuming program runs, so the SDK must be present.</remarks>"

        projectProvider.AddMembers [
            constString "SolutionFile" solution
                $"<summary>The solution file the projects were taken from. Empty when they were found by walking the directory tree instead.{addendumValue solution}</summary>"
            constBool "HasProjects" (not projects.IsEmpty)
                $"<summary>Whether any project was found at compile time.{addendumValue <| not projects.IsEmpty}</summary>"
        ]

        let isAvailableMethod =
            ProvidedMethod(
                "IsDotnetAvailable",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Project.Runtime.isAvailable () @@>
            )

        isAvailableMethod.AddXmlDoc
            "<summary>Whether a usable <c>dotnet</c> is on PATH at runtime. Every property member returns an empty string without it.</summary>"

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
                            invokeCode = fun args -> <@@ Project.Runtime.property path (%%args.[0]: string) @@>
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
                        let methodName = string (Char.ToUpperInvariant verb.[0]) + verb.Substring 1

                        let doc =
                            $"<summary>The argument list for a <c>dotnet</c> command that {description} this project.</summary>
    <remarks>Returns the arguments only - nothing is executed. Extra arguments are appended.</remarks>"

                        let bare =
                            ProvidedMethod(
                                methodName,
                                [],
                                typeof<string list>,
                                isStatic = true,
                                invokeCode = fun _ -> <@@ Project.Runtime.command verb path [] @@>
                            )

                        bare.AddXmlDoc doc

                        let withArguments =
                            ProvidedMethod(
                                methodName,
                                [ ProvidedParameter("arguments", typeof<string list>) ],
                                typeof<string list>,
                                isStatic = true,
                                invokeCode = fun args -> <@@ Project.Runtime.command verb path (%%args.[0]: string list) @@>
                            )

                        withArguments.AddXmlDoc doc

                        projectType.AddMembers [ bare; withArguments ]

                    projectType

        projectProvider


[<TypeProvider>]
type BuildHelperProvider(config: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(config, assemblyReplacementMap = [ "Partas.TypeProvider.BuildHelper.DesignTime", "Partas.TypeProvider.BuildHelper" ])
    let namespaceName = "Partas.TypeProvider.BuildHelper"
    let name = "BuildHelperProvider"
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
                    makeFileProvider rootType rootDirectory

                    match configText |> ValueOption.map (VirtualDirectory.Parser.parse rootDirectory.FullName) with
                    | ValueSome rootNode when rootNode.Children.Count = 0 -> ()
                    | ValueNone -> ()
                    | ValueSome rootNode ->
                        makeVirtualFileProvider rootType rootDirectory rootNode
                if getCapabilityGit parametersValue then
                    makeGitProvider rootType rootDirectory

                if getCapabilityProject parametersValue then
                    makeProjectProvider rootType rootDirectory

                rootType
            )

    do this.AddNamespace(namespaceName, [ thisType ])
