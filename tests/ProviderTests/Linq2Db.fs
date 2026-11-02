namespace FSharp.Linq.ProviderTests

open System
open System.Collections.Generic
open System.Diagnostics
open LinqToDB
open LinqToDB.Data
open LinqToDB.DataProvider.SQLite
open LinqToDB.Mapping
open FSharp.Core.UnitTests.Linq

/// linq2db over the in-memory SQLite database that EF Core filled. linq2db names a table after its class, as EF Core
/// does here, so only the navigation properties need a mapping.
module Linq2Db =

    let private mappingSchema =
        let schema = MappingSchema()
        let builder = FluentMappingBuilder(schema)

        let associate (owner: Type) (property: string) (thisKey: string) (otherKey: string) =
            builder.HasAttribute(owner.GetProperty property, AssociationAttribute(ThisKey = thisKey, OtherKey = otherKey))
            |> ignore

        associate typeof<Customer> "Orders" "Id" "CustomerId"
        associate typeof<Order> "Lines" "Id" "OrderId"
        builder.Build() |> ignore
        schema

    let private provider = SQLiteTools.GetDataProvider SQLiteProvider.Microsoft

    /// Runs the scenario with linq2db and compares the rows with the expected ones.
    let sqlite (shop: SqliteShop) (scenario: Scenario) : Status * string =
        let commands = List<string>()

        let options =
            DataOptions()
                .UseConnection(provider, shop.Connection, false)
                .UseMappingSchema(mappingSchema)
                .UseTracing(
                    TraceLevel.Info,
                    fun info ->
                        if info.TraceInfoStep = TraceInfoStep.BeforeExecute then
                            match info.Command with
                            | null -> ()
                            | command -> lock commands (fun () -> commands.Add command.CommandText)
                )

        use connection = new DataConnection(options)

        let db =
            { Customers = connection.GetTable<Customer>()
              Orders = connection.GetTable<Order>()
              Lines = connection.GetTable<OrderLine>()
              Products = connection.GetTable<Product>() }

        try
            let actual =
                match scenario.Run db with
                | Outcome.Rows rows -> [ for row in rows -> row ]
                | Outcome.Value value -> [ value ]

            let sql = lock commands (fun () -> commands |> Seq.map Text.normalizeSql |> String.concat " ;; ")

            (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong), sql
        with error ->
            Runner.failed error
