// Creates db/shop.db: the tables of db/shop.sql filled with the data of the scenarios.
// SQLProvider reads this file, so run the script again, and commit the result, when the data or the tables change:
//
//   dotnet fsi scripts/create-db.fsx
//
// The database is left in the DELETE journal mode and vacuumed, so that no -wal file belongs to it and the file does
// not change from one run to the next.

#r "nuget: Microsoft.Data.Sqlite, 10.0.14"
#load "../src/Scenarios/QueryProviderShape.fs"
#load "../src/Scenarios/QueryScenarioDomain.fs"

open System
open System.IO
open Microsoft.Data.Sqlite
open FSharp.Core.UnitTests.Linq

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let databasePath = Path.Combine(root, "db", "shop.db")

if File.Exists databasePath then
    File.Delete databasePath

let connection = new SqliteConnection($"Data Source={databasePath};Pooling=False")
connection.Open()

let execute (sql: string) =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

execute (File.ReadAllText(Path.Combine(root, "db", "shop.sql")))

let insert (table: string) (columns: (string * obj) list) =
    use command = connection.CreateCommand()
    let names = columns |> List.map (fun (name, _) -> $"\"{name}\"") |> String.concat ", "
    let parameters = columns |> List.mapi (fun i _ -> $"$p{i}") |> String.concat ", "
    command.CommandText <- $"INSERT INTO \"{table}\" ({names}) VALUES ({parameters})"

    columns
    |> List.iteri (fun i (_, value) ->
        let value =
            match value with
            | null -> box DBNull.Value
            | value -> value

        command.Parameters.AddWithValue($"$p{i}", value) |> ignore)

    command.ExecuteNonQuery() |> ignore

let nullable (value: Nullable<'T>) : obj =
    if value.HasValue then box value.Value else null

let data = Data()

let transaction = connection.BeginTransaction()

for product in data.Products do
    insert "Products" [ "Id", box product.Id; "Name", box product.Name; "Category", box product.Category ]

for customer in data.Customers do
    insert
        "Customers"
        [ "Id", box customer.Id
          "Name", box customer.Name
          "City", box customer.City
          "Age", box customer.Age
          "Registered", nullable customer.Registered ]

for order in data.Orders do
    insert "Orders" [ "Id", box order.Id; "CustomerId", box order.CustomerId; "Total", box order.Total ]

for line in data.Lines do
    insert
        "OrderLines"
        [ "Id", box line.Id
          "OrderId", box line.OrderId
          "ProductId", box line.ProductId
          "Quantity", box line.Quantity
          "Price", box line.Price
          "Discount", nullable line.Discount ]

transaction.Commit()

execute "PRAGMA journal_mode = DELETE"
execute "VACUUM"
connection.Close()
SqliteConnection.ClearAllPools()

printfn "Wrote %s" databasePath
