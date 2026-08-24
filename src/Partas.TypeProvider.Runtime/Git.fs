/// Reading of git repository state for the type provider.
///
/// Structure - ref, remote and submodule *names* - is read straight out of the
/// `.git` directory at design time, so expanding the provided types never
/// spawns a process. Volatile values - shas, dirtiness, anything needing
/// object or history traversal - are deferred to the `Runtime` module, which
/// shells out to `git` in the consuming program instead.
module Partas.TypeProvider.BuildHelper.Runtime.Git

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open Microsoft.FSharp.Core.CompilerServices

let private startsWith (prefix: string) (value: string) =
    value.StartsWith(prefix, StringComparison.Ordinal)

/// Minimal (partial grammar support) reader for the git config format.
module Config =

    type Entry =
        { Section: string
          Subsection: string
          Key: string
          Value: string }

    /// Reads a value, honouring backslash escapes and stopping at an inline
    /// comment that falls outside a quoted run.
    let private readValue (raw: string) =
        let sb = StringBuilder()
        let mutable inQuotes = false
        let mutable stop = false
        let mutable i = 0

        while not stop && i < raw.Length do
            let c = raw.[i]

            if c = '\\' && i + 1 < raw.Length then
                i <- i + 1

                sb.Append(
                    match raw.[i] with
                    | 'n' -> '\n'
                    | 't' -> '\t'
                    | other -> other
                )
                |> ignore
            elif c = '"' then
                inQuotes <- not inQuotes
            elif (c = '#' || c = ';') && not inQuotes then
                stop <- true
            else
                sb.Append c |> ignore

            i <- i + 1

        sb.ToString().Trim()

    let parse (text: string) =
        let entries = ResizeArray()
        let mutable section = ""
        let mutable subsection = ""

        for rawLine in text.Split('\n') do
            let line = rawLine.Trim()

            if line = "" || startsWith "#" line || startsWith ";" line then
                ()
            elif startsWith "[" line then
                let close = line.IndexOf ']'

                if close > 0 then
                    let header = line.Substring(1, close - 1).Trim()
                    let firstQuote = header.IndexOf '"'
                    let lastQuote = header.LastIndexOf '"'

                    if firstQuote >= 0 then
                        section <- header.Substring(0, firstQuote).Trim().ToLowerInvariant()

                        subsection <-
                            if lastQuote > firstQuote then
                                header.Substring(firstQuote + 1, lastQuote - firstQuote - 1)
                            else
                                ""
                    else
                        section <- header.ToLowerInvariant()
                        subsection <- ""
            else
                let eq = line.IndexOf '='

                if eq > 0 then
                    entries.Add
                        { Section = section
                          Subsection = subsection
                          Key = line.Substring(0, eq).Trim().ToLowerInvariant()
                          Value = readValue (line.Substring(eq + 1)) }

        List.ofSeq entries

    let parseFile (path: string) =
        if File.Exists path then
            try
                parse (File.ReadAllText path)
            with _ ->
                []
        else
            []

type RepoLayout =
    { GitDir: string
      CommonDir: string
      WorkTree: string }

type Ref =
    { Name: string
      FullName: string
      Target: string }

type Remote =
    { Name: string
      FetchUrl: string
      PushUrl: string }

type Submodule =
    { Name: string
      Path: string
      Url: string
      Branch: string }

/// In submodules and linked worktrees `.git` is a file holding `gitdir: <path>`.
let private resolveGitDirFile (dotGitFile: string) =
    try
        let text = File.ReadAllText(dotGitFile).Trim()

        if startsWith "gitdir:" text then
            let target = text.Substring(7).Trim()

            if Path.IsPathRooted target then
                target
            else
                Path.Combine(Path.GetDirectoryName dotGitFile, target)
            |> Path.GetFullPath
            |> Some
        else
            None
    with _ ->
        None

