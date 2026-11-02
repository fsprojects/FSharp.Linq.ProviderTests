/// The Azure Cosmos DB Emulator, which runs the queries that the Cosmos provider builds. Both emulators are
/// supported: the Windows one and the Linux vNext one that runs in Docker. They answer some queries differently, so
/// each has its own baseline.
[<RequireQualifiedAccess>]
module FSharp.Linq.ProviderTests.CosmosEmulator

open System
open System.Net.Http
open System.Net.Security
open Microsoft.Azure.Cosmos
open Newtonsoft.Json.Linq
open FSharp.Core.UnitTests.Linq

/// Overrides <see cref="DefaultEndpoint"/>.
[<Literal>]
let EndpointVariable = "COSMOS_EMULATOR_ENDPOINT"

/// Overrides <see cref="DefaultKey"/>.
[<Literal>]
let KeyVariable = "COSMOS_EMULATOR_KEY"

/// Set to 1 where the emulator has to be there, as on the Linux job of CI: a missing emulator then fails the test
/// instead of skipping it.
[<Literal>]
let RequiredVariable = "COSMOS_EMULATOR_REQUIRED"

[<Literal>]
let DefaultEndpoint = "https://127.0.0.1:8081"

/// The key of the emulator, which is documented and the same everywhere.
[<Literal>]
let DefaultKey =
    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="

let private readVariable (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null -> ValueNone
    | value when String.IsNullOrWhiteSpace value -> ValueNone
    | value -> ValueSome(value.Trim())

let endpoint = readVariable EndpointVariable |> ValueOption.defaultValue DefaultEndpoint

let key = readVariable KeyVariable |> ValueOption.defaultValue DefaultKey

let isRequired () =
    readVariable RequiredVariable |> ValueOption.exists (fun value -> String.Equals(value, "1", StringComparison.Ordinal))

[<RequireQualifiedAccess>]
type Kind =
    /// The emulator for Windows.
    | Windows
    /// The Linux emulator of the vNext preview, which CI runs.
    | VNext

    /// The name of the baseline.
    member this.Provider =
        match this with
        | Kind.Windows -> "cosmos-emulator-windows"
        | Kind.VNext -> "cosmos-emulator-vnext"

// The emulator serves a certificate that it signed itself; it is accepted from this machine only
let private createHandler () =
    new HttpClientHandler(
        ServerCertificateCustomValidationCallback =
            (fun request _ _ errors ->
                errors = SslPolicyErrors.None
                || (match request.RequestUri with
                    | null -> false
                    | uri -> uri.IsLoopback))
    )

/// Asks the endpoint which emulator answers: the <c>Server</c> header of every answer, also of the refusal of a
/// request without authorization, names the product. <c>ValueNone</c> when nothing answers within five seconds.
let probe () : Kind voption =
    try
        use client = new HttpClient(createHandler (), true, Timeout = TimeSpan.FromSeconds 5.0)
        use response = client.GetAsync(endpoint).GetAwaiter().GetResult()

        let names (product: string) =
            response.Headers.Server
            |> Seq.exists (fun server ->
                match server.Product with
                | null -> false
                | found -> String.Equals(found.Name, product, StringComparison.Ordinal))

        match names "PGSQL", names "Microsoft-HTTPAPI" with
        | true, _ -> ValueSome Kind.VNext
        | _, true -> ValueSome Kind.Windows
        | _ ->
            invalidOp
                $"The emulator at {endpoint} sent the Server header '{response.Headers.Server}', which names neither the vNext emulator (PGSQL) nor the Windows emulator (Microsoft-HTTPAPI)."
    with
    | :? HttpRequestException -> ValueNone
    | :? OperationCanceledException -> ValueNone

let private databasePrefix = "fsharp-linq-"

let private partitionKey = "shop"

let private createClient () =
    new CosmosClient(
        endpoint,
        key,
        CosmosClientOptions(
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            HttpClientFactory = (fun () -> new HttpClient(new QueryRecorder(createHandler ()), true))
        )
    )

/// Deletes the databases of earlier runs that were not cleaned up. A younger database may belong to a run that
/// is still going, so it stays.
let private sweep (client: CosmosClient) =
    try
        use iterator = client.GetDatabaseQueryIterator<DatabaseProperties>()

        while iterator.HasMoreResults do
            for database in iterator.ReadNextAsync().GetAwaiter().GetResult() do
                let isOld =
                    database.LastModified.HasValue
                    && DateTime.UtcNow - database.LastModified.Value > TimeSpan.FromHours 1.0

                if isOld && database.Id.StartsWith(databasePrefix, StringComparison.Ordinal) then
                    client.GetDatabase(database.Id).DeleteAsync().GetAwaiter().GetResult() |> ignore
    with _ ->
        ()

/// Stores a document for every entity, with the properties that Cosmos needs: <c>id</c>, a string, and the key.
let private seed (container: Container) (entities: seq<obj>) =
    for entity in entities do
        let document = JObject.FromObject entity
        document["id"] <- JValue(string (document.Value<int> "Id"))
        document["pk"] <- JValue partitionKey

        container.CreateItemAsync(document, PartitionKey partitionKey).GetAwaiter().GetResult() |> ignore

let private execute (databaseId: string) (db: Db) (scenario: Scenario) : Status * string =
    let queries = QueryRecorder.Start()

    let hideDatabase (text: string) =
        text.Replace(databaseId, "<database>", StringComparison.Ordinal)

    try
        let actual, sql =
            match scenario.Run db with
            | Outcome.Rows rows ->
                let sql = Text.normalizeSql (Cosmos.queryText rows)
                [ for row in rows -> row ], sql
            | Outcome.Value value ->
                let sent = lock queries (fun () -> queries |> Seq.map Text.normalizeSql |> String.concat " ;; ")
                [ value ], sent

        (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong), sql
    with error ->
        let status, detail = Runner.failed error
        status, hideDatabase detail

/// Runs every scenario against a database that this run creates in the emulator and deletes afterwards.
let run () : Observation list =
    use client = createClient ()
    sweep client

    let databaseId = $"{databasePrefix}{Guid.NewGuid()}"
    let database = client.CreateDatabaseAsync(databaseId).GetAwaiter().GetResult().Database

    try
        let create (name: string) =
            database.CreateContainerAsync(ContainerProperties(name, "/pk")).GetAwaiter().GetResult().Container

        let customers = create "Customers"
        let orders = create "Orders"
        let lines = create "Lines"
        let products = create "Products"

        let data = Data()
        seed customers (data.Customers |> Seq.map box)
        seed orders (data.Orders |> Seq.map box)
        seed lines (data.Lines |> Seq.map box)
        seed products (data.Products |> Seq.map box)

        Runner.run (execute databaseId (Cosmos.tables customers orders lines products))
    finally
        try
            database.DeleteAsync().GetAwaiter().GetResult() |> ignore
        with _ ->
            ()
