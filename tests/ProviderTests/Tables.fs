namespace FSharp.Linq.ProviderTests

open System
open System.Collections
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open System.Reflection
open Azure.Data.Tables
open FSharp.Core.UnitTests.Linq

/// A query that Azure Table Storage cannot be asked: the service filters the rows of one table, and that is all.
exception CannotExpress of reason: string

/// The roots of the scenarios for Azure.Data.Tables, which has no LINQ provider: its queries are a filter, an
/// <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> that <c>TableClient.CreateQueryFilter</c> turns into the text the
/// service reads. A query of a scenario is that filter when it is a table with <c>where</c> clauses only; the
/// filters of the clauses are joined with <c>and</c>.
type TablesSource<'T>(provider: TablesProvider, expression: Expression) =
    /// A whole table.
    new(provider: TablesProvider) = TablesSource<'T>(provider, Unchecked.defaultof<Expression>)

    interface IOrderedQueryable<'T> with
        member this.ElementType = typeof<'T>

        member this.Expression =
            if isNull expression then Expression.Constant(this) :> Expression else expression

        member _.Provider = provider :> IQueryProvider

        member this.GetEnumerator() : IEnumerator<'T> =
            (provider.Rows (this :> IQueryable).Expression |> Seq.cast<'T>).GetEnumerator()

        member this.GetEnumerator() : IEnumerator =
            ((this :> IEnumerable<'T>).GetEnumerator()) :> IEnumerator

/// Turns the query of a scenario into a filter and gives it to <paramref name="read"/>, which returns the rows of
/// the table of an element type that pass a filter.
and TablesProvider(read: Type -> string -> obj seq) =

    static let createQueryFilter =
        typeof<TableClient>.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
        |> Array.find (fun method -> method.Name = "CreateQueryFilter" && method.IsGenericMethodDefinition)

    static let rec unquote (expression: Expression) =
        match expression with
        | :? UnaryExpression as unary when unary.NodeType = ExpressionType.Quote -> unquote unary.Operand
        | _ -> expression

    static let (|Select|_|) (expression: Expression) =
        match expression with
        | :? MethodCallExpression as call when call.Method.DeclaringType = typeof<Queryable> && call.Method.Name = "Select" ->
            match call.Arguments |> Seq.map unquote |> List.ofSeq with
            | [ source; (:? LambdaExpression as selector) ] when selector.Parameters.Count = 1 -> Some(source, selector)
            | _ -> None
        | _ -> None

    /// The element type of the table and the filters of its <c>where</c> clauses, innermost first.
    static let rec decompose (expression: Expression) : Type * LambdaExpression list =
        match expression with
        | :? ConstantExpression as constant when (constant.Value :? IQueryable) ->
            (constant.Value :?> IQueryable).ElementType, []
        // The compiler leaves a select of the element itself after a for
        | Select(source, selector) when obj.ReferenceEquals(selector.Body, selector.Parameters[0]) -> decompose source
        | :? MethodCallExpression as call when call.Method.DeclaringType = typeof<Queryable> ->
            match call.Method.Name, call.Arguments |> Seq.map unquote |> List.ofSeq with
            | "Where", [ source; (:? LambdaExpression as filter) ] when filter.Parameters.Count = 1 ->
                let table, filters = decompose source
                table, filters @ [ filter ]
            | name, _ -> raise (CannotExpress $"{name} is not a filter of a table")
        | _ -> raise (CannotExpress $"{expression.NodeType} is not a table")

    /// Whether the expression reads a navigation, such as the orders of a customer: a table has none.
    static let readsNavigation (expression: Expression) =
        let mutable found = false

        let visitor =
            { new ExpressionVisitor() with
                member _.VisitMember(node) =
                    if
                        node.Type <> typeof<string>
                        && typeof<IEnumerable>.IsAssignableFrom node.Type
                        && not (typeof<IQueryable>.IsAssignableFrom node.Type)
                    then
                        found <- true

                    base.VisitMember node }

        visitor.Visit expression |> ignore
        found

    /// The query without a projection at its end, and the projection: a service that only filters leaves the
    /// projection of the rows it returns to the client, as an application does. A projection that reads a
    /// navigation cannot be asked.
    static let projected (expression: Expression) : Expression * LambdaExpression voption =
        match expression with
        | Select(source, selector) when not (obj.ReferenceEquals(selector.Body, selector.Parameters[0])) ->
            if readsNavigation selector then
                raise (CannotExpress "a projection that reads a navigation")

            source, ValueSome selector
        | _ -> expression, ValueNone

    /// The text of the filter that the service reads, empty for a whole table.
    static member Filter(expression: Expression) : Type * string =
        let table, filters = decompose (fst (projected expression))

        let texts =
            [ for filter in filters ->
                  createQueryFilter.MakeGenericMethod(table).Invoke(null, [| box filter |]) :?> string ]

        table,
        match texts with
        | [] -> ""
        | [ text ] -> text
        | texts -> texts |> List.map (fun text -> $"({text})") |> String.concat " and "

    member _.Rows(expression: Expression) : obj seq =
        let table, filter = TablesProvider.Filter expression
        let rows = read table filter

        match snd (projected expression) with
        | ValueNone -> rows
        | ValueSome selector ->
            let project = selector.Compile()
            rows |> Seq.map (fun row -> project.DynamicInvoke [| row |])

    interface IQueryProvider with
        member this.CreateQuery<'E>(expression: Expression) : IQueryable<'E> =
            TablesSource<'E>(this, expression) :> IQueryable<'E>

        member this.CreateQuery(expression: Expression) : IQueryable =
            let elementType = expression.Type.GetGenericArguments()[0]

            Activator.CreateInstance(typedefof<TablesSource<_>>.MakeGenericType(elementType), [| box this; box expression |])
            :?> IQueryable

        member _.Execute<'R>(expression: Expression) : 'R =
            raise (CannotExpress "a value computed by the query, not rows of a table")

        member _.Execute(expression: Expression) : obj =
            raise (CannotExpress "a value computed by the query, not rows of a table")

/// Azure Table Storage, through Azure.Data.Tables, on Azurite.
module Tables =

    let server =
        { Name = "Azurite"
          Variable = "AZURITE_CONNECTION_STRING"
          RequiredVariable = "AZURITE_REQUIRED"
          // The account and key of Azurite, which are documented and the same everywhere
          Default =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;" }

    let private tablesOf (provider: TablesProvider) : Db =
        { Customers = TablesSource<Customer>(provider)
          Orders = TablesSource<Order>(provider)
          Lines = TablesSource<OrderLine>(provider)
          Products = TablesSource<Product>(provider) }

    let rec private cannotExpress (error: exn) =
        match error with
        | null -> false
        | CannotExpress _ -> true
        | error -> cannotExpress error.InnerException

    let private offlineProvider =
        TablesProvider(fun _ _ -> invalidOp "Without a server there are no rows.")

    /// The filter of the scenario, without a server. A scenario that Table Storage cannot be asked is n/a.
    let offline (scenario: Scenario) : Status * string =
        try
            match scenario.Run(tablesOf offlineProvider) with
            | Outcome.Rows rows ->
                let _, filter = TablesProvider.Filter rows.Expression
                Status.Ok, filter
            | Outcome.Value _ -> Status.NotApplicable, ""
        with
        | error when cannotExpress error -> Status.NotApplicable, ""
        | error -> Runner.failed error

    let private plain (ty: Type) =
        ty.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.filter (fun property ->
            let t =
                match Nullable.GetUnderlyingType property.PropertyType with
                | null -> property.PropertyType
                | underlying -> underlying

            t.IsPrimitive || t = typeof<string> || t = typeof<DateTime>)

    /// An entity of the table for an object: its plain properties, and its key as the row key. A date has no time
    /// zone, so it is stored as UTC, which the year of a date then does not depend on.
    let private toEntity (item: obj) =
        let entity = TableEntity("shop", string (item.GetType().GetProperty("Id").GetValue item))

        for property in plain (item.GetType()) do
            match property.GetValue item with
            | null -> ()
            | :? DateTime as date -> entity[property.Name] <- DateTime.SpecifyKind(date, DateTimeKind.Utc)
            | value -> entity[property.Name] <- value

        entity

    let private ofEntity (ty: Type) (entity: TableEntity) : obj =
        let item = Activator.CreateInstance ty

        for property in plain ty do
            match entity.TryGetValue property.Name with
            | true, (:? DateTimeOffset as date) -> property.SetValue(item, box date.UtcDateTime)
            | true, null -> ()
            | true, value -> property.SetValue(item, value)
            | _ -> ()

        item

    let private tableName (prefix: string) (ty: Type) = $"{prefix}{ty.Name}"

    /// Whether the service answers.
    let probe () : Result<unit, string> =
        try
            let service = TableServiceClient(Server.address server)

            service.Query(maxPerPage = Nullable 1) |> Seq.truncate 1 |> Seq.iter ignore
            Ok()
        with error ->
            Error(Text.message (Runner.rootCause error).Message)

    /// Runs every scenario that Table Storage can be asked, in tables that this run creates and deletes afterwards.
    let run () : Observation list =
        let service = TableServiceClient(Server.address server)
        let prefix = $"""fsl{Guid.NewGuid().ToString("N").Substring(0, 12)}"""
        let types = [ typeof<Customer>; typeof<Order>; typeof<OrderLine>; typeof<Product> ]

        try
            let data = Data()

            let fill (ty: Type) (items: seq<obj>) =
                let table = service.GetTableClient(tableName prefix ty)
                table.CreateIfNotExists() |> ignore

                for item in items do
                    table.AddEntity(toEntity item) |> ignore

            fill typeof<Customer> (Seq.cast data.Customers)
            fill typeof<Order> (Seq.cast data.Orders)
            fill typeof<OrderLine> (Seq.cast data.Lines)
            fill typeof<Product> (Seq.cast data.Products)

            let read (ty: Type) (filter: string) : obj seq =
                let table = service.GetTableClient(tableName prefix ty)

                table.Query<TableEntity>((if filter = "" then null else filter))
                |> Seq.map (ofEntity ty)
                |> Seq.toList
                |> Seq.ofList

            let provider = TablesProvider read

            let execute (scenario: Scenario) : Status * string =
                try
                    match scenario.Run(tablesOf provider) with
                    | Outcome.Rows rows ->
                        let _, filter = TablesProvider.Filter rows.Expression
                        let actual = [ for row in rows -> row ]
                        (if Runner.sameRows scenario (Data()) actual then Status.Ok else Status.Wrong), filter
                    | Outcome.Value _ -> Status.NotApplicable, ""
                with
                | error when cannotExpress error -> Status.NotApplicable, ""
                | error -> Runner.failed error

            Runner.run execute
        finally
            for ty in types do
                try
                    service.DeleteTable(tableName prefix ty) |> ignore
                with _ ->
                    ()
