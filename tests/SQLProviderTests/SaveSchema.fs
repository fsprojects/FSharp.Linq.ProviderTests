namespace FSharp.Linq.ProviderTests.SQLProviderTests

/// Writes db/shop.schema, the schema that SQLProvider reads instead of the database while it compiles the project,
/// with every table and column that the queries of the project use. The file is compiled last, only for
/// dotnet build -p:SaveSchema=true.
module SaveSchema =

    let write = (Database.context ()).``Design Time Commands``.SaveContextSchema
