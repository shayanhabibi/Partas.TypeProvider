module Partas.TypeProvider.Tests.Say

open System.IO
open Expecto
open Partas.TypeProvider
open Partas.TypeProviders

/// The test project lives inside this repository, so the provider resolves
/// against it.
type Root = MyTypeProvider<"">

/// Builds a `.git` directory out of plain files, so the design-time reader is
/// exercised without needing the git binary.
let private fixtureRepo () =
    let root = Path.Combine(Path.GetTempPath(), "partas-tp-" + Path.GetRandomFileName())
    let git = Path.Combine(root, ".git")
    Directory.CreateDirectory(Path.Combine(git, "refs", "heads", "feature")) |> ignore
    Directory.CreateDirectory(Path.Combine(git, "refs", "tags")) |> ignore

    File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n")
    File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), "1111111111111111111111111111111111111111\n")

    File.WriteAllText(
        Path.Combine(git, "refs", "heads", "feature", "nested"),
        "2222222222222222222222222222222222222222\n"
    )

    // `packed-refs` carries a tag plus a branch that is also loose; the loose
    // one must win.
    File.WriteAllText(
        Path.Combine(git, "packed-refs"),
        "# pack-refs with: peeled fully-peeled sorted\n"
        + "9999999999999999999999999999999999999999 refs/heads/main\n"
        + "3333333333333333333333333333333333333333 refs/tags/v1.0.0\n"
        + "^4444444444444444444444444444444444444444\n"
        + "5555555555555555555555555555555555555555 refs/remotes/origin/main\n"
    )

    File.WriteAllText(
        Path.Combine(git, "config"),
        "[core]\n\trepositoryformatversion = 0\n"
        + "[remote \"origin\"]\n"
        + "\turl = https://example.com/repo.git ; trailing comment\n"
        + "\tfetch = +refs/heads/*:refs/remotes/origin/*\n"
        + "[remote \"fork\"]\n"
        + "\turl = https://example.com/fetch.git\n"
        + "\tpushurl = https://example.com/push.git\n"
        + "[branch \"main\"]\n"
        + "\tremote = origin\n"
        + "\tmerge = refs/heads/main\n"
    )

    File.WriteAllText(
        Path.Combine(root, ".gitmodules"),
        "[submodule \"vendor/lib\"]\n\tpath = vendor/lib\n\turl = https://example.com/lib.git\n\tbranch = release\n"
    )

    root

let private withFixture f =
    let root = fixtureRepo ()

    try
        f (Git.discover root |> Option.get)
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

[<Tests>]
let configTests =
    testList "Git.Config" [
        test "reads sections, quoted subsections and keys" {
            let entries =
                Git.Config.parse "[remote \"origin\"]\n\turl = https://example.com/r.git\n"

            Expect.equal entries.Length 1 "one entry"
            Expect.equal entries.[0].Section "remote" "section"
            Expect.equal entries.[0].Subsection "origin" "subsection"
            Expect.equal entries.[0].Key "url" "key"
            Expect.equal entries.[0].Value "https://example.com/r.git" "value"
        }

        test "strips inline comments outside quotes but keeps them inside" {
            let entries = Git.Config.parse "[a]\n\tx = one ; comment\n\ty = \"two ; kept\"\n"
            Expect.equal entries.[0].Value "one" "comment stripped"
            Expect.equal entries.[1].Value "two ; kept" "comment inside quotes kept"
        }

        test "honours backslash escapes" {
            let entries = Git.Config.parse "[a]\n\tx = \"c:\\\\dir\\tsep\"\n"
            Expect.equal entries.[0].Value "c:\\dir\tsep" "escapes decoded"
        }

        test "ignores comment and blank lines" {
            let entries = Git.Config.parse "# lead\n\n[a]\n; note\n\tx = 1\n"
            Expect.equal entries.Length 1 "only the assignment"
        }
    ]

[<Tests>]
let discoveryTests =
    testList "Git.discover" [
        test "finds the working tree from a nested directory" {
            withFixture (fun layout ->
                let nested = Path.Combine(layout.WorkTree, "a", "b")
                Directory.CreateDirectory nested |> ignore
                let found = Git.discover nested
                Expect.isSome found "discovered from a subdirectory"
                Expect.equal found.Value.WorkTree layout.WorkTree "same working tree")
        }

        test "returns None outside a repository" {
            let dir = Path.Combine(Path.GetTempPath(), "partas-tp-none-" + Path.GetRandomFileName())
            Directory.CreateDirectory dir |> ignore

            try
                Expect.isNone (Git.discover dir) "no repository above a fresh temp directory"
            finally
                Directory.Delete(dir, true)
        }

        test "CommonDir equals GitDir without a commondir marker" {
            withFixture (fun layout -> Expect.equal layout.CommonDir layout.GitDir "no linked worktree")
        }
    ]