/// Walks up from `startDir` looking for a working tree.
let discover (startDir: string) =
    let rec walk (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        else
            let dotGit = Path.Combine(dir.FullName, ".git")

            if Directory.Exists dotGit then
                Some(dir.FullName, Path.GetFullPath dotGit)
            else
                match (if File.Exists dotGit then resolveGitDirFile dotGit else None) with
                | Some gitDir -> Some(dir.FullName, gitDir)
                | None -> walk dir.Parent

    try
        walk (DirectoryInfo startDir)
        |> Option.map (fun (workTree, gitDir) ->
            let commonDir =
                let marker = Path.Combine(gitDir, "commondir")

                if File.Exists marker then
                    let target = File.ReadAllText(marker).Trim()

                    if Path.IsPathRooted target then
                        target
                    else
                        Path.Combine(gitDir, target)
                    |> Path.GetFullPath
                else
                    gitDir

            { GitDir = gitDir
              CommonDir = commonDir
              WorkTree = workTree })
    with _ ->
        None

let private readPackedRefs (commonDir: string) =
    let path = Path.Combine(commonDir, "packed-refs")

    if not (File.Exists path) then
        []
    else
        try
            File.ReadAllLines path
            |> Array.toList
            |> List.choose (fun line ->
                let line = line.Trim()

                // `^` lines carry the peeled target of the preceding tag.
                if line = "" || startsWith "#" line || startsWith "^" line then
                    None
                else
                    let sp = line.IndexOf ' '

                    if sp > 0 then
                        Some(line.Substring(sp + 1).Trim(), line.Substring(0, sp))
                    else
                        None)
        with _ ->
            []

let private readLooseRefs (commonDir: string) (prefix: string) =
    let root = Path.Combine(commonDir, prefix.Replace('/', Path.DirectorySeparatorChar))

    if not (Directory.Exists root) then
        []
    else
        try
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            |> Seq.map (fun file ->
                let name =
                    file
                        .Substring(root.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/')

                let sha =
                    try
                        File.ReadAllText(file).Trim()
                    with _ ->
                        ""

                name, sha)
            |> Seq.filter (fun (name, _) -> name <> "" && not (startsWith "." name))
            |> List.ofSeq
        with _ ->
            []

/// All refs under a prefix such as `refs/heads`, with loose refs shadowing
/// anything of the same name in `packed-refs`.
let refsUnder (layout: RepoLayout) (prefix: string) =
    let prefix = prefix.TrimEnd '/'

    let packed =
        readPackedRefs layout.CommonDir
        |> List.choose (fun (full, sha) ->
            if startsWith (prefix + "/") full then
                Some(full.Substring(prefix.Length + 1), sha)
            else
                None)

    readLooseRefs layout.CommonDir prefix @ packed
    |> List.distinctBy fst
    |> List.map (fun (name, sha) ->
        { Name = name
          FullName = prefix + "/" + name
          Target = sha })
    |> List.sortBy (fun r -> r.Name)

let private grouped section (entries: Config.Entry list) =
    entries
    |> List.filter (fun e -> e.Section = section && e.Subsection <> "")
    |> List.groupBy (fun e -> e.Subsection)
    |> List.map (fun (name, es) ->
        let get key =
            es
            |> List.tryPick (fun e -> if e.Key = key then Some e.Value else None)
            |> Option.defaultValue ""

        name, get)
    |> List.sortBy fst

let repoConfig (layout: RepoLayout) =
    Config.parseFile (Path.Combine(layout.CommonDir, "config"))

let remotes (layout: RepoLayout) =
    repoConfig layout
    |> grouped "remote"
    |> List.map (fun (name, get) ->
        let url = get "url"

        { Name = name
          FetchUrl = url
          PushUrl =
            match get "pushurl" with
            | "" -> url
            | push -> push })

let submodules (layout: RepoLayout) =
    Config.parseFile (Path.Combine(layout.WorkTree, ".gitmodules"))
    |> grouped "submodule"
    |> List.map (fun (name, get) ->
        { Name = name
          Path = get "path"
          Url = get "url"
          Branch = get "branch" })

/// The configured upstream of a local branch, in `remote/branch` shorthand.
let upstreamOf (entries: Config.Entry list) (branch: string) =
    let get key =
        entries
        |> List.tryPick (fun e ->
            if e.Section = "branch" && e.Subsection = branch && e.Key = key then
                Some e.Value
            else
                None)
        |> Option.defaultValue ""

    match get "remote", get "merge" with
    | "", _
    | _, "" -> ""
    | remote, merge ->
        let short =
            if startsWith "refs/heads/" merge then
                merge.Substring 11
            else
                merge

        if remote = "." then short else remote + "/" + short

/// Functions invoked from erased quotations - these run in the *consuming*
/// program, not in the compiler, and must never throw.
module Runtime =

    type RevisionDetails =
        { Sha: string
          ShortSha: string
          Date: string
          Author: string
          Message: string }

    /// Prefixed to every invocation: never page, never consult a credential
    /// helper, and never wake the filesystem monitor daemon.
    let private safetyArgs = "--no-pager -c credential.helper= -c core.fsmonitor=false"

    /// Applied to every invocation: never prompt for credentials, and never
    /// take the optional index lock, which would otherwise contend with the
    /// user's own git commands.
    let private safetyEnvironment =
        [ "GIT_OPTIONAL_LOCKS", "0"
          "GIT_TERMINAL_PROMPT", "0"
          "GCM_INTERACTIVE", "never" ]

    let private defaultTimeoutMs = 2000

    let private quoteArgument (value: string) =
        "\"" + value.Replace("\"", "\\\"") + "\""

    /// Applies a revision suffix to a ref. A leading `~` or `^` is interpreted
    /// relative to the generated branch, while all other expressions are passed
    /// to git as written.
    let revisionExpression (baseRef: string) (expression: string) =
        if String.IsNullOrWhiteSpace expression then
            baseRef
        elif expression.StartsWith "~" || expression.StartsWith "^" then
            baseRef + expression
        else
            expression

    let rec private resolve (commonDir: string) (refName: string) depth =
        if depth > 5 || String.IsNullOrWhiteSpace refName then
            ""
        else
            let loose = Path.Combine(commonDir, refName.Replace('/', Path.DirectorySeparatorChar))

            let raw =
                if File.Exists loose then
                    try
                        File.ReadAllText(loose).Trim()
                    with _ ->
                        ""
                else
                    let packed = Path.Combine(commonDir, "packed-refs")

                    if not (File.Exists packed) then
                        ""
                    else
                        try
                            File.ReadAllLines packed
                            |> Array.tryPick (fun line ->
                                let line = line.Trim()
                                let sp = line.IndexOf ' '

                                if sp > 0 && not (startsWith "^" line) && line.Substring(sp + 1).Trim() = refName then
                                    Some(line.Substring(0, sp))
                                else
                                    None)
                            |> Option.defaultValue ""
                        with _ ->
                            ""

            if startsWith "ref:" raw then
                resolve commonDir (raw.Substring(4).Trim()) (depth + 1)
            else
                raw

    /// Resolves a ref name such as `refs/heads/main` to a sha, following
    /// symbolic refs. Returns "" when the ref no longer exists.
    let resolveRef (commonDir: string) (refName: string) = resolve commonDir refName 0

    let private headText (gitDir: string) =
        let path = Path.Combine(gitDir, "HEAD")

        if File.Exists path then
            try
                File.ReadAllText(path).Trim()
            with _ ->
                ""
        else
            ""

    /// The checked-out branch name, or "" when HEAD is detached.
    let headBranch (gitDir: string) =
        let text = headText gitDir

        if startsWith "ref: refs/heads/" text then
            text.Substring(16).Trim()
        else
            ""

    /// The sha HEAD resolves to. "" in a repository with no commits yet.
    let headSha (gitDir: string) (commonDir: string) =
        let text = headText gitDir

        if startsWith "ref:" text then
            resolveRef commonDir (text.Substring(4).Trim())
        else
            text

    /// Whether HEAD currently resolves to a commit.
    let headIsAvailable (gitDir: string) (commonDir: string) =
        headSha gitDir commonDir <> ""

    /// Whether HEAD identifies a branch with a commit available to inspect.
    let headBranchIsAvailable (gitDir: string) (commonDir: string) =
        headBranch gitDir <> "" && headSha gitDir commonDir <> ""

    let isDetached (gitDir: string) =
        let text = headText gitDir
        text <> "" && not (startsWith "ref:" text)

    /// Runs `git` in `workTree`, returning trimmed stdout on success and
    /// `None` on failure, non-zero exit, timeout, or a missing `git`.
    let tryExecTimeout (timeoutMs: int) (workTree: string) (args: string) =
        Proc.tryOutputWith safetyEnvironment timeoutMs workTree "git" (safetyArgs + " " + args)

    let tryExec (workTree: string) (args: string) =
        tryExecTimeout defaultTimeoutMs workTree args

    /// Escape hatch for arbitrary read-only git commands. Returns "" on failure.
    let exec (workTree: string) (args: string) =
        tryExec workTree args |> Option.defaultValue ""

    let private emptyRevisionDetails =
        { Sha = ""
          ShortSha = ""
          Date = ""
          Author = ""
          Message = "" }

    let private revisionCache = ConcurrentDictionary<string, RevisionDetails>()
    let private latestTagCache = ConcurrentDictionary<string, string>()

    /// Reads the commit identity and display metadata in one git invocation.
    /// The result is cached because each generated property may be accessed
    /// independently by a consuming build script.
    let revisionDetails (workTree: string) (baseRef: string) (expression: string) =
        let revision = revisionExpression baseRef expression
        let cacheKey = workTree + "\u0000" + revision

        match revisionCache.TryGetValue cacheKey with
        | true, details -> details
        | _ ->
            let argument = quoteArgument (revision + "^{commit}")
            let details =
                match
                    tryExec
                        workTree
                        ("show -s --format=%H%x00%h%x00%aI%x00%an%x00%s " + argument)
                with
                | Some output ->
                    match output.Split([| '\u0000' |], StringSplitOptions.None) with
                    | [| sha; shortSha; date; author; message |] ->
                        { Sha = sha
                          ShortSha = shortSha
                          Date = date
                          Author = author
                          Message = message }
                    | _ -> emptyRevisionDetails
                | None -> emptyRevisionDetails

            revisionCache.[cacheKey] <- details
            details

    /// Resolves a git revision expression to a commit SHA. Returns an empty
    /// string when the expression is unavailable or does not name a commit.
    let resolveRevision (workTree: string) (baseRef: string) (expression: string) =
        (revisionDetails workTree baseRef expression).Sha

    /// Resolves a git revision expression to a seven-character display SHA.
    let shortRevision (workTree: string) (baseRef: string) (expression: string) =
        (revisionDetails workTree baseRef expression).ShortSha

    /// Whether a usable `git` is on PATH in the consuming environment.
    let isAvailable () = Proc.exists "git" "--version"

    /// True when the working tree or index has changes.
    let isDirty (workTree: string) =
        match tryExec workTree "status --porcelain" with
        | Some output -> output <> ""
        | None -> false

    /// Commits reachable from `headRef` but not from `baseRef`.
    /// Returns 0 when git is unavailable, either ref is missing, or the call times out.
    let commitsAhead (workTree: string) (baseRef: string) (headRef: string) =
        match tryExec workTree ("rev-list --count " + quoteArgument (baseRef + ".." + headRef)) with
        | Some s ->
            match Int32.TryParse(s.Trim()) with
            | true, n -> n
            | _ -> 0
        | None -> 0

    /// Commits reachable from `baseRef` but not from `headRef`. Inverse of `commitsAhead`.
    let commitsBehind (workTree: string) (baseRef: string) (headRef: string) =
        commitsAhead workTree headRef baseRef

    /// The nearest ancestor tag reachable from HEAD, via `git describe --tags --abbrev=0`.
    /// Returns "" when no tags are reachable or git is unavailable.
    let latestTag (workTree: string) =
        match latestTagCache.TryGetValue workTree with
        | true, tag -> tag
        | _ ->
            let tag = tryExec workTree "describe --tags --abbrev=0" |> Option.defaultValue ""
            latestTagCache.[workTree] <- tag
            tag

    /// Revision details for the nearest ancestor tag. Returns the empty record when no tag is reachable.
    let latestTagDetails (workTree: string) =
        let tag = latestTag workTree
        if tag = "" then emptyRevisionDetails
        else revisionDetails workTree tag ""

    /// Revision details for the given expression relative to the nearest ancestor tag.
    /// Returns the empty record when no tag is reachable.
    let latestTagRevisionDetails (workTree: string) (expression: string) =
        let tag = latestTag workTree
        if tag = "" then emptyRevisionDetails
        else revisionDetails workTree tag expression

    /// Commits in HEAD not yet pushed to the configured upstream branch.
    /// Returns 0 when HEAD is detached, no upstream is configured, or git is unavailable.
    let commitsAheadOfUpstream (workTree: string) (gitDir: string) (commonDir: string) =
        let branch = headBranch gitDir
        if branch = "" then 0
        else
            let entries = Config.parseFile (Path.Combine(commonDir, "config"))

            match upstreamOf entries branch with
            | "" -> 0
            | upstream -> commitsAhead workTree ("refs/remotes/" + upstream) "HEAD"

    /// Commits in the configured upstream branch not yet merged into HEAD.
    /// Returns 0 when HEAD is detached, no upstream is configured, or git is unavailable.
    let commitsBehindUpstream (workTree: string) (gitDir: string) (commonDir: string) =
        let branch = headBranch gitDir
        if branch = "" then 0
        else
            let entries = Config.parseFile (Path.Combine(commonDir, "config"))

            match upstreamOf entries branch with
            | "" -> 0
            | upstream -> commitsBehind workTree ("refs/remotes/" + upstream) "HEAD"

[<assembly: TypeProviderAssembly "Partas.TypeProvider.BuildHelper.DesignTime">]
do ()
