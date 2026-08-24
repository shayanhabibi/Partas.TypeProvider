/// Reading of MSBuild project structure for the type provider.
///
/// The same split as `Git`: structure - which projects exist, where they live -
/// is read from the solution file or the directory tree at design time, so
/// expanding the provided types never spawns a process and never loads MSBuild
/// into the compiler. Property *values* are read at runtime by `Runtime`,
/// which shells out to `dotnet msbuild -getProperty`.
///
/// The design-time values are deliberately approximate. They are read straight
/// out of the project XML with no condition evaluation, and exist only to be
/// shown in a doc comment as a hint. Nothing compiles against them.
module Partas.TypeProvider.Runtime.Project

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Xml.Linq

let private ordinalEquals (a: string) (b: string) =
    String.Equals (a, b, StringComparison.OrdinalIgnoreCase)

/// Extensions treated as buildable projects. Anything else in a solution file
/// - solution folders, shared projects - is ignored.
let private projectExtensions = [ ".fsproj"; ".csproj"; ".vbproj" ]

let private isProjectFile (path: string) =
    let ext = Path.GetExtension path

    projectExtensions
    |> List.exists (ordinalEquals ext)

/// Directories never worth walking into when looking for projects.
let private skippedDirectories =
    [ "bin"; "obj"; ".git"; ".vs"; "node_modules"; "packages" ]

/// A project as it appears in the tree, with everything needed to name it, to
/// point `dotnet` at it, and to find the `Directory.Build.props` above it.
type ProjectRef =
    {
        /// The file name without its extension, e.g. `Partas.TypeProvider`.
        Name: string
        /// Absolute path to the project file.
        Path: string
        /// Absolute path to the directory containing the project file.
        Directory: string
        /// Path relative to the provider root, using forward slashes.
        RelativePath: string
    }

let private normalise (path: string) =
    path.Replace ('\\', '/')

