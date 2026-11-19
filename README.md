# FSharp.Linq.ProviderTests

Checks that F# query expressions, and lambdas that are passed as `Expression<Func<..>>`, translate with real LINQ
providers:

| Provider | What the tests do |
|---|---|
| EF Core on SQLite | run every scenario against an in-memory database and compare the rows, also with `ToListAsync` |
| EF Core on SQL Server and on PostgreSQL | ask for the SQL of every scenario without a server, and run every scenario on a server in a container when one is there |
| Azure Cosmos DB | ask for the SQL of every scenario without a server, and run every scenario on the Azure Cosmos DB Emulator when one is there |
| EF Core on Azure Cosmos DB | the same with the Cosmos provider of EF Core, which translates the queries itself |
| linq2db on SQLite | run every scenario and compare the rows |
| MongoDB, with the LINQ provider of the driver and with EF Core | ask the driver for the pipeline of every scenario without a server, and run every scenario with both on a server in a container when one is there; the EF Core provider cannot give a query without running it |
| SQLProvider on SQLite | run about sixty queries in the style of SQLProvider users against `db/shop.db` and compare the rows |

A compiler or FSharp.Core change that stops a query from translating, or that makes it return other rows, fails the
tests. This is the purpose of the repository: [dotnet/fsharp#20649](https://github.com/dotnet/fsharp/issues/20649)
asks for tests against real providers, which cannot live in the build of the compiler itself because they need NuGet
packages.

## Run

```
dotnet test tests/ProviderTests/FSharp.Linq.ProviderTests.fsproj -c Release
dotnet test tests/SQLProviderTests/FSharp.Linq.ProviderTests.SQLProvider.fsproj -c Release
```

The .NET SDK in `global.json` is needed. FSharp.Core is the implicit one of the SDK. dotnet/fsharp builds the
repository with the compiler and the FSharp.Core it has just built: see [Regression matrix](#regression-matrix).

## Baselines

What a provider can do with a scenario is not a decision of this repository: EF Core cannot translate many of the
scenarios, and Cosmos cannot translate most of them. The result of each scenario is recorded in
`baselines/<FSharp.Core major.minor>/<provider>.tsv`, one line each:

```
Projection	tuple of two	ok	SELECT "c"."Id", "c"."Name" FROM "Customer" AS "c"
```

The columns are the section, the scenario, the status and the SQL, or the first line of the error. The status is `ok`
(translated and, where the provider ran the query, the right rows), `wrong` (the rows are not the expected ones),
`fail <exception type>`, or `n/a` (the provider cannot be asked this scenario, for example a value that is computed
at once, when there is no server).

A test compares a run with the baseline of the series of the FSharp.Core it runs with, or the nearest series below it:

- With **the build of FSharp.Core that recorded the baseline**, every difference is an error. A new SDK, a new provider
  or a changed scenario shows up, and the baseline is refreshed on purpose. The header of a baseline names the build
  (`# build`) by the commit in its version, or by the module version id when the version has no commit: the version
  number alone is not enough, because `main` of dotnet/fsharp and its pull requests share one. `global.json` pins the
  SDK exactly for this reason.
- With **another build**, for example the one that a pull request to dotnet/fsharp makes, only regressions are
  errors: `ok` that is not `ok` any more, `fail` that returns wrong rows, and a scenario that is no longer run.
  Scenarios that start to work and changes of the SQL are printed in the test output.

Every run writes what it saw to `artifacts/actual/<provider>.tsv`. To refresh the baselines, set
`FSHARP_LINQ_UPDATE_BASELINES=1` and run the tests; the variable is ignored when `CI` is set.

## Scenarios

`src/Scenarios` holds the scenarios: queries over `Customer`, `Order`, `OrderLine` and `Product`, and the rows that each
must return. They are copied from the tests of FSharp.Core in dotnet/fsharp
(`tests/FSharp.Core.UnitTests/FSharp.Core/Microsoft.FSharp.Linq`) with `scripts/sync-scenarios.ps1`; `SOURCE.txt`
there says from which commit. These tests are still in pull requests of dotnet/fsharp, so for now the copy comes
from the branch that integrates them. The scenarios of the sections `Projection`, `Operator`, `Key`, `Structure`,
`MethodSyntax` and `FSharpOperator` are run by every provider except SQLProvider.

SQLProvider provides its own types for the tables, so its queries are written by hand in
`tests/SQLProviderTests/Queries.fs`, with the expected rows computed from the same data.

## Azure Cosmos DB Emulator

The test `Cosmos emulator` runs the scenarios against a database that it creates in the emulator and deletes
afterwards. Both emulators work and answer a little differently, so each has its own baseline:
`cosmos-emulator-windows.tsv` and `cosmos-emulator-vnext.tsv`.

| Variable | |
|---|---|
| `COSMOS_EMULATOR_ENDPOINT` | the address of the emulator, `https://127.0.0.1:8081` by default |
| `COSMOS_EMULATOR_KEY` | the key; the documented key of the emulator by default |
| `COSMOS_EMULATOR_REQUIRED` | `1` makes a missing emulator an error; without it the test is skipped. The Linux job of CI sets it |

The vNext emulator runs in Docker: `.github/scripts/start-cosmos-emulator.ps1`.

The test `EF Core Cosmos emulator` does the same with the Cosmos provider of EF Core, with the same variables, and
writes `efcore-cosmos-emulator-windows.tsv` or `efcore-cosmos-emulator-vnext.tsv`. It passes while there is no
baseline yet; the first run with an emulator gives one. The provider refuses synchronous queries unless its warning
`CosmosEventId.SyncNotSupported` is turned off, and the tests turn it off: a query expression enumerates its rows.

## Servers in containers

The tests of the module `ServerTests` run the scenarios on a server, in a database that they create and delete
afterwards, and compare the rows. Each test is skipped when its server does not answer. The job `servers` of CI starts
the servers as service containers and makes a missing one an error. The defaults are the addresses of these
containers, with throwaway passwords:

```
docker run -d -p 1433:1433 -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=FSharp-Linq-1 mcr.microsoft.com/mssql/server:2022-latest
docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=fsharp-linq postgres:17
docker run -d -p 27017:27017 mongo:8
```

| Provider | Address | Variable that makes a missing server an error | Baseline |
|---|---|---|---|
| EF Core on SQL Server | `SQLSERVER_CONNECTION_STRING`, `Server=127.0.0.1,1433;User Id=sa;Password=FSharp-Linq-1;...` by default | `SQLSERVER_REQUIRED=1` | `efcore-sqlserver-live.tsv` |
| EF Core on PostgreSQL | `POSTGRESQL_CONNECTION_STRING`, `Host=127.0.0.1;Port=5432;Username=postgres;Password=fsharp-linq;...` by default | `POSTGRESQL_REQUIRED=1` | `efcore-postgresql-live.tsv` |
| MongoDB, the driver and EF Core | `MONGODB_CONNECTION_STRING`, `mongodb://127.0.0.1:27017` by default | `MONGODB_REQUIRED=1` | `mongodb-driver-live.tsv`, `efcore-mongodb-live.tsv` |

The driver of MongoDB stores an object as it is, so a customer holds its orders and an order its lines, and a
navigation of a scenario reads the embedded documents. The EF Core provider of MongoDB has no relationships between
collections, so its model leaves the navigations out, and the scenarios that use them fail with it.

To run only these tests: `dotnet test tests/ProviderTests/FSharp.Linq.ProviderTests.fsproj -c Release --filter "FullyQualifiedName~ServerTests"`.

## SQLProvider

`db/shop.db` is a SQLite database with the data of the scenarios. `scripts/create-db.fsx` creates it from
`db/shop.sql` and the data. SQLProvider does not read the database while it compiles the project, but
`db/shop.schema`, so that compiling needs no database and no native SQLite library. When `db/shop.sql` or the queries
change, delete `db/shop.schema` and write it again with a database that is there:

```
dotnet fsi scripts/create-db.fsx
dotnet build tests/SQLProviderTests/FSharp.Linq.ProviderTests.SQLProvider.fsproj -c Release -p:SaveSchema=true
```

## Regression matrix

dotnet/fsharp builds third-party repositories with the compiler and the FSharp.Core of a pull request
(`eng/templates/regression-test-jobs.yml`). This repository is made for that, so it keeps to what the matrix needs:

- FSharp.Core only comes from the implicit reference of the SDK: no `FSharp.Core` package reference, no central package
  management, no package source mapping.
- The tests are xUnit v3 with the VSTest runner, because the matrix replaces `global.json` by one without
  a test runner.
- `Directory.Build.props` turns off `ContinuousIntegrationBuild`, which would map `__SOURCE_DIRECTORY__` to a fake
  path, and `NuGetAudit`, so that a new advisory does not break a pinned commit.

An entry for the matrix looks like this:

```yaml
- repo: fsprojects/FSharp.Linq.ProviderTests
  commit: <sha>
  buildScript: dotnet test tests/ProviderTests/FSharp.Linq.ProviderTests.fsproj -c Release
  displayName: FSharpLinqProviderTests
  expectLocalCore: true
```

and a second one, `FSharpLinqProviderTests_SQLProvider`, for `tests/SQLProviderTests`, so that a failure of one does not
hide the results of the other.

When the commit in the matrix changes, check that `baselines/` has a directory for the FSharp.Core of dotnet/fsharp
`main`, and refresh it with the FSharp.Core that `main` builds.

## License

MIT
