namespace FSharp.Linq.ProviderTests.SQLProviderTests

open System
open System.Linq
open FSharp.Data.Sql
open FSharp.Core.UnitTests.Linq

/// One query written the way SQLProvider users write it, and the rows that it must return for the data of the
/// scenarios, which db/shop.db holds.
type Case =
    {
        Section: string
        Name: string
        /// Runs the query against the database and gives its rows.
        Run: Database.Sql.dataContext -> obj list
        Expected: Data -> obj list
        /// Whether the order of the rows belongs to the query.
        Ordered: bool
    }

module Case =

    /// A query that gives rows. The rows are compared in any order unless <see cref="ordered"/> says otherwise.
    let rows (section: string) (name: string) (run: Database.Sql.dataContext -> #seq<'Actual>) (expected: Data -> seq<'Expected>) =
        { Section = section
          Name = name
          Run = fun context -> [ for row in run context -> box row ]
          Expected = fun data -> [ for row in expected data -> box row ]
          Ordered = false }

    /// A query that gives one value: a count, a sum, whether something exists.
    let value (section: string) (name: string) (run: Database.Sql.dataContext -> 'Actual) (expected: Data -> 'Expected) =
        { Section = section
          Name = name
          Run = fun context -> [ box (run context) ]
          Expected = fun data -> [ box (expected data) ]
          Ordered = true }

    let ordered (case: Case) = { case with Ordered = true }

module Queries =

    let private registered (customer: Customer) =
        if customer.Registered.HasValue then Some customer.Registered.Value else None

    let private ordersOf (data: Data) (customer: Customer) =
        data.Orders |> List.filter (fun order -> order.CustomerId = customer.Id)

    let cases: Case list =
        [ Case.rows
              "Projection"
              "a column"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.map _.Name)

          Case.rows
              "Projection"
              "a tuple of columns"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Id, c.Name)
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Id, c.Name))

          Case.rows
              "Projection"
              "an anonymous record"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select {| Name = c.Name; Age = c.Age |}
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> {| Name = c.Name; Age = c.Age |}))

          Case.rows
              "Projection"
              "a column that can be null"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Name, c.Registered)
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Name, registered c))

          Case.rows
              "Operator"
              "where with a comparison"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Age > 26)
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Age > 26) |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where with and, or"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where ((c.City = "London" && c.Age > 30) || c.City = "Berlin")
                          select c.Name
                  })
              (fun data ->
                  data.Customers
                  |> Seq.filter (fun c -> (c.City = "London" && c.Age > 30) || c.City = "Berlin")
                  |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where with not"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (not (c.City = "Paris"))
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.City <> "Paris") |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where with a text search"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Name.Contains "o")
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Name.Contains "o") |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where over a column that can be null"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where c.Registered.IsSome
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Registered.HasValue) |> Seq.map _.Name)

          Case.rows
              "Operator"
              "sortBy, thenBy and take"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          sortBy c.City
                          thenByDescending c.Age
                          select (c.City, c.Name)
                          take 3
                  })
              (fun data ->
                  data.Customers
                  |> Seq.sortBy (fun c -> c.City, -c.Age)
                  |> Seq.map (fun c -> c.City, c.Name)
                  |> Seq.truncate 3)
          |> Case.ordered

          Case.rows
              "Operator"
              "skip and take"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          sortBy c.Id
                          select c.Name
                          skip 1
                          take 2
                  })
              (fun data -> data.Customers |> Seq.sortBy _.Id |> Seq.map _.Name |> Seq.skip 1 |> Seq.truncate 2)
          |> Case.ordered

          Case.rows
              "Operator"
              "distinct"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select c.City
                          distinct
                  })
              (fun data -> data.Customers |> Seq.map _.City |> Seq.distinct)

          Case.value
              "Operator"
              "count"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Age > 26)
                          count
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Age > 26) |> Seq.length)

          Case.value
              "Operator"
              "sumBy"
              (fun ctx ->
                  query {
                      for o in ctx.Main.Orders do
                          sumBy o.Total
                  })
              (fun data -> data.Orders |> Seq.sumBy _.Total)

          Case.value
              "Operator"
              "exists"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          exists (c.City = "Berlin")
                  })
              (fun data -> data.Customers |> Seq.exists (fun c -> c.City = "Berlin"))

          Case.rows
              "Key"
              "groupBy with a count"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          groupBy c.City into g
                          select (g.Key, g.Count())
                  })
              (fun data -> data.Customers |> Seq.countBy _.City)

          Case.rows
              "Structure"
              "an inner join"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          join o in ctx.Main.Orders on (c.Id = o.CustomerId)
                          select (c.Name, o.Total)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c -> c.Name, o.Total ])

          Case.rows
              "Structure"
              "a navigation property"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for o in c.``main.Orders by Id`` do
                              select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c -> c.Name, o.Id ])

          Case.rows
              "Structure"
              "navigation properties two levels deep"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for o in c.``main.Orders by Id`` do
                              for l in o.``main.OrderLines by Id`` do
                                  select (c.Name, l.Quantity)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c do
                            for l in o.Lines -> c.Name, l.Quantity ])

          Case.rows
              "Structure"
              "a left outer join keeps the row without a match"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          leftOuterJoin o in ctx.Main.Orders on (c.Id = o.CustomerId) into orders
                          for o in orders.DefaultIfEmpty() do
                              select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        match ordersOf data c with
                        | [] -> yield c.Name, 0
                        | orders ->
                            for o in orders -> c.Name, o.Id ])

          Case.rows
              "Structure"
              "a group join flattened is an inner join"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          groupJoin o in ctx.Main.Orders on (c.Id = o.CustomerId) into orders
                          for o in orders do
                              select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c -> c.Name, o.Id ])

          Case.rows
              "Structure"
              "three tables joined"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          join o in ctx.Main.Orders on (c.Id = o.CustomerId)
                          join l in ctx.Main.OrderLines on (o.Id = l.OrderId)
                          join p in ctx.Main.Products on (l.ProductId = p.Id)
                          select (c.Name, p.Name)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c do
                            for l in o.Lines do
                                for p in data.Products do
                                    if p.Id = l.ProductId then
                                        yield c.Name, p.Name ])

          Case.rows
              "Structure"
              "two tables that nothing joins"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for p in ctx.Main.Products do
                              where (c.City = "London" && p.Category = "Food")
                              select (c.Name, p.Name)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for p in data.Products do
                            if c.City = "London" && p.Category = "Food" then
                                yield c.Name, p.Name ])

          Case.rows
              "MethodSyntax"
              "Where and Select"
              (fun ctx -> ctx.Main.Customers.Where(fun c -> c.Age > 26).Select(fun c -> c.Name))
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Age > 26) |> Seq.map _.Name)


          Case.rows
              "Projection"
              "an arithmetic expression"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Name, c.Age + 1L)
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Name, c.Age + 1))

          Case.rows
              "Projection"
              "a text expression"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Name + "!")
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Name + "!"))

          Case.rows
              "Projection"
              "a constant next to a column"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Name, 1)
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Name, 1))

          Case.rows
              "Projection"
              "a record"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select { PersonName = c.Name; PersonAge = int c.Age }
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> { PersonName = c.Name; PersonAge = c.Age }))

          Case.rows
              "Projection"
              "a nested tuple"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (c.Id, (c.Name, c.City))
                  })
              (fun data -> data.Customers |> Seq.map (fun c -> c.Id, (c.Name, c.City)))

          Case.rows
              "Operator"
              "where with a captured value"
              (fun ctx ->
                  let city = "London"

                  query {
                      for c in ctx.Main.Customers do
                          where (c.City = city)
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.City = "London") |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where in a list"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.City |=| [ "London"; "Berlin" ])
                          select c.Name
                  })
              (fun data ->
                  data.Customers
                  |> Seq.filter (fun c -> c.City = "London" || c.City = "Berlin")
                  |> Seq.map _.Name)

          Case.rows
              "Operator"
              "where in a subquery"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (
                              c.Id
                              |=| query {
                                  for o in ctx.Main.Orders do
                                      select o.CustomerId
                              }
                          )

                          select c.Name
                  })
              (fun data ->
                  data.Customers
                  |> Seq.filter (fun c -> data.Orders |> List.exists (fun o -> o.CustomerId = c.Id))
                  |> Seq.map _.Name)

          Case.rows
              "Operator"
              "sortByDescending"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          sortByDescending c.Age
                          select c.Name
                  })
              (fun data -> data.Customers |> Seq.sortByDescending _.Age |> Seq.map _.Name)
          |> Case.ordered

          Case.value
              "Operator"
              "head"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          sortBy c.Id
                          select c.Name
                          head
                  })
              (fun data -> data.Customers |> Seq.sortBy _.Id |> Seq.map _.Name |> Seq.head)

          Case.value
              "Operator"
              "exactlyOne"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Id = 4L)
                          select c.Name
                          exactlyOne
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Id = 4) |> Seq.map _.Name |> Seq.exactlyOne)

          Case.value
              "Operator"
              "headOrDefault of nothing"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Id = 99L)
                          select c.Name
                          headOrDefault
                  })
              (fun _ -> null: string)

          Case.value
              "Operator"
              "maxBy"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          maxBy c.Age
                  })
              (fun data -> data.Customers |> Seq.map _.Age |> Seq.max)

          Case.value
              "Operator"
              "minBy"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          minBy c.Age
                  })
              (fun data -> data.Customers |> Seq.map _.Age |> Seq.min)

          Case.value
              "Operator"
              "averageBy"
              (fun ctx ->
                  query {
                      for o in ctx.Main.Orders do
                          averageBy o.Total
                  })
              (fun data -> data.Orders |> Seq.averageBy _.Total)

          Case.value
              "Operator"
              "all"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          all (c.Age > 20L)
                  })
              (fun data -> data.Customers |> Seq.forall (fun c -> c.Age > 20))

          Case.value
              "Operator"
              "contains"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select c.Name
                          contains "Alice"
                  })
              (fun data -> data.Customers |> Seq.exists (fun c -> c.Name = "Alice"))

          Case.value
              "Operator"
              "count of distinct values"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select c.City
                          distinct
                          count
                  })
              (fun data -> data.Customers |> Seq.map _.City |> Seq.distinct |> Seq.length)

          Case.rows
              "Key"
              "groupBy with a sum"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          groupBy c.City into g
                          select (g.Key, g.Sum(fun c -> c.Age))
                  })
              (fun data ->
                  data.Customers
                  |> Seq.groupBy _.City
                  |> Seq.map (fun (city, customers) -> city, customers |> Seq.sumBy _.Age))

          Case.rows
              "Key"
              "groupBy with a maximum and an average"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          groupBy c.City into g
                          select (g.Key, g.Max(fun c -> c.Age), g.Average(fun c -> c.Age))
                  })
              (fun data ->
                  data.Customers
                  |> Seq.groupBy _.City
                  |> Seq.map (fun (city, customers) ->
                      city, customers |> Seq.map _.Age |> Seq.max, customers |> Seq.averageBy (fun c -> float c.Age)))

          Case.rows
              "Key"
              "groupBy with having"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          groupBy c.City into g
                          where (g.Count() > 1)
                          select (g.Key, g.Count())
                  })
              (fun data -> data.Customers |> Seq.countBy _.City |> Seq.filter (fun (_, count) -> count > 1))

          Case.rows
              "Key"
              "groupBy after where"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          where (c.Age > 26L)
                          groupBy c.City into g
                          select (g.Key, g.Count())
                  })
              (fun data -> data.Customers |> Seq.filter (fun c -> c.Age > 26) |> Seq.countBy _.City)

          Case.rows
              "Key"
              "groupBy over a join"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          join o in ctx.Main.Orders on (c.Id = o.CustomerId)
                          groupBy c.Name into g
                          select (g.Key, g.Sum(fun (_, o) -> o.Total))
                  })
              (fun data ->
                  [ for c in data.Customers do
                        match ordersOf data c with
                        | [] -> ()
                        | orders -> yield c.Name, orders |> List.sumBy _.Total ])

          Case.rows
              "Structure"
              "a left outer join counted per group"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          leftOuterJoin o in ctx.Main.Orders on (c.Id = o.CustomerId) into orders
                          for o in orders.DefaultIfEmpty() do
                              groupBy c.Name into g
                              select (g.Key, g.Count())
                  })
              // The row of a customer without orders counts as one, as in SQL
              (fun data -> data.Customers |> Seq.map (fun c -> c.Name, max 1 (ordersOf data c).Length))

          Case.rows
              "Structure"
              "two left outer joins in a row"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          leftOuterJoin o in ctx.Main.Orders on (c.Id = o.CustomerId) into orders
                          for o in orders.DefaultIfEmpty() do
                              leftOuterJoin l in ctx.Main.OrderLines on (o.Id = l.OrderId) into lines
                              for l in lines.DefaultIfEmpty() do
                                  select (c.Name, o.Id, l.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        match ordersOf data c with
                        | [] -> yield c.Name, 0, 0
                        | orders ->
                            for o in orders do
                                match List.ofSeq o.Lines with
                                | [] -> yield c.Name, o.Id, 0
                                | lines ->
                                    for l in lines -> c.Name, o.Id, l.Id ])

          Case.rows
              "Structure"
              "a navigation property as an outer join"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for o in (!!) c.``main.Orders by Id`` do
                              select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        match ordersOf data c with
                        | [] -> yield c.Name, 0
                        | orders ->
                            for o in orders -> c.Name, o.Id ])

          Case.rows
              "Structure"
              "a navigation property to the parent"
              (fun ctx ->
                  query {
                      for o in ctx.Main.Orders do
                          for c in o.``main.Customers by Id`` do
                              select (o.Id, c.Name)
                  })
              (fun data ->
                  [ for o in data.Orders do
                        for c in data.Customers do
                            if c.Id = o.CustomerId then
                                yield o.Id, c.Name ])

          Case.rows
              "Structure"
              "a join written as a condition"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for o in ctx.Main.Orders do
                              where (c.Id = o.CustomerId)
                              select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c -> c.Name, o.Id ])

          Case.rows
              "Structure"
              "three tables that nothing joins"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          for o in ctx.Main.Orders do
                              for p in ctx.Main.Products do
                                  where (c.Id = o.CustomerId && p.Category = "Food" && c.City = "London")
                                  select (c.Name, o.Id, p.Name)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c do
                            for p in data.Products do
                                if p.Category = "Food" && c.City = "London" then
                                    yield c.Name, o.Id, p.Name ])

          Case.rows
              "Structure"
              "a join with conditions on both sides"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          join o in ctx.Main.Orders on (c.Id = o.CustomerId)
                          where (c.City = "London" && o.Total > 3.0)
                          select (c.Name, o.Id)
                  })
              (fun data ->
                  [ for c in data.Customers do
                        for o in ordersOf data c do
                            if c.City = "London" && o.Total > 3.0 then
                                yield c.Name, o.Id ])

          Case.rows
              "MethodSyntax"
              "OrderBy and Select"
              (fun ctx -> ctx.Main.Customers.OrderBy(fun c -> c.Age).Select(fun c -> c.Name))
              (fun data -> data.Customers |> Seq.sortBy _.Age |> Seq.map _.Name)
          |> Case.ordered

          Case.value
              "MethodSyntax"
              "Any"
              (fun ctx -> ctx.Main.Customers.Any(fun c -> c.City = "Berlin"))
              (fun data -> data.Customers |> Seq.exists (fun c -> c.City = "Berlin"))

          Case.value
              "MethodSyntax"
              "Sum"
              (fun ctx -> ctx.Main.Orders.Sum(fun o -> o.Total))
              (fun data -> data.Orders |> List.sumBy _.Total)

          Case.rows
              "FSharpOperator"
              "int of a column"
              (fun ctx ->
                  query {
                      for c in ctx.Main.Customers do
                          select (int c.Age)
                  })
              (fun data -> data.Customers |> Seq.map _.Age)

          Case.rows
              "FSharpOperator"
              "float of a column"
              (fun ctx ->
                  query {
                      for o in ctx.Main.Orders do
                          select (float o.Id)
                  })
              (fun data -> data.Orders |> Seq.map (fun o -> float o.Id)) ]
