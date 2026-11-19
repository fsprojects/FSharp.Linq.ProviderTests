namespace FSharp.Linq.ProviderTests

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open Microsoft.EntityFrameworkCore
open MongoDB.Bson
open MongoDB.Bson.Serialization
open MongoDB.Bson.Serialization.Serializers
open MongoDB.Driver
open MongoDB.Driver.Core.Configuration
open MongoDB.Driver.Core.Events
open MongoDB.EntityFrameworkCore.Extensions
open FSharp.Core.UnitTests.Linq

/// Collects the commands that the MongoDB driver sends while a scenario runs, without what names the session or
/// the connection. Start it on the flow of execution of the scenario.
type MongoCommandRecorder private () =

    static let current = new AsyncLocal<List<string>>()

    static let queries = set [ "aggregate"; "find"; "count"; "distinct" ]

    static let ignored =
        set [ "lsid"; "$db"; "$clusterTime"; "$readPreference"; "cursor"; "apiVersion"; "txnNumber" ]

    /// Starts collecting on this flow of execution.
    static member Start() : List<string> =
        let commands = List<string>()
        current.Value <- commands
        commands

    /// Remembers a command that reads, if collecting has started on this flow of execution.
    static member Record(started: CommandStartedEvent) =
        match current.Value with
        | null -> ()
        | commands when queries.Contains started.CommandName ->
            let command = BsonDocument()

            for element in started.Command do
                if not (ignored.Contains element.Name) then
                    command.Add element |> ignore

            lock commands (fun () -> commands.Add(command.ToString()))
        | _ -> ()

    /// The commands collected, as a line of a baseline.
    static member Text(commands: List<string>) =
        lock commands (fun () -> commands |> Seq.map Text.normalizeSql |> String.concat " ;; ")

/// The LINQ provider of the MongoDB driver. A document holds what the object holds, so a customer holds its orders
/// and an order its lines, and a navigation of a scenario reads the embedded documents.
module Mongo =

    let server =
        { Name = "MongoDB"
          Variable = "MONGODB_CONNECTION_STRING"
          RequiredVariable = "MONGODB_REQUIRED"
          Default = "mongodb://127.0.0.1:27017" }

    // A date of the data has no time zone, and the driver would take it for local time and store it as UTC: the
    // year of a date then depends on the machine. As a date only it is stored as midnight UTC of that day. A class
    // map can be registered only before anything looks the class up, and the EF Core provider looks it up too, so
    // MongoClassMaps registers it before any test runs
    let internal registerClassMaps () =
        BsonClassMap.RegisterClassMap<Customer>(fun (map: BsonClassMap<Customer>) ->
            map.AutoMap()

            map
                .MapMember(fun c -> c.Registered)
                .SetSerializer(NullableSerializer<DateTime>(DateTimeSerializer.DateOnlyInstance))
            |> ignore)
        |> ignore

    /// The settings of a client that records its commands and gives up on a server after the time out.
    let settings (address: string) (timeout: TimeSpan) =
        let settings = MongoClientSettings.FromConnectionString address
        settings.ServerSelectionTimeout <- timeout
        settings.ConnectTimeout <- timeout

        settings.ClusterConfigurator <-
            Action<ClusterBuilder>(fun builder ->
                builder.Subscribe<CommandStartedEvent>(Action<CommandStartedEvent> MongoCommandRecorder.Record)
                |> ignore)

        settings

    /// The roots of the scenarios: a collection for each entity.
    let tables (database: IMongoDatabase) : Db =
        { Customers = database.GetCollection<Customer>("Customers").AsQueryable()
          Orders = database.GetCollection<Order>("Orders").AsQueryable()
          Lines = database.GetCollection<OrderLine>("Lines").AsQueryable()
          Products = database.GetCollection<Product>("Products").AsQueryable() }

    let rec private timedOut (error: exn) =
        match error with
        | null -> false
        | :? TimeoutException -> true
        | error -> timedOut error.InnerException

    // Nothing listens on port 1, and a scenario that computes a value gives up on it at once
    let private offlineClient =
        lazy (new MongoClient(settings "mongodb://127.0.0.1:1" (TimeSpan.FromMilliseconds 100.0)))

    // The text of a query of the driver is its translation, which names the collection, or the message of the
    // failure to translate it: "Expression not supported: ..." or "<exception type>: <message>"
    [<Literal>]
    let private Translated = "shop."

    [<Literal>]
    let private NotSupported = "Expression not supported"

    let private failureOf (text: string) =
        let cause =
            match text.IndexOf ':' with
            | colon when text.StartsWith(NotSupported, StringComparison.Ordinal) -> "ExpressionNotSupportedException"
            | colon when colon > 0 && not (text.Substring(0, colon).Contains ' ') ->
                let name = text.Substring(0, colon)
                name.Substring(name.LastIndexOf '.' + 1)
            | _ -> "Untranslated"

        Status.Fail cause, Text.message text

    /// Asks the driver for the pipeline of the scenario, without a server: the text of the query is its
    /// translation. A scenario that computes a value has to send the query to produce the value, so there is
    /// nothing to ask for. The driver translates a part of a query only when it runs it, for example the
    /// materialization of the rows, so a scenario can pass here and fail on a server.
    let offline (scenario: Scenario) : Status * string =
        let database = offlineClient.Value.GetDatabase "shop"

        try
            match scenario.Run(tables database) with
            | Outcome.Rows rows when rows.Provider.GetType().Namespace.StartsWith("MongoDB", StringComparison.Ordinal) ->
                match string rows with
                | text when text.StartsWith(Translated, StringComparison.Ordinal) -> Status.Ok, Text.normalizeSql text
                | text -> failureOf text
            | Outcome.Rows rows ->
                Status.Fail "LeftProvider", $"The query is not one of the driver any more: {rows.Provider.GetType().Name}"
            | Outcome.Value _ -> Status.NotApplicable, ""
        with
        | error when timedOut error -> Status.NotApplicable, ""
        | error -> Runner.failed error

    let private connect () =
        new MongoClient(settings (Server.address server) (TimeSpan.FromSeconds 5.0))

    /// Whether the server answers.
    let probe () : Result<unit, string> =
        try
            use client = connect ()

            client
                .GetDatabase("admin")
                .RunCommand<BsonDocument>(BsonDocumentCommand<BsonDocument>(BsonDocument("ping", BsonInt32 1)))
            |> ignore

            Ok()
        with error ->
            Error(Text.message (Runner.rootCause error).Message)

    /// Runs the scenario and compares the rows with the expected ones. The name of the database of the run, which
    /// the driver puts into its messages, is hidden.
    let execute (database: string) (db: Db) (scenario: Scenario) : Status * string =
        let commands = MongoCommandRecorder.Start()

        let hideDatabase (text: string) =
            text.Replace(database, "<database>", StringComparison.Ordinal)

        try
            let actual =
                match scenario.Run db with
                | Outcome.Rows rows -> [ for row in rows -> row ]
                | Outcome.Value value -> [ value ]

            (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong),
            MongoCommandRecorder.Text commands
        with error ->
            let status, detail = Runner.failed error
            status, hideDatabase detail

    /// Runs the scenarios against a database that this run creates on the server and drops afterwards. The run
    /// gives the client and the name of the database to the work and returns what it observed.
    let onDatabase (work: MongoClient -> string -> Observation list) : Observation list =
        use client = connect ()
        let name = EfCoreServer.databaseName ()

        try
            work client name
        finally
            client.DropDatabase name

    /// Runs every scenario with the driver.
    let run () : Observation list =
        onDatabase (fun client name ->
            let database = client.GetDatabase name
            let data = Data()
            database.GetCollection<Customer>("Customers").InsertMany data.Customers
            database.GetCollection<Order>("Orders").InsertMany data.Orders
            database.GetCollection<OrderLine>("Lines").InsertMany data.Lines
            database.GetCollection<Product>("Products").InsertMany data.Products
            Runner.run (execute name (tables database)))

