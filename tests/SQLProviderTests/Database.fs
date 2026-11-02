namespace FSharp.Linq.ProviderTests.SQLProviderTests

open FSharp.Data.Sql
open FSharp.Data.Sql.SQLite

module Database =

    [<Literal>]
    let ConnectionString = "Data Source=" + __SOURCE_DIRECTORY__ + "/../../db/shop.db;Mode=ReadOnly"

    [<Literal>]
    let SchemaPath = __SOURCE_DIRECTORY__ + "/../../db/shop.schema"

    type Sql =
        SqlDataProvider<
            Common.DatabaseProviderTypes.SQLITE,
            ConnectionString,
            SQLiteLibrary = Common.SQLiteLibrary.MicrosoftDataSqlite,
            CaseSensitivityChange = Common.CaseSensitivityChange.ORIGINAL,
            UseOptionTypes = Common.NullableColumnType.OPTION,
            ContextSchemaPath = SchemaPath
         >

    let context () = Sql.GetDataContext()