[<Tests>]
let refTests =
    testList "Git.refsUnder" [
        test "merges loose and packed refs" {
            withFixture (fun layout ->
                let names = Git.refsUnder layout "refs/heads" |> List.map (fun r -> r.Name)
                Expect.equal names [ "feature/nested"; "main" ] "loose nested branch and main")
        }

        test "loose refs shadow packed refs of the same name" {
            withFixture (fun layout ->
                let main = Git.refsUnder layout "refs/heads" |> List.find (fun r -> r.Name = "main")

                Expect.equal
                    main.Target
                    "1111111111111111111111111111111111111111"
                    "loose value wins over packed")
        }

        test "skips peel lines when reading tags" {
            withFixture (fun layout ->
                let tags = Git.refsUnder layout "refs/tags"
                Expect.equal (tags |> List.map (fun r -> r.Name)) [ "v1.0.0" ] "one tag"

                Expect.equal
                    tags.Head.Target
                    "3333333333333333333333333333333333333333"
                    "tag object, not the peeled target")
        }

        test "builds fully qualified names" {
            withFixture (fun layout ->
                let remote = Git.refsUnder layout "refs/remotes" |> List.exactlyOne
                Expect.equal remote.Name "origin/main" "short name"
                Expect.equal remote.FullName "refs/remotes/origin/main" "full name")
        }
    ]

[<Tests>]
let configReadingTests =
    testList "Git config readers" [
        test "reads remotes, defaulting PushUrl to the fetch url" {
            withFixture (fun layout ->
                let remotes = Git.remotes layout
                Expect.equal (remotes |> List.map (fun r -> r.Name)) [ "fork"; "origin" ] "sorted by name"

                let origin = remotes |> List.find (fun r -> r.Name = "origin")
                Expect.equal origin.PushUrl origin.FetchUrl "falls back to fetch url"

                let fork = remotes |> List.find (fun r -> r.Name = "fork")
                Expect.equal fork.PushUrl "https://example.com/push.git" "explicit pushurl")
        }

        test "reads submodules from .gitmodules" {
            withFixture (fun layout ->
                let sub = Git.submodules layout |> List.exactlyOne
                Expect.equal sub.Name "vendor/lib" "name"
                Expect.equal sub.Path "vendor/lib" "path"
                Expect.equal sub.Branch "release" "branch")
        }

        test "resolves the configured upstream" {
            withFixture (fun layout ->
                let config = Git.repoConfig layout
                Expect.equal (Git.upstreamOf config "main") "origin/main" "shorthand upstream"
                Expect.equal (Git.upstreamOf config "feature/nested") "" "no upstream configured")
        }
    ]

[<Tests>]
let runtimeTests =
    testList "Git.Runtime" [
        test "resolves a loose ref" {
            withFixture (fun layout ->
                Expect.equal
                    (Git.Runtime.resolveRef layout.CommonDir "refs/heads/main")
                    "1111111111111111111111111111111111111111"
                    "loose ref")
        }

        test "falls back to packed-refs" {
            withFixture (fun layout ->
                Expect.equal
                    (Git.Runtime.resolveRef layout.CommonDir "refs/tags/v1.0.0")
                    "3333333333333333333333333333333333333333"
                    "packed ref")
        }

        test "returns empty for a missing ref" {
            withFixture (fun layout ->
                Expect.equal (Git.Runtime.resolveRef layout.CommonDir "refs/heads/gone") "" "no such ref")
        }

        test "reads HEAD" {
            withFixture (fun layout ->
                Expect.equal (Git.Runtime.headBranch layout.GitDir) "main" "branch name"
                Expect.isFalse (Git.Runtime.isDetached layout.GitDir) "not detached"

                Expect.equal
                    (Git.Runtime.headSha layout.GitDir layout.CommonDir)
                    "1111111111111111111111111111111111111111"
                    "head sha")
        }

        test "reports a detached HEAD" {
            withFixture (fun layout ->
                let sha = "8888888888888888888888888888888888888888"
                File.WriteAllText(Path.Combine(layout.GitDir, "HEAD"), sha + "\n")
                Expect.isTrue (Git.Runtime.isDetached layout.GitDir) "detached"
                Expect.equal (Git.Runtime.headBranch layout.GitDir) "" "no branch"
                Expect.equal (Git.Runtime.headSha layout.GitDir layout.CommonDir) sha "sha read directly")
        }

        test "reports an unborn HEAD as empty rather than throwing" {
            withFixture (fun layout ->
                File.Delete(Path.Combine(layout.GitDir, "refs", "heads", "main"))

                File.WriteAllText(
                    Path.Combine(layout.GitDir, "packed-refs"),
                    "# pack-refs with: peeled fully-peeled sorted\n"
                )

                Expect.equal (Git.Runtime.headSha layout.GitDir layout.CommonDir) "" "no commit yet"
                Expect.equal (Git.Runtime.headBranch layout.GitDir) "main" "branch name still known")
        }

        test "a failing command yields an empty string" {
            withFixture (fun layout ->
                Expect.equal (Git.Runtime.exec layout.WorkTree "not-a-real-subcommand") "" "no output")
        }
    ]

[<Tests>]
let providerTests =
    testList "GitProvider" [
        test "provides the repository this test lives in" {
            Expect.isTrue Root.GitProvider.IsRepository "found the repository"

            Expect.equal
                (Path.GetFullPath Root.GitProvider.WorkingDirectory)
                (Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..")))
                "repository root"
        }

        test "exposes runtime members without throwing" {
            Root.GitProvider.Head |> ignore
            Root.GitProvider.HeadBranch |> ignore
            Root.GitProvider.IsDetached |> ignore
            Root.GitProvider.IsDirty() |> ignore
            Expect.isTrue (Root.GitProvider.IsGitAvailable()) "git is on PATH in this environment"
        }
    ]
