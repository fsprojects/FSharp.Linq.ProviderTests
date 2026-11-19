namespace FSharp.Linq.ProviderTests

open System
open System.Collections.Generic
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Diagnostics
open Microsoft.EntityFrameworkCore.Storage.ValueConversion
open FSharp.Core.UnitTests.Linq

/// The entities of the scenarios as EF Core maps them: a table for each class, named after the class.
type ShopContext(options: DbContextOptions<ShopContext>) =
    inherit DbContext(options)

    override this.OnModelCreating model =
        model.Entity<Customer>() |> ignore
        model.Entity<Order>() |> ignore
        model.Entity<OrderLine>() |> ignore
        model.Entity<Product>() |> ignore

        // The data brings its own keys. SQL Server refuses a value for an identity column, which SQLite and
        // PostgreSQL take
        if this.Database.IsSqlServer() then
            model.Entity<Customer>().Property(fun c -> c.Id).ValueGeneratedNever() |> ignore
            model.Entity<Order>().Property(fun o -> o.Id).ValueGeneratedNever() |> ignore
            model.Entity<OrderLine>().Property(fun l -> l.Id).ValueGeneratedNever() |> ignore
            model.Entity<Product>().Property(fun p -> p.Id).ValueGeneratedNever() |> ignore

        // The dates of the data have no time zone. Npgsql maps DateTime to timestamp with time zone, which only
        // takes UTC, so they are written as UTC. The column stays what a model without the conversion has, and so
        // does the SQL
        if this.Database.IsNpgsql() then
            model.Entity<Customer>().Property(fun c -> c.Registered).HasConversion(ShopContext.AsUtc :> ValueConverter)
            |> ignore

    static member private AsUtc =
        ValueConverter<DateTime, DateTime>(
            (fun date -> DateTime.SpecifyKind(date, DateTimeKind.Utc)),
            (fun date -> DateTime.SpecifyKind(date, DateTimeKind.Utc))
        )

/// The first command that a scenario sent. A provider that is asked for its SQL only throws this from the
/// interceptor, so that no command reaches a server that is not there.
exception CommandCaptured of sql: string

/// Collects the text of the commands that a context sends while a scenario runs. Start it on the flow of
/// execution of the scenario; the commands of other scenarios, running at the same time, are not mixed in.
type CommandRecorder(stopAtFirst: bool) =
    inherit DbCommandInterceptor()

    static let current = new AsyncLocal<List<string>>()

    /// Starts collecting on this flow of execution.
    static member Start() : List<string> =
        let commands = List<string>()
        current.Value <- commands
        commands

    member private _.Record(command: DbCommand) =
        match current.Value with
        | null -> ()
        | commands ->
            commands.Add command.CommandText

            if stopAtFirst then
                raise (CommandCaptured command.CommandText)

    override this.ReaderExecuting(command, eventData, result) =
        this.Record command
        result

    override this.ReaderExecutingAsync(command, eventData, result, cancellationToken) =
        this.Record command
        ValueTask<InterceptionResult<DbDataReader>>(result)

    override this.ScalarExecuting(command, eventData, result) =
        this.Record command
        result

    override this.ScalarExecutingAsync(command, eventData, result, cancellationToken) =
        this.Record command
        ValueTask<InterceptionResult<obj>>(result)

    override this.NonQueryExecuting(command, eventData, result) =
        this.Record command
        result

    override this.NonQueryExecutingAsync(command, eventData, result, cancellationToken) =
        this.Record command
        ValueTask<InterceptionResult<int>>(result)

/// Keeps a context that has no server behind it from trying to connect.
type ConnectionSuppressor() =
    inherit DbConnectionInterceptor()

    override _.ConnectionOpening(connection, eventData, result) = InterceptionResult.Suppress()

    override _.ConnectionOpeningAsync(connection, eventData, result, cancellationToken) =
        ValueTask<InterceptionResult>(InterceptionResult.Suppress())

module Shop =

    /// The tables of a context as the roots of the scenarios.
    let tables (context: ShopContext) : Db =
        { Customers = context.Set<Customer>()
          Orders = context.Set<Order>()
          Lines = context.Set<OrderLine>()
          Products = context.Set<Product>() }

    /// Creates the tables of the database that the options point at, and fills them with the data of the scenarios.
    let seed (options: DbContextOptions<ShopContext>) =
        use context = new ShopContext(options)
        context.Database.EnsureCreated() |> ignore
        let data = Data()
        context.AddRange(data.Customers |> Seq.cast<obj>)
        context.AddRange(data.Products |> Seq.cast<obj>)
        context.SaveChanges() |> ignore
