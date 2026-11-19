namespace FSharp.Linq.ProviderTests

open System
open System.Data.Common
open Microsoft.Data.SqlClient
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Diagnostics
open MySql.Data.MySqlClient
open Npgsql
open FSharp.Core.UnitTests.Linq

/// A server that a test runs the scenarios on when one is there, as the test of the Cosmos DB Emulator does: a
/// variable gives its address, or else a documented local default, and another variable says that it has to be there.
type Server =
    {
        /// What the messages call the server.
        Name: string
        /// Overrides <see cref="Default"/>.
        Variable: string
        /// Set to 1 where the server has to be there, as on the job of CI that starts it: a missing server then fails
        /// the test instead of skipping it.
        RequiredVariable: string
        /// The address of a server in a local container, with the throwaway password the README documents.
        Default: string
    }

module Server =

    let private readVariable (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null -> ValueNone
        | value when String.IsNullOrWhiteSpace value -> ValueNone
        | value -> ValueSome(value.Trim())

    /// The connection string or address of the server.
    let address (server: Server) =
        readVariable server.Variable |> ValueOption.defaultValue server.Default

    let isRequired (server: Server) =
        readVariable server.RequiredVariable
        |> ValueOption.exists (fun value -> String.Equals(value, "1", StringComparison.Ordinal))

    let private secrets = [ "password"; "pwd"; "accountkey" ]

    /// The address without a password, for a message.
    let describe (server: Server) =
        let text = address server

        match Uri.TryCreate(text, UriKind.Absolute) with
        | true, uri when uri.Scheme <> "" && not (String.IsNullOrEmpty uri.UserInfo) ->
            text.Replace(uri.UserInfo + "@", "", StringComparison.Ordinal)
        | true, _ -> text
        | _ ->
            try
                let builder = DbConnectionStringBuilder(ConnectionString = text)

                for key in secrets do
                    builder.Remove key |> ignore

                builder.ConnectionString
            with _ ->
                server.Variable

/// EF Core on a real SQL Server, PostgreSQL or MySQL: every scenario runs against a database that the test creates and
/// deletes afterwards, and the rows are compared, as on SQLite.
module EfCoreServer =

    let sqlServer =
        { Name = "SQL Server"
          Variable = "SQLSERVER_CONNECTION_STRING"
          RequiredVariable = "SQLSERVER_REQUIRED"
          Default = "Server=127.0.0.1,1433;User Id=sa;Password=FSharp-Linq-1;TrustServerCertificate=True;Connect Timeout=5" }

    let postgreSql =
        { Name = "PostgreSQL"
          Variable = "POSTGRESQL_CONNECTION_STRING"
          RequiredVariable = "POSTGRESQL_REQUIRED"
          Default = "Host=127.0.0.1;Port=5432;Username=postgres;Password=fsharp-linq;Timeout=5" }

    let mySql =
        { Name = "MySQL"
          Variable = "MYSQL_CONNECTION_STRING"
          RequiredVariable = "MYSQL_REQUIRED"
          Default = "Server=127.0.0.1;Port=3306;User ID=root;Password=fsharp-linq;Connection Timeout=5" }

    let private sqlServerConnection (database: string) =
        SqlConnectionStringBuilder(Server.address sqlServer, InitialCatalog = database).ConnectionString

    let private postgreSqlConnection (database: string) =
        NpgsqlConnectionStringBuilder(Server.address postgreSql, Database = database).ConnectionString

    let private mySqlConnection (database: string) =
        MySqlConnectionStringBuilder(Server.address mySql, Database = database).ConnectionString

    let sqlServerOptions (database: string) =
        DbContextOptionsBuilder<ShopContext>()
            .UseSqlServer(sqlServerConnection database)
            .AddInterceptors(Recorders.executing :> IInterceptor)
            .Options

    let postgreSqlOptions (database: string) =
        DbContextOptionsBuilder<ShopContext>()
            .UseNpgsql(postgreSqlConnection database)
            .AddInterceptors(Recorders.executing :> IInterceptor)
            .Options

    let mySqlOptions (database: string) =
        DbContextOptionsBuilder<ShopContext>()
            .UseMySQL(mySqlConnection database)
            .AddInterceptors(Recorders.executing :> IInterceptor)
            .Options

    let private tryOpen (connection: DbConnection) : Result<unit, string> =
        try
            use connection = connection
            connection.Open()
            Ok()
        with error ->
            Error(Text.message (Runner.rootCause error).Message)

    /// Whether the SQL Server answers.
    let probeSqlServer () =
        tryOpen (new SqlConnection(sqlServerConnection "master"))

    /// Whether the PostgreSQL server answers.
    let probePostgreSql () =
        tryOpen (new NpgsqlConnection(postgreSqlConnection "postgres"))

    /// Whether the MySQL server answers.
    let probeMySql () =
        tryOpen (new MySqlConnection(mySqlConnection "mysql"))

    /// A name for the database of one run, which no other run uses.
    let databaseName () = $"fsharp_linq_{Guid.NewGuid():N}"

    /// Runs every scenario against a database that this run creates on the server and deletes afterwards.
    let run (optionsFor: string -> DbContextOptions<ShopContext>) : Observation list =
        let options = optionsFor (databaseName ())

        try
            Shop.seed options
            Runner.run (EfCore.execute options)
        finally
            use context = new ShopContext(options)
            context.Database.EnsureDeleted() |> ignore
