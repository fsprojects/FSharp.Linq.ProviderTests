namespace FSharp.Linq.ProviderTests

open System
open System.Collections.Generic
open System.Linq
open System.Net.Http
open System.Net.Security
open System.Threading
open System.Threading.Tasks
open Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos.Linq
open Newtonsoft.Json.Linq
open FSharp.Core.UnitTests.Linq

/// Cosmos builds the SQL of a query without the network; a request to the network is not what is checked.
type Offline() =
    inherit HttpMessageHandler()

    static member Message = "offline"

    override _.SendAsync(_, _) =
        Task.FromException<HttpResponseMessage>(InvalidOperationException Offline.Message)

/// Remembers the queries that the Cosmos SDK sends. A scenario that computes a number has run its query by the time
/// the provider is done, so the text of that query can only be read from the request.
type QueryRecorder(inner: HttpMessageHandler) =
    inherit HttpMessageHandler()

    static let current = new AsyncLocal<List<string>>()

    let invoker = new HttpMessageInvoker(inner)

    /// Starts collecting on this flow of execution.
    static member Start() : List<string> =
        let queries = List<string>()
        current.Value <- queries
        queries

    override _.SendAsync(request, cancellationToken) =
        task {
            match current.Value, request.Content with
            | null, _
            | _, null -> ()
            | queries, content ->
                let! body = content.ReadAsStringAsync cancellationToken

                let query =
                    try
                        (JObject.Parse body).Value<string> "query"
                    with _ ->
                        null

                match query with
                | null -> ()
                | query ->
                    lock queries (fun () ->
                        if not (queries.Contains query) then
                            queries.Add query)

            return! invoker.SendAsync(request, cancellationToken)
        }

    override _.Dispose(disposing: bool) =
        if disposing then
            invoker.Dispose()

        base.Dispose disposing

module Cosmos =

    let private toQueryDefinition =
        typeof<CosmosLinqExtensions>.GetMethods()
        |> Array.find (fun method -> method.Name = "ToQueryDefinition" && method.GetParameters().Length = 1)

    /// The SQL that the Cosmos provider builds for the query.
    let queryText (rows: IQueryable) : string =
        let definition =
            toQueryDefinition.MakeGenericMethod(rows.ElementType).Invoke(null, [| box rows |]) :?> QueryDefinition

        definition.QueryText

    /// The roots of the scenarios: a container for each entity.
    let tables (customers: Container) (orders: Container) (lines: Container) (products: Container) : Db =
        { Customers = customers.GetItemLinqQueryable<Customer>(true)
          Orders = orders.GetItemLinqQueryable<Order>(true)
          Lines = lines.GetItemLinqQueryable<OrderLine>(true)
          Products = products.GetItemLinqQueryable<Product>(true) }

    let rec private mentionsOffline (error: exn) =
        match error with
        | null -> false
        | error -> error.Message.Contains(Offline.Message, StringComparison.Ordinal) || mentionsOffline error.InnerException

    let private offlineClient =
        lazy
            (new CosmosClient(
                "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==",
                CosmosClientOptions(
                    ConnectionMode = ConnectionMode.Gateway,
                    LimitToEndpoint = true,
                    HttpClientFactory = (fun () -> new HttpClient(new Offline()))
                )
            ))

    /// Asks the Cosmos provider for the SQL of the scenario, without a server. A scenario that computes a value
    /// has to send the query to produce the value, so there is nothing to ask for.
    let offline (scenario: Scenario) : Status * string =
        let container = offlineClient.Value.GetContainer("shop", "items")

        try
            match scenario.Run(tables container container container container) with
            | Outcome.Rows rows -> Status.Ok, Text.normalizeSql (queryText rows)
            | Outcome.Value _ -> Status.NotApplicable, ""
        with
        | error when mentionsOffline error -> Status.NotApplicable, ""
        | error -> Runner.failed error
