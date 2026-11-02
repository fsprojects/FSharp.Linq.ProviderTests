/// The scenarios of dotnet/fsharp, by section. The sections are listed here, not found by reflection, so that a
/// section that goes missing in a sync is a compile error and not a run with fewer scenarios.
module FSharp.Linq.ProviderTests.AllScenarios

open FSharp.Core.UnitTests.Linq

/// Scenarios that are not checked here: their expected rows are a placeholder, because they exist to show that the
/// result of the query depends on something the scenario cannot say.
let private excluded =
    set [ "Select written in the query over a member that cannot always be read" ]

/// Every section with its scenarios, in the order of the sections in dotnet/fsharp.
let sections () : (string * Scenario list) list =
    [ "Projection", QueryProjectionScenarios.cases ()
      "Operator", QueryOperatorScenarios.cases ()
      "Key", QueryKeyScenarios.cases ()
      "Structure", QueryStructureScenarios.cases ()
      "MethodSyntax", QueryMethodSyntaxScenarios.cases ()
      "FSharpOperator", QueryFSharpOperatorScenarios.cases () ]
    |> List.map (fun (section, scenarios) ->
        section, scenarios |> List.filter (fun scenario -> not (excluded.Contains scenario.Name)))
