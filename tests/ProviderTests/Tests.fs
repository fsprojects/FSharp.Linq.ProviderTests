namespace FSharp.Linq.ProviderTests

open Xunit

module AssemblyFixtures =

    // Created before any test runs
    [<assembly: AssemblyFixture(typeof<MongoClassMaps>)>]
    do ()

module Verify =

    /// Checks what a provider did with every scenario against the baseline of the provider.
    let provider (name: string) (allowNoBaseline: bool) (observations: Observation list) =
        let verdict = Baseline.verify name allowNoBaseline observations

        match TestContext.Current.TestOutputHelper with
        | null -> ()
        | output -> output.WriteLine verdict.Report

        Assert.True(verdict.Passed, verdict.Report)

    /// Checks a provider on a server when the server answers. Skips the test when it does not, or fails it when the
    /// server has to be there.
    let onServer (server: Server) (probe: unit -> Result<unit, string>) (name: string) (run: unit -> Observation list) =
        match probe () with
        | Error reason when Server.isRequired server ->
            failwith
                $"No {server.Name} answers at {Server.describe server} ({reason}), and {server.RequiredVariable} says that one has to."
        | Error reason -> Assert.Skip $"No {server.Name} answers at {Server.describe server}: {reason}"
        | Ok() -> provider name false (run ())

module EfCoreTests =

    [<Fact>]
    let ``EF Core SQLite`` () =
        use shop = new SqliteShop()
        Verify.provider "efcore-sqlite" false (Runner.run (EfCore.sqlite shop))

    [<Fact>]
    let ``EF Core SQLite with ToListAsync`` () =
        use shop = new SqliteShop()
        Verify.provider "efcore-sqlite-async" false (Runner.run (EfCore.sqliteAsync shop))

    [<Fact>]
    let ``EF Core SQL Server`` () =
        Verify.provider "efcore-sqlserver" false (Runner.run (EfCore.sqlOnly EfCore.sqlServerOptions))

    [<Fact>]
    let ``EF Core PostgreSQL`` () =
        Verify.provider "efcore-postgresql" false (Runner.run (EfCore.sqlOnly EfCore.postgreSqlOptions))

    [<Fact>]
    let ``EF Core MySQL`` () =
        Verify.provider "efcore-mysql" false (Runner.run (EfCore.sqlOnly EfCore.mySqlOptions))

/// The tests that need a server in a container. CI runs them in a job of their own, which starts the servers.
module ServerTests =

    [<Fact>]
    let ``EF Core SQL Server on a server`` () =
        Verify.onServer EfCoreServer.sqlServer EfCoreServer.probeSqlServer "efcore-sqlserver-live" (fun () ->
            EfCoreServer.run EfCoreServer.sqlServerOptions)

    [<Fact>]
    let ``EF Core PostgreSQL on a server`` () =
        Verify.onServer EfCoreServer.postgreSql EfCoreServer.probePostgreSql "efcore-postgresql-live" (fun () ->
            EfCoreServer.run EfCoreServer.postgreSqlOptions)

    [<Fact>]
    let ``EF Core MySQL on a server`` () =
        Verify.onServer EfCoreServer.mySql EfCoreServer.probeMySql "efcore-mysql-live" (fun () ->
            EfCoreServer.run EfCoreServer.mySqlOptions)

    [<Fact>]
    let ``MongoDB driver on a server`` () =
        Verify.onServer Mongo.server Mongo.probe "mongodb-driver-live" Mongo.run

    [<Fact>]
    let ``EF Core MongoDB on a server`` () =
        Verify.onServer Mongo.server Mongo.probe "efcore-mongodb-live" EfCoreMongo.run

module MongoTests =

    [<Fact>]
    let ``MongoDB driver without a server`` () =
        Verify.provider "mongodb-driver-offline" false (Runner.run Mongo.offline)

module Linq2DbTests =

    [<Fact>]
    let ``linq2db SQLite`` () =
        use shop = new SqliteShop()
        Verify.provider "linq2db-sqlite" false (Runner.run (Linq2Db.sqlite shop))

module CosmosTests =

    [<Fact>]
    let ``Cosmos without a server`` () =
        Verify.provider "cosmos-offline" false (Runner.run Cosmos.offline)

    [<Fact>]
    let ``Cosmos emulator`` () =
        match CosmosEmulator.probe () with
        | ValueNone when CosmosEmulator.isRequired () ->
            failwith
                $"No Cosmos DB Emulator answers at {CosmosEmulator.endpoint}, and {CosmosEmulator.RequiredVariable} says that one has to."
        | ValueNone -> Assert.Skip $"No Cosmos DB Emulator answers at {CosmosEmulator.endpoint}."
        | ValueSome kind -> Verify.provider kind.Provider true (CosmosEmulator.run ())

    [<Fact>]
    let ``EF Core Cosmos without a server`` () =
        Verify.provider "efcore-cosmos-offline" false (Runner.run EfCoreCosmos.offline)

    [<Fact>]
    let ``EF Core Cosmos emulator`` () =
        match CosmosEmulator.probe () with
        | ValueNone when CosmosEmulator.isRequired () ->
            failwith
                $"No Cosmos DB Emulator answers at {CosmosEmulator.endpoint}, and {CosmosEmulator.RequiredVariable} says that one has to."
        | ValueNone -> Assert.Skip $"No Cosmos DB Emulator answers at {CosmosEmulator.endpoint}."
        | ValueSome kind -> Verify.provider $"efcore-{kind.Provider}" true (EfCoreCosmos.run ())