/// Registers the class maps of the driver once, before any test runs: see <c>Mongo.registerClassMaps</c>.
type MongoClassMaps() =
    do Mongo.registerClassMaps ()

/// The entities as the EF Core provider of MongoDB maps them: a collection for each class. The provider embeds
/// owned entities only and has no relationships between collections, and every class here is also a root of the
/// scenarios, so the navigations are not mapped.
type MongoShopContext(options: DbContextOptions<MongoShopContext>) =
    inherit DbContext(options)

    override _.OnModelCreating model =
        model.Entity<Customer>().ToCollection("Customers").Ignore("Orders") |> ignore
        model.Entity<Order>().ToCollection("Orders").Ignore("Lines") |> ignore
        model.Entity<OrderLine>().ToCollection("Lines") |> ignore
        model.Entity<Product>().ToCollection("Products") |> ignore

        // A date of the data has no time zone. The provider stores it as UTC, taking it for local time, and reads it
        // back as local time, so that it is the date that was written
        model.Entity<Customer>().Property(fun c -> c.Registered).HasDateTimeKind(DateTimeKind.Local) |> ignore

/// MongoDB.EntityFrameworkCore, which hands the query that EF Core has prepared to the LINQ provider of the driver.
/// It cannot give the text of a query without running it (no <c>ToQueryString</c>), so it is checked on a server
/// only.
module EfCoreMongo =

    let tables (context: MongoShopContext) : Db =
        { Customers = context.Set<Customer>()
          Orders = context.Set<Order>()
          Lines = context.Set<OrderLine>()
          Products = context.Set<Product>() }

    let private optionsFor (client: IMongoClient) (database: string) =
        DbContextOptionsBuilder<MongoShopContext>().UseMongoDB(client, database).Options

    let private execute (database: string) (options: DbContextOptions<MongoShopContext>) (scenario: Scenario) =
        use context = new MongoShopContext(options)
        Mongo.execute database (tables context) scenario

    /// Runs every scenario with the provider. A server that is not a replica set has no transactions, so
    /// SaveChanges must not ask for one.
    let run () : Observation list =
        Mongo.onDatabase (fun client name ->
            let options = optionsFor client name

            do
                use context = new MongoShopContext(options)
                context.Database.AutoTransactionBehavior <- AutoTransactionBehavior.Never
                let data = Data()
                context.AddRange(data.Customers |> Seq.cast<obj>)
                context.AddRange(data.Orders |> Seq.cast<obj>)
                context.AddRange(data.Lines |> Seq.cast<obj>)
                context.AddRange(data.Products |> Seq.cast<obj>)
                context.SaveChanges() |> ignore

            Runner.run (execute name options))
