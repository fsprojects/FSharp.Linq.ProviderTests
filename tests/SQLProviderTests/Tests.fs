namespace FSharp.Linq.ProviderTests.SQLProviderTests

open System
open System.Collections.Generic
open System.Threading
open FSharp.Data.Sql
open FSharp.Core.UnitTests.Linq
open FSharp.Linq.ProviderTests
open Xunit

/// The SQL that SQLProvider sends while a case runs. The provider announces every query in one static event.
module Captured =

    let private current = new AsyncLocal<List<string>>()

    let private subscribed =
        lazy
            (Common.QueryEvents.SqlQueryEvent.Add(fun data ->
                match current.Value with
                | null -> ()
                | queries -> lock queries (fun () -> queries.Add data.Command)))

    /// Starts collecting on this flow of execution.
    let start () : List<string> =
        subscribed.Force()
        let queries = List<string>()
        current.Value <- queries
        queries

module Cases =

    let execute (case: Case) : Status * string =
        let queries = Captured.start ()

        try
            let actual = case.Run(Database.context ())
            let sql = lock queries (fun () -> queries |> Seq.map Text.normalizeSql |> String.concat " ;; ")

            (if Runner.sameRowsAs case.Ordered (case.Expected(Data())) actual then
                 Status.Ok
             else
                 Status.Wrong),
            sql
        with error ->
            Runner.failed error

    let run () : Observation list =
        [ for case in Queries.cases ->
              let status, detail = Runner.guarded (fun () -> execute case)

              { Section = case.Section
                Name = case.Name
                Status = status
                Detail = detail } ]

module SQLProviderTests =

    [<Fact>]
    let ``SQLProvider SQLite`` () =
        let observations = Cases.run ()
        let verdict = Baseline.verify "sqlprovider-sqlite" false observations

        match TestContext.Current.TestOutputHelper with
        | null -> ()
        | output -> output.WriteLine verdict.Report

        Assert.True(verdict.Passed, verdict.Report)
