namespace FSharp.Linq.ProviderTests

open System
open System.Collections
open System.Linq
open System.Threading
open System.Threading.Tasks
open JasperFx
open Marten
open Marten.Linq
open Npgsql
open FSharp.Core.UnitTests.Linq

/// Marten, a document store on PostgreSQL with a LINQ provider of its own. A document is the JSON of the object, so
/// a customer holds its orders and an order its lines, as with MongoDB, and a navigation of a scenario reads the
/// embedded documents.
module Marten =

    /// Uses the PostgreSQL server of the EF Core tests, with its own MARTEN_REQUIRED flag.
    let server =
        { EfCoreServer.postgreSql with
            RequiredVariable = "MARTEN_REQUIRED" }

    let private connection (database: string) =
        NpgsqlConnectionStringBuilder(Server.address server, Database = database).ConnectionString

    /// Whether the server answers.
    let probe () = EfCoreServer.probePostgreSql ()

    let private store (database: string) =
        DocumentStore.For(fun (options: StoreOptions) ->
            options.Connection(connection database)
            options.AutoCreateSchemaObjects <- AutoCreate.All)

    /// The roots of the scenarios: a table of documents for each entity.
    let tables (session: IQuerySession) : Db =
        { Customers = session.Query<Customer>()
          Orders = session.Query<Order>()
          Lines = session.Query<OrderLine>()
          Products = session.Query<Product>() }

    let private toCommand =
        typeof<QueryableExtensions>.GetMethods()
        |> Array.find (fun method -> method.Name = "ToCommand" && method.GetParameters().Length = 2)

    /// The SQL that Marten builds for the query.
    let private sqlOf (rows: IQueryable) : string =
        let command =
            toCommand.MakeGenericMethod(rows.ElementType).Invoke(null, [| box rows; box FetchType.FetchMany |])
            :?> NpgsqlCommand

        command.CommandText

    let private toListAsync =
        typeof<QueryableExtensions>.GetMethods()
        |> Array.find (fun method -> method.Name = "ToListAsync" && method.GetParameters().Length = 2)

    /// The rows of a query, read with <c>ToListAsync</c>: since Marten 9 a query cannot be enumerated
    /// synchronously.
    let private readAsync (rows: IQueryable) : obj list =
        let task =
            toListAsync.MakeGenericMethod(rows.ElementType).Invoke(null, [| box rows; box CancellationToken.None |])
            :?> Task

        task.Wait()
        let items = task.GetType().GetProperty("Result").GetValue task :?> IEnumerable
        [ for item in items -> item ]

    let rec private onlyAsync (error: exn) =
        match error with
        | null -> false
        | :? NotSupportedException when error.Message.Contains("only asynchronous", StringComparison.Ordinal) -> true
        | error -> onlyAsync error.InnerException

    /// Runs the scenario and compares the rows. A scenario that computes a value runs its query synchronously, in
    /// the operator that gives the value, and Marten refuses that: n/a, as with ToListAsync on EF Core.
    let private execute (store: IDocumentStore) (scenario: Scenario) : Status * string =
        use session = store.QuerySession()

        try
            match scenario.Run(tables session) with
            | Outcome.Rows rows ->
                let sql = Text.normalizeSql (sqlOf rows)
                let actual = readAsync rows
                (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong), sql
            | Outcome.Value _ -> Status.NotApplicable, ""
        with
        | error when onlyAsync error -> Status.NotApplicable, ""
        | error -> Runner.failed error

    let private admin (sql: string) =
        use connection = new NpgsqlConnection(connection "postgres")
        connection.Open()
        use command = new NpgsqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    /// Runs every scenario against a database that this run creates on the server and drops afterwards.
    let run () : Observation list =
        let database = EfCoreServer.databaseName ()
        admin $"CREATE DATABASE {database}"

        try
            use store = store database

            do
                use session = store.LightweightSession()
                let data = Data()
                session.Store(Array.ofList data.Customers)
                session.Store(Array.ofList data.Orders)
                session.Store(Array.ofList data.Lines)
                session.Store(Array.ofList data.Products)
                session.SaveChangesAsync().GetAwaiter().GetResult()

            Runner.run (execute store)
        finally
            NpgsqlConnection.ClearAllPools()
            admin $"DROP DATABASE IF EXISTS {database} WITH (FORCE)"