let private relativeTo (root: string) (path: string) =
    let root =
        Path.GetFullPath(root).TrimEnd (Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let full = Path.GetFullPath path

    if
        full.StartsWith (root, StringComparison.OrdinalIgnoreCase)
        && full.Length > root.Length
    then
        normalise (
            full.Substring (
                root.Length
                + 1
            )
        )
    else
        normalise full

let private makeRef (root: string) (path: string) =
    let full = Path.GetFullPath path

    { Name = Path.GetFileNameWithoutExtension full
      Path = full
      Directory = Path.GetDirectoryName full
      RelativePath = relativeTo root full }

/// Reads `<Project Path="..." />` out of an `.slnx`, at any nesting depth so
/// solution folders are transparent.
let private parseSlnx (root: string) (solutionPath: string) =
    try
        let doc = XDocument.Load (solutionPath: string)

        doc.Descendants ()
        |> Seq.filter (fun element -> ordinalEquals element.Name.LocalName "Project")
        |> Seq.choose (fun element ->
            match element.Attribute (XName.Get "Path") with
            | null -> None
            | attribute when String.IsNullOrWhiteSpace attribute.Value -> None
            | attribute -> Some attribute.Value)
        |> Seq.toList
    with _ ->
        []

/// Reads the project lines of a classic `.sln`. Solution folders share the
/// line format but carry a non-project extension, so the extension filter
/// applied by the caller is what excludes them.
let private parseSln (solutionPath: string) =
    let pattern =
        "^Project\\(\"\\{[^}]*\\}\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]*)\""

    try
        File.ReadAllLines solutionPath
        |> Array.choose (fun line ->
            let m = Regex.Match (line, pattern)

            if m.Success then Some m.Groups.[1].Value else None)
        |> Array.toList
    with _ ->
        []

/// The solution file governing `root`, preferring `.slnx` over `.sln` when a
/// repository carries both during a migration.
let solutionFile (root: string) =
    let find pattern =
        try
            Directory.GetFiles (root, pattern)
            |> Array.sort
            |> Array.tryHead
        with _ ->
            None

    match find "*.slnx" with
    | Some path -> Some path
    | None -> find "*.sln"

/// Every project under `root`, found by walking the tree. Used when there is
/// no solution file to enumerate.
let private byWalking (root: string) =
    let rec walk (dir: string) =
        seq {
            let entries =
                try
                    Directory.GetFiles dir
                with _ ->
                    [||]

            for file in entries do
                if isProjectFile file then
                    yield file

            let subdirectories =
                try
                    Directory.GetDirectories dir
                with _ ->
                    [||]

            for subdirectory in subdirectories do
                let name = Path.GetFileName subdirectory

                if
                    not (
                        skippedDirectories
                        |> List.exists (ordinalEquals name)
                    )
                then
                    yield! walk subdirectory
        }

    walk root
    |> Seq.toList

/// Every project belonging to `root`, taken from its solution file when there
/// is one and from the directory tree otherwise. Sorted by name, and never
/// throws: an unreadable tree yields an empty list.
let discover (root: string) =
    let fromSolution =
        match solutionFile root with
        | None -> None
        | Some path ->
            let entries =
                if ordinalEquals (Path.GetExtension path) ".slnx" then
                    parseSlnx root path
                else
                    parseSln path

            entries
            |> List.map (fun entry -> Path.GetFullPath (Path.Combine (root, entry.Replace ('\\', Path.DirectorySeparatorChar))))
            |> List.filter isProjectFile
            |> Some

    let paths =
        match fromSolution with
        | Some paths -> paths
        | None -> byWalking root

    paths
    |> List.filter File.Exists
    |> List.map (makeRef root)
    |> List.distinctBy (fun reference -> reference.Path)
    |> List.sortBy (fun reference -> reference.RelativePath)

/// The properties given a member of their own on every provided project. The
/// same list is requested in one batch at runtime, so adding to it costs
/// nothing extra per member.
let defaultProperties =
    [ "Version"
      "TargetFramework"
      "TargetFrameworks"
      "AssemblyName"
      "RootNamespace"
      "OutputType"
      "PackageId"
      "IsPackable" ]

/// MSBuild's own default for a project that never declares `<Version>`. Worth
/// naming, because a hint of exactly this is ambiguous - see `propertyDoc`.
[<Literal>]
let DefaultVersion = "1.0.0"

let private propertyElements (path: string) =
    try
        let doc = XDocument.Load (path: string)

        doc.Descendants ()
        |> Seq.filter (fun element ->
            not (isNull element.Parent)
            && ordinalEquals element.Parent.Name.LocalName "PropertyGroup")
        |> Seq.map (fun element -> element.Name.LocalName, element.Value.Trim ())
        |> Seq.toList
    with _ ->
        []

/// The `Directory.Build.props` files applying to `projectDirectory`, outermost
/// first, so nearer files overwrite further ones.
let private buildPropsChain (root: string) (projectDirectory: string) =
    let root = Path.GetFullPath root

    let rec climb (dir: DirectoryInfo) acc =
        if isNull (box dir) then
            acc
        else
            let candidate = Path.Combine (dir.FullName, "Directory.Build.props")

            let acc =
                if File.Exists candidate then
                    candidate
                    :: acc
                else
                    acc

            if ordinalEquals dir.FullName root then
                acc
            else
                climb dir.Parent acc

    try
        climb (DirectoryInfo projectDirectory) []
    with _ ->
        []

let private variablePattern =
    Regex (@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.Compiled)

/// Substitutes `$(Name)` references against the properties already collected.
/// Bounded, because properties can refer to each other in a cycle. Unknown
/// names are left as written rather than blanked, so a hint that could not be
/// resolved reads as obviously unresolved.
let private expand (lookup: Map<string, string>) (value: string) =
    let rec go depth (value: string) =
        if
            depth > 5
            || not (value.Contains "$(")
        then
            value
        else
            let replaced =
                variablePattern.Replace (
                    value,
                    fun m ->
                        match lookup.TryFind m.Groups.[1].Value with
                        | Some found when
                            found
                            <> value
                            ->
                            found
                        | _ -> m.Value
                )

            if replaced = value then value else go (depth + 1) replaced

    go 0 value

/// Best-effort property values for a project, read from XML with no condition
/// evaluation: the last assignment of a name wins, whatever guarded it. Good
/// enough for a doc comment and for nothing else.
let hints (root: string) (reference: ProjectRef) =
    let collected =
        [ for propsFile in buildPropsChain root reference.Directory do
              yield! propertyElements propsFile
          yield! propertyElements reference.Path ]

    let table =
        collected
        |> List.fold (fun table (name, value) -> Map.add name value table) Map.empty

    table
    |> Map.map (fun _ value -> expand table value)

/// The hint shown for `name`, or "" when the project does not declare it.
/// `Version` falls back to MSBuild's default rather than to "", because that
/// is what a runtime read will report.
let hint (table: Map<string, string>) (name: string) =
    match table.TryFind name with
    | Some value when
        value
        <> ""
        ->
        value
    | _ when name = "Version" -> DefaultVersion
    | _ -> ""

/// Minimal reader for the flat `{"Properties":{"Name":"value"}}` object that
/// `dotnet msbuild -getProperty` emits. A dependency-free scanner rather than
/// a JSON library, because the designer assembly targets netstandard2.0 and
/// carries no runtime dependencies.
module Json =

    let private readString (text: string) (index: int byref) =
        let sb = StringBuilder ()
        index <- index + 1 // opening quote
        let mutable finished = false

        while not finished
              && index < text.Length do
            match text.[index] with
            | '"' ->
                index <- index + 1
                finished <- true
            | '\\' when index + 1 < text.Length ->
                let escape = text.[index + 1]
                index <- index + 2

                match escape with
                | 'n' ->
                    sb.Append '\n'
                    |> ignore
                | 'r' ->
                    sb.Append '\r'
                    |> ignore
                | 't' ->
                    sb.Append '\t'
                    |> ignore
                | 'b' ->
                    sb.Append '\b'
                    |> ignore
                | 'f' ->
                    sb.Append '\f'
                    |> ignore
                | 'u' when
                    index + 4
                    <= text.Length
                    ->
                    let code = text.Substring (index, 4)
                    index <- index + 4

                    match Int32.TryParse (code, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture) with
                    | true, value ->
                        sb.Append (char value)
                        |> ignore
                    | _ -> ()
                | other ->
                    sb.Append other
                    |> ignore
            | c ->
                sb.Append c
                |> ignore

                index <- index + 1

        sb.ToString ()

    /// Reads the flat string-to-string object stored under `name`. Returns []
    /// for anything it does not understand, including an absent key.
    let readFlatObject (name: string) (text: string) =
        let key =
            "\""
            + name
            + "\""

        let start = text.IndexOf (key, StringComparison.Ordinal)

        if start < 0 then
            []
        else
            let mutable index =
                text.IndexOf (
                    '{',
                    start
                    + key.Length
                )

            if index < 0 then
                []
            else
                index <- index + 1
                let results = ResizeArray ()
                let mutable finished = false

                while not finished
                      && index < text.Length do
                    match text.[index] with
                    | '}' -> finished <- true
                    | c when
                        Char.IsWhiteSpace c
                        || c = ','
                        ->
                        index <- index + 1
                    | '"' ->
                        let name = readString text &index

                        while index < text.Length
                              && (Char.IsWhiteSpace text.[index]
                                  || text.[index] = ':') do
                            index <- index + 1

                        if
                            index < text.Length
                            && text.[index] = '"'
                        then
                            results.Add (name, readString text &index)
                        else
                            // A non-string value; this reader only handles the
                            // flat shape, so stop rather than guess.
                            finished <- true
                    | _ -> finished <- true

                List.ofSeq results

/// Functions invoked from erased quotations - they run in the *consuming*
/// program, not in the compiler, and must never throw.
module Runtime =

    /// Evaluating a project cold means loading the SDK targets, which is far
    /// slower than the warm case. Generous, because the alternative to waiting
    /// is silently reporting "".
    let private defaultTimeoutMs = 30000

    /// One entry per project, holding every property read so far. Batching the
    /// default set on first access keeps this to a single process per project
    /// for typical use.
    let private cache = ConcurrentDictionary<string, Map<string, string>> ()

    /// Runs `dotnet msbuild -getProperty:...` for `names`. Returns an empty
    /// map when the project cannot be evaluated, `dotnet` is missing, or the
    /// evaluation times out.
    let readProperties (projectPath: string) (names: string list) =
        if
            List.isEmpty names
            || not (File.Exists projectPath)
        then
            Map.empty
        else
            let workingDirectory =
                try
                    Path.GetDirectoryName projectPath
                with _ ->
                    "."

            let args =
                names
                |> List.map (fun name ->
                    "-getProperty:"
                    + name)
                |> String.concat " "

            let quoted =
                "\""
                + projectPath
                + "\""

            match
                Proc.tryOutputWith
                    []
                    defaultTimeoutMs
                    workingDirectory
                    "dotnet"
                    ("msbuild "
                     + quoted
                     + " -nologo "
                     + args)
            with
            | None -> Map.empty
            | Some output ->
                Json.readFlatObject "Properties" output
                |> List.fold (fun table (name, value) -> Map.add name value table) Map.empty

    /// The value of a single property. The first read of any project also
    /// fetches `defaultProperties`, so the members that share that batch cost
    /// one process between them.
    let property (projectPath: string) (name: string) =
        let known =
            match cache.TryGetValue projectPath with
            | true, table -> table
            | _ -> Map.empty

        match known.TryFind name with
        | Some value -> value
        | None ->
            let wanted =
                name
                :: defaultProperties
                |> List.distinct

            let fetched = readProperties projectPath wanted

            // Record the miss as "" too, so an undeclared property does not
            // re-run MSBuild on every access.
            let merged =
                wanted
                |> List.fold
                    (fun table key ->
                        match fetched.TryFind key with
                        | Some value -> Map.add key value table
                        | None -> Map.add key "" table)
                    known

            cache.[projectPath] <- merged

            merged.TryFind name
            |> Option.defaultValue ""

    /// Drops everything read for `projectPath`, for a build that changes a
    /// project and then wants to read it back.
    let invalidate (projectPath: string) =
        cache.TryRemove projectPath
        |> ignore

    /// The argument list for `dotnet <verb> <project>`, with `extra` appended.
    /// `run` is special-cased: it takes its project behind `--project`.
    let command (verb: string) (projectPath: string) (extra: string list) =
        let extra = if isNull (box extra) then [] else extra

        if verb = "run" then
            [ "run"; "--project"; projectPath ]
            @ extra
        else
            [ verb; projectPath ]
            @ extra

    /// Whether a usable `dotnet` is on PATH in the consuming environment.
    let isAvailable () =
        Proc.exists "dotnet" "--version"
