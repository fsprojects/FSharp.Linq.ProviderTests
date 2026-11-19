namespace FSharp.Linq.ProviderTests

open System
open System.Net.Http
open Microsoft.Azure.Cosmos
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Diagnostics
open Microsoft.EntityFrameworkCore.Infrastructure
open FSharp.Core.UnitTests.Linq

/// The entities as EF Core's Cosmos provider maps them: a container for each class, as in the test of the Cosmos
/// SDK, and no discriminator, because a container holds one class only.
type CosmosShopContext(options: DbContextOptions<CosmosShopContext>) =
    inherit DbContext(options)

    override _.OnModelCreating model =
        model.Entity<Customer>().ToContainer("Customers").HasNoDiscriminator() |> ignore
        model.Entity<Order>().ToContainer("Orders").HasNoDiscriminator() |> ignore
        model.Entity<OrderLine>().ToContainer("Lines").HasNoDiscriminator() |> ignore
        model.Entity<Product>().ToContainer("Products").HasNoDiscriminator() |> ignore

/// EF Core's Cosmos provider (Microsoft.EntityFrameworkCore.Cosmos), which builds its own SQL for Cosmos DB from the
/// expression tree: another translator than the LINQ provider of the Cosmos SDK that <c>Cosmos</c> checks.
module EfCoreCosmos =

    /// The tables of a context as the roots of the scenarios.
    let tables (context: CosmosShopContext) : Db =
        { Customers = context.Set<Customer>()
          Orders = context.Set<Order>()
          Lines = context.Set<OrderLine>()
          Products = context.Set<Product>() }

    // The provider refuses synchronous I/O unless this warning is turned off. The scenarios enumerate their rows,
    // as a query expression does, and what is checked is the translation, not the style of the calls
    let private options (endpoint: string) (key: string) (database: string) (handler: unit -> HttpMessageHandler) =
        DbContextOptionsBuilder<CosmosShopContext>()
            .UseCosmos(
                endpoint,
                key,
                database,
                fun cosmos ->
                    cosmos
                        .ConnectionMode(ConnectionMode.Gateway)
                        .LimitToEndpoint()
                        .HttpClientFactory(fun () -> new HttpClient(handler (), true))
                    |> ignore
            )
            .ConfigureWarnings(fun warnings -> warnings.Ignore(CosmosEventId.SyncNotSupported) |> ignore)
            .Options

    let private offlineOptions =
        lazy (options "https://localhost:8081" CosmosEmulator.DefaultKey "shop" (fun () -> new Offline() :> HttpMessageHandler))

    /// Asks the provider for the SQL of the scenario, without a server. A scenario that computes a value has to send
    /// the query to produce the value, so there is nothing to ask for.
    let offline (scenario: Scenario) : Status * string =
        use context = new CosmosShopContext(offlineOptions.Value)

        try
            match scenario.Run(tables context) with
            | Outcome.Rows rows -> Status.Ok, Text.normalizeSql (rows.ToQueryString())
            | Outcome.Value _ -> Status.NotApplicable, ""
        with
        | error when Cosmos.mentionsOffline error -> Status.NotApplicable, ""
        | error -> Runner.failed error

    let private execute (databaseId: string) (options: DbContextOptions<CosmosShopContext>) (scenario: Scenario) =
        let queries = QueryRecorder.Start()
        use context = new CosmosShopContext(options)

        let hideDatabase (text: string) =
            text.Replace(databaseId, "<database>", StringComparison.Ordinal)

        try
            let actual, sql =
                match scenario.Run(tables context) with
                | Outcome.Rows rows ->
                    let sql = Text.normalizeSql (rows.ToQueryString())
                    [ for row in rows -> row ], sql
                | Outcome.Value value ->
                    let sent = lock queries (fun () -> queries |> Seq.map Text.normalizeSql |> String.concat " ;; ")
                    [ value ], sent

            (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong), hideDatabase sql
        with error ->
            let status, detail = Runner.failed error
            status, hideDatabase detail

    /// Runs every scenario on the emulator, in a database that this run creates and deletes afterwards. The name
    /// starts as the names of the databases of the Cosmos SDK test do, so a run that stopped half way is swept up.
    let run () : Observation list =
        let databaseId = $"fsharp-linq-efcore-{Guid.NewGuid()}"

        let options =
            options CosmosEmulator.endpoint CosmosEmulator.key databaseId (fun () ->
                new QueryRecorder(CosmosEmulator.createHandler ()) :> HttpMessageHandler)

        use context = new CosmosShopContext(options)

        try
            context.Database.EnsureCreated() |> ignore
            let data = Data()
            context.AddRange(data.Customers |> Seq.cast<obj>)
            context.AddRange(data.Products |> Seq.cast<obj>)
            context.SaveChanges() |> ignore
            Runner.run (execute databaseId options)
        finally
            try
                context.Database.EnsureDeleted() |> ignore
            with _ ->
                ()
