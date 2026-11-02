namespace FSharp.Linq.ProviderTests

open Xunit

module Verify =

    /// Checks what a provider did with every scenario against the baseline of the provider.
    let provider (name: string) (allowNoBaseline: bool) (observations: Observation list) =
        let verdict = Baseline.verify name allowNoBaseline observations

        match TestContext.Current.TestOutputHelper with
        | null -> ()
        | output -> output.WriteLine verdict.Report

        Assert.True(verdict.Passed, verdict.Report)

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
