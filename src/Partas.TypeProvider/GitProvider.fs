module private Partas.TypeProvider.BuildHelper.DesignTime.GitProvider

open Partas.TypeProvider.BuildHelper.Runtime
open System.IO
open ProviderImplementation.ProvidedTypes

let make
    (typ: ProvidedTypeDefinition)
    (rootDirectory: DirectoryInfo) = typ.AddMemberDelayed <| fun () ->
    let gitProvider = ProvidedTypeDefinition("Git", Some typeof<obj>, hideObjectMethods = true)
    let addendumValue (text: string) = docs {
        br + br
        "DesignTime Hint: "
        i { squote { text } }
    }

    match Git.discover rootDirectory.FullName with
    | None ->
        // Emitted even outside a repository, so consuming code fails on a
        // legible `IsRepository = false` rather than a missing member.
        summary {
            "No git repository was found at or above "
            squote { rootDirectory.FullName }
        }
        |> gitProvider.AddXmlDoc

        docs {
            summary { c { false } }
            rn
            remarks { "Whether a git repository was found at compile time." }
        }
        |> constBool "IsRepository" false
        |> gitProvider.AddMember

        gitProvider
    | Some layout ->
        let gitDir = layout.GitDir
        let commonDir = layout.CommonDir
        let workTree = layout.WorkTree

        summary {
            "Interface representing the git repository at "
            squote { workTree }
        }
        |> gitProvider.AddXmlDoc

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

        headProjection.setXmlDoc {
            summary { "A projection over the commit currently resolved by "; c { "HEAD" }; "."
                      "Use "; c { "IsAvailable" }; " before reading its values."
                      addendumValue headDetails.Sha }
        }

        let headAvailable =
            ProvidedMethod(
                "IsAvailable",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.headIsAvailable gitDir commonDir @@>
            )

        headAvailable.setXmlDoc { summary { "Whether"; c { "HEAD" }; "currently resolves to an available commit." } }

        headProjection.AddMember headAvailable

        addRevisionMembers headProjection "HEAD" "HEAD" headDetails

        let headBranchProjection =
            ProvidedTypeDefinition("HeadBranch", Some typeof<obj>, hideObjectMethods = true)

        headBranchProjection.setXmlDoc {
            summary {
                $"""A projection over the checked-out branch. It is unavailable for detached or unborn {c { "HEAD" }}. Use {c { "IsAvailable" }} before reading its values."""
                addendumValue designTimeBranch
            }
        }

        let branchAvailable =
            ProvidedMethod(
                "IsAvailable",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.headBranchIsAvailable gitDir commonDir @@>
            )

        branchAvailable.setXmlDoc { summary { "Whether"; c { "HEAD" }; "currently names a branch with an available commit." } }

        let branchName =
            ProvidedProperty(
                "Name",
                typeof<string>,
                isStatic = true,
                getterCode = fun _ -> <@@ Git.Runtime.headBranch gitDir @@>
            )

        branchName.setXmlDoc { summary { "The checked-out branch name, resolved at runtime."; addendumValue designTimeBranch } }

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

        branchRefName.setXmlDoc { summary { "The fully qualified checked-out branch ref name, resolved at runtime."; addendumValue designTimeBranchRef } }

        let commitsAheadUpstreamMethod =
            ProvidedMethod(
                "CommitsAheadOfUpstream",
                [],
                typeof<int>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.commitsAheadOfUpstream workTree gitDir commonDir @@>
            )

        commitsAheadUpstreamMethod.setXmlDoc { summary {
            "Commits in HEAD not yet merged into the configured upstream. Returns 0 when HEAD is detached, no upstream is set, or git is unavailable."
        } }

        let commitsBehindUpstreamMethod =
            ProvidedMethod(
                "CommitsBehindUpstream",
                [],
                typeof<int>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.commitsBehindUpstream workTree gitDir commonDir @@>
            )

        commitsBehindUpstreamMethod.setXmlDoc { summary {
            "Commits in the configured upstream not yet merged into HEAD. Returns 0 when HEAD is detached, no upstream is set, or git is unavailable."
        } }

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

        isDetachedProperty.setXmlDoc { summary { "Whether <c>HEAD</c> is detached, read at runtime." } }

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

        isDirtyMethod.setXmlDoc { summary {
            "Whether the working tree or index has changes."
            "Shells out to"; c { "git status" }; "with"; c {"GIT_OPTIONAL_LOCKS=0"}
            "so it cannot be contend on"
            c { "index.lock" }
            br
            "Returns"; c { false }; "if git is unavailable."
        } }

        let isAvailableMethod =
            ProvidedMethod(
                "IsGitAvailable",
                [],
                typeof<bool>,
                isStatic = true,
                invokeCode = fun _ -> <@@ Git.Runtime.isAvailable () @@>
            )

        isAvailableMethod.setXmlDoc { summary {
            "Whether a usable"
            c { "git" }
            "is on PATH at runtime."
            "Check this before relying on"
            c { "Run" }
        } }

        let runMethod =
            ProvidedMethod(
                "Run",
                [ ProvidedParameter("arguments", typeof<string>) ],
                typeof<string>,
                isStatic = true,
                invokeCode = fun args -> <@@ Git.Runtime.exec workTree (%%args[0]: string) @@>
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
                invokeCode = fun args -> <@@ Git.Runtime.commitsAhead workTree (%%args[0]: string) (%%args[1]: string) @@>
            )

        commitsAheadMethod.AddXmlDoc
            "<summary>Commits reachable from <c>headRef</c> but not from <c>baseRef</c>. Shells out to <c>git rev-list --count</c>. Returns 0 on error.</summary>"

        let commitsBehindMethod =
            ProvidedMethod(
                "CommitsBehind",
                [ ProvidedParameter("baseRef", typeof<string>); ProvidedParameter("headRef", typeof<string>) ],
                typeof<int>,
                isStatic = true,
                invokeCode = fun args -> <@@ Git.Runtime.commitsBehind workTree (%%args[0]: string) (%%args[1]: string) @@>
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
                                let expression = parameters[0] :?> string
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
                                    <@@ Git.Runtime.commitsAhead workTree (%%args[0]: string) fullName @@>
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
                                    <@@ Git.Runtime.commitsBehind workTree (%%args[0]: string) fullName @@>
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
                    let expression = parameters[0] :?> string
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
                        summary { "The remote name."; addendumValue remote.Name }
                        |> constString "Name" remote.Name
                        summary { "The URL fetched from."; addendumValue remote.FetchUrl }
                        |> constString "FetchUrl" remote.FetchUrl
                        summary {
                            "The URL pushed to, falling back to the fetch URL when no "
                            c { "pushurl" }
                            " is set."
                            addendumValue remote.PushUrl
                        }
                        |> constString "PushUrl" remote.PushUrl
                    ]

                    remoteType)

            group

        gitProvider.AddMemberDelayed <| fun () ->
            let group = ProvidedTypeDefinition("Submodules", Some typeof<obj>, hideObjectMethods = true)
            summary {
                "Submodules, read from "
                c { ".gitmodules" }
                "."
            }
            |> group.AddXmlDoc

            Git.submodules layout
            |> List.filter (fun s -> isUsableMemberName s.Name)
            |> List.iter (fun submodule ->
                group.AddMemberDelayed <| fun () ->
                    let submoduleType =
                        ProvidedTypeDefinition(submodule.Name, Some typeof<obj>, hideObjectMethods = true)

                    summary { "The submodule "; squote { submodule.Name }; " at "; squote { submodule.Path }; "." }
                    |> submoduleType.AddXmlDoc

                    let fullPath = Path.Combine(workTree, submodule.Path)

                    submoduleType.AddMembers [
                        constString "Name" submodule.Name <| summary { "The submodule name."; addendumValue submodule.Name }
                        constString "Path" submodule.Path <| summary { "The path relative to the working tree root."; addendumValue submodule.Path }
                        constString "FullPath" fullPath <| summary { "The absolute path to the submodule working tree."; addendumValue fullPath }
                        constString "Url" submodule.Url <| summary { "The configured URL."; addendumValue submodule.Url }
                        constString "Branch" submodule.Branch <| summary { "The configured branch. Empty when none is set."; addendumValue submodule.Branch }
                    ]

                    submoduleType)

            group

        gitProvider
