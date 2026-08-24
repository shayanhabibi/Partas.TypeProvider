/// Running of external command line tools.
///
/// Shared by the providers that answer questions the compiler cannot answer
/// alone - git state, MSBuild evaluation - without taking a library dependency
/// on the tool. Nothing here throws: a missing executable, a timeout or a
/// crash all surface as `None`, because a provider that throws takes the whole
/// provided type down with it.
module Partas.TypeProvider.Proc

open System
open System.Diagnostics

/// The outcome of a process that started and exited. A non-zero `ExitCode` is
/// reported rather than swallowed, so callers can decide whether it means "no
/// answer" or "an answer, with complaints on stderr".
type Result =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

/// How long to wait for the pipes to finish once the process itself has
/// exited. The data is already buffered by then, so this only guards against
/// a reader task that never completes.
let private drainTimeoutMs = 500

/// Runs `executable` and waits up to `timeoutMs` for it to exit, returning
/// `None` if it cannot be started, does not exit in time, or throws on the
/// way. `environment` is applied on top of the inherited environment.
///
/// stdin is redirected and closed immediately: a tool that decides to prompt
/// gets EOF instead of blocking forever on a console that is not there.
let tryRunWith (environment: (string * string) list) (timeoutMs: int) (workingDirectory: string) (executable: string) (arguments: string) =
    try
        let psi = ProcessStartInfo (executable, arguments)
        psi.WorkingDirectory <- workingDirectory
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true

        for key, value in environment do
            psi.Environment.[key] <- value

        use proc = new Process (StartInfo = psi)

        if not (proc.Start ()) then
            None
        else
            // Drain both pipes concurrently; a full pipe buffer would
            // otherwise deadlock against WaitForExit.
            let stdout = proc.StandardOutput.ReadToEndAsync ()
            let stderr = proc.StandardError.ReadToEndAsync ()
            proc.StandardInput.Close ()

            if not (proc.WaitForExit timeoutMs) then
                (try
                    proc.Kill ()
                 with _ ->
                     ())

                None
            elif
                stdout.Wait drainTimeoutMs
                && stderr.Wait drainTimeoutMs
            then
                Some
                    { ExitCode = proc.ExitCode
                      StandardOutput = stdout.Result
                      StandardError = stderr.Result }
            else
                None
    with _ ->
        None

/// `tryRunWith` with an unmodified environment.
let tryRun (timeoutMs: int) (workingDirectory: string) (executable: string) (arguments: string) =
    tryRunWith [] timeoutMs workingDirectory executable arguments

/// Trimmed stdout of a successful run. `None` covers a non-zero exit as well
/// as the failures `tryRunWith` reports, for callers that treat both as
/// "no answer".
let tryOutputWith (environment: (string * string) list) (timeoutMs: int) (workingDirectory: string) (executable: string) (arguments: string) =
    tryRunWith environment timeoutMs workingDirectory executable arguments
    |> Option.bind (fun result ->
        if result.ExitCode = 0 then
            Some (result.StandardOutput.Trim ())
        else
            None)

/// Whether `executable` can be started at all, used to gate members whose
/// value depends on a tool being installed.
let exists (executable: string) (probeArguments: string) =
    tryRunWith [] 2000 (IO.Path.GetTempPath ()) executable probeArguments
    |> Option.isSome
