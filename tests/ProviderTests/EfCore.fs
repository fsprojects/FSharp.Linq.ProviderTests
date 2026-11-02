namespace FSharp.Linq.ProviderTests

open System
open System.Collections
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Diagnostics
open FSharp.Core.UnitTests.Linq

/// One instance of each interceptor serves every context. EF Core keeps a service provider for each set of
/// interceptors it sees, and warns when it has created too many of them.
module Recorders =

    /// Records the commands of a database that runs them.
    let executing = CommandRecorder(false)

    /// Records the first command and stops: the database is not there.
    let first = CommandRecorder(true)

    let suppressConnection = ConnectionSuppressor()

/// The data of the scenarios in an in-memory SQLite database, which lasts as long as its connection. EF Core fills it;
/// linq2db reads it too.
type SqliteShop() =
    let connection = new SqliteConnection "DataSource=:memory:"
    do connection.Open()

    let options =
        DbContextOptionsBuilder<ShopContext>()
            .UseSqlite(connection)
            .AddInterceptors(Recorders.executing :> IInterceptor)
            .Options

    do Shop.seed options

    member _.Connection = connection
    member _.Options = options

    interface IDisposable with
        member _.Dispose() = connection.Dispose()

module EfCore =

    let private sqlOf (commands: seq<string>) =
        commands |> Seq.map Text.normalizeSql |> String.concat " ;; "

    let private verdictOf (scenario: Scenario) (actual: obj list) =
        if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong

    /// Runs the scenario on SQLite and compares the rows with the expected ones.
    let sqlite (shop: SqliteShop) (scenario: Scenario) : Status * string =
        let commands = CommandRecorder.Start()
        use context = new ShopContext(shop.Options)

        try
            let actual =
                match scenario.Run(Shop.tables context) with
                | Outcome.Rows rows -> [ for row in rows -> row ]
                | Outcome.Value value -> [ value ]

            verdictOf scenario actual, sqlOf commands
        with error ->
            Runner.failed error

    let private toListAsync =
        typeof<EntityFrameworkQueryableExtensions>.GetMethods()
        |> Array.find (fun method -> method.Name = "ToListAsync" && method.GetParameters().Length = 2)

    /// The rows of a query read the way a service that awaits the query reads them.
    let private readAsync (rows: IQueryable) : obj list =
        let task =
            toListAsync
                .MakeGenericMethod(rows.ElementType)
                .Invoke(null, [| box rows; box CancellationToken.None |])
            :?> Task

        task.Wait()
        let items = task.GetType().GetProperty("Result").GetValue task :?> IEnumerable
        [ for item in items -> item ]

    /// Runs the scenario on SQLite with <c>ToListAsync</c>. A scenario that computes a value has run when the
    /// provider is done, so there is nothing left to await.
    let sqliteAsync (shop: SqliteShop) (scenario: Scenario) : Status * string =
        let commands = CommandRecorder.Start()
        use context = new ShopContext(shop.Options)

        try
            match scenario.Run(Shop.tables context) with
            | Outcome.Rows rows ->
                let actual = readAsync rows
                verdictOf scenario actual, sqlOf commands
            | Outcome.Value _ -> Status.NotApplicable, ""
        with error ->
            Runner.failed error

    let sqlServerOptions =
        DbContextOptionsBuilder<ShopContext>()
            .UseSqlServer("Server=localhost;Database=shop;User Id=unused;Password=unused;TrustServerCertificate=True")
            .AddInterceptors(Recorders.suppressConnection :> IInterceptor, Recorders.first :> IInterceptor)
            .Options

    let postgreSqlOptions =
        DbContextOptionsBuilder<ShopContext>()
            .UseNpgsql("Host=localhost;Database=shop;Username=unused;Password=unused")
            .AddInterceptors(Recorders.suppressConnection :> IInterceptor, Recorders.first :> IInterceptor)
            .Options

    let rec private capturedSql (error: exn) : string voption =
        match error with
        | null -> ValueNone
        | CommandCaptured sql -> ValueSome sql
        | error -> capturedSql error.InnerException

    /// Asks the provider for the SQL of the first command of the scenario. There is no server: the command is
    /// stopped before it is sent, so the rows cannot be compared.
    let sqlOnly (options: DbContextOptions<ShopContext>) (scenario: Scenario) : Status * string =
        CommandRecorder.Start() |> ignore
        use context = new ShopContext(options)

        try
            match scenario.Run(Shop.tables context) with
            | Outcome.Rows rows ->
                for _ in rows do
                    ()
            | Outcome.Value _ -> ()

            Status.Fail "NoCommand", "No command was sent."
        with error ->
            match capturedSql error with
            | ValueSome sql -> Status.Ok, Text.normalizeSql sql
            | ValueNone -> Runner.failed error
