/// Running every scenario with one provider.
module FSharp.Linq.ProviderTests.Runner

open System
open System.Reflection
open System.Threading.Tasks
open FSharp.Core.UnitTests.Linq

/// The exception that the failure really is: reflection and tasks wrap the one that was thrown.
let rec rootCause (error: exn) : exn =
    match error with
    | :? TargetInvocationException
    | :? AggregateException ->
        match error.InnerException with
        | null -> error
        | inner -> rootCause inner
    | _ -> error

/// A scenario that threw: the type of the failure and its message.
let failed (error: exn) : Status * string =
    let cause = rootCause error
    Status.Fail(cause.GetType().Name), Text.message cause.Message

/// Whether the rows are the expected ones: in any order, unless the order belongs to the query.
let sameRowsAs (ordered: bool) (expected: obj list) (actual: obj list) : bool =
    let inOrder (rows: obj list) =
        let rendered = List.map Scenario.render rows
        if ordered then rendered else List.sort rendered

    inOrder expected = inOrder actual

/// Whether the rows are the ones that the scenario expects of the data it was run over.
let sameRows (scenario: Scenario) (data: Data) (actual: obj list) : bool =
    sameRowsAs scenario.Ordered (scenario.Expected data) actual

/// What one run of a scenario gives: the status and the detail of the line in the baseline.
type Execute = Scenario -> Status * string

/// How long a scenario may take. A query that never returns is a failure, not a hang of the whole run.
let timeout = TimeSpan.FromSeconds 120.0

/// Runs the work on another thread and gives up on it after <see cref="timeout"/>.
let guarded (work: unit -> Status * string) : Status * string =
    let task = Task.Run<Status * string>(Func<Status * string>(work))

    try
        if task.Wait timeout then
            task.Result
        else
            Status.Fail(nameof TimeoutException), $"The scenario did not finish in {timeout}."
    with error ->
        failed error

/// Runs every scenario of every section with the provider.
let run (execute: Execute) : Observation list =
    [ for section, scenarios in AllScenarios.sections () do
          for scenario in scenarios do
              let status, detail = guarded (fun () -> execute scenario)

              { Section = section
                Name = scenario.Name
                Status = status
                Detail = detail } ]
