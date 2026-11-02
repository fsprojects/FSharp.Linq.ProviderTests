namespace FSharp.Linq.ProviderTests

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Text.RegularExpressions

/// What happened to one scenario with one provider.
[<RequireQualifiedAccess>]
type Status =
    /// The provider translated the query and, where it ran the query, returned the expected rows.
    | Ok
    /// The provider ran the query and returned other rows than the expected ones.
    | Wrong
    /// The provider failed. The cause is the type of the exception.
    | Fail of cause: string
    /// The provider cannot be asked this scenario, for example a value computed at once for a provider that is
    /// only asked for the text of its SQL.
    | NotApplicable

    member this.Text =
        match this with
        | Status.Ok -> "ok"
        | Status.Wrong -> "wrong"
        | Status.Fail cause -> $"fail {cause}"
        | Status.NotApplicable -> "n/a"

module Status =

    let tryParse (text: string) : Status voption =
        match text with
        | "ok" -> ValueSome Status.Ok
        | "wrong" -> ValueSome Status.Wrong
        | "n/a" -> ValueSome Status.NotApplicable
        | _ when text.StartsWith("fail ", StringComparison.Ordinal) -> ValueSome(Status.Fail(text.Substring "fail ".Length))
        | _ -> ValueNone

/// The outcome of one scenario with one provider: a line of a baseline.
type Observation =
    {
        Section: string
        Name: string
        Status: Status
        /// The SQL the provider produced, for a scenario that was translated; the first line of the message of the
        /// exception, for one that failed.
        Detail: string
    }

    member this.Key = $"{this.Section}\t{this.Name}"

/// Text of SQL and messages as a baseline keeps it.
module Text =

    let private whitespace = Regex(@"\s+", RegexOptions.Compiled)

    // The compiler names the parameters of lambdas it generates _arg1, arg62 and arg0_0. EF Core and SQLProvider
    // turn them into aliases, and the counter changes with the code around the query, not with the query.
    let private counters =
        Regex(@"(?<![A-Za-z0-9])(_?arg)\d+(?:_\d+)?(?![A-Za-z0-9])", RegexOptions.Compiled)

    /// SQL on one line, without the counters of generated names.
    let normalizeSql (sql: string) =
        counters.Replace(whitespace.Replace(sql, " ").Trim(), "$1N")

    // Servers name each request: Cosmos puts the identifier of the activity into its messages
    let private guids =
        Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled)

    // The Cosmos SDK ends its messages with the system and its own version: ", Windows/10.0.26300 cosmos-netstandard-sdk/3.44.1"
    let private userAgent =
        Regex(@",\s*\S+/\S+ cosmos-netstandard-sdk/\S+", RegexOptions.Compiled)

    /// A message on one line, without what differs between runs and machines, cut to a length that a baseline can
    /// carry.
    let message (text: string) =
        let line =
            guids.Replace(userAgent.Replace(whitespace.Replace(text, " ").Trim(), ""), "<guid>")

        if line.Length > 300 then line.Substring(0, 300) + "..." else line

module Repository =

    let private marker = "FSharp.Linq.ProviderTests.slnx"

    /// The directory of the repository: the first one above the test binaries that holds the solution file.
    let root () : string =
        let rec up (directory: DirectoryInfo) =
            match directory with
            | null -> invalidOp $"No directory above {AppContext.BaseDirectory} holds {marker}."
            | directory when File.Exists(Path.Combine(directory.FullName, marker)) -> directory.FullName
            | directory -> up directory.Parent

        up (DirectoryInfo AppContext.BaseDirectory)

    /// Where a run writes what it observed, in the format of a baseline.
    let actualDirectory () =
        Path.Combine(root (), "artifacts", "actual")

/// The FSharp.Core that the tests run against.
type FSharpCoreVersion =
    {
        /// The informational version of the assembly, with the hash of the commit it was built from:
        /// 10.1.402-servicing.26505.111+0123abc. The builds of two pull requests to dotnet/fsharp have the same
        /// version number and not the same hash.
        Text: string
        /// The major and minor part: the name of the directory of the baselines.
        Series: Version
    }

module FSharpCoreVersion =

    let private leading = Regex(@"^(\d+)\.(\d+)", RegexOptions.Compiled)

    let current () : FSharpCoreVersion =
        let assembly = typeof<unit>.Assembly

        let text =
            match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
            | null -> assembly.GetName().Version.ToString()
            | attribute -> attribute.InformationalVersion

        match leading.Match text with
        | m when m.Success -> { Text = text; Series = Version(int m.Groups[1].Value, int m.Groups[2].Value) }
        | _ -> invalidOp $"Cannot read the version of FSharp.Core from '{text}'."

/// How a run is compared with the baseline.
[<RequireQualifiedAccess>]
type Mode =
    /// The FSharp.Core is the one the baseline was recorded with: every difference is an error, so that a
    /// change in a provider or in FSharp.Core shows up and the baseline is refreshed on purpose.
    | Exact
    /// Another FSharp.Core, for example the one a pull request to dotnet/fsharp builds: an error only if
    /// something that worked stopped working, or the scenario is gone.
    | Regressions

type Verdict =
    {
        Passed: bool
        /// Everything worth reading, also when the test passes.
        Report: string
    }

module Baseline =

    /// The variable that makes a run rewrite the baseline. It is ignored on a CI server.
    [<Literal>]
    let UpdateVariable = "FSHARP_LINQ_UPDATE_BASELINES"

    let private isSet (name: string) =
        not (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable name))

    let private runsOnServer () = isSet "CI" || isSet "TF_BUILD"

    let private updateRequested () =
        not (runsOnServer ())
        && String.Equals(Environment.GetEnvironmentVariable UpdateVariable, "1", StringComparison.Ordinal)

    /// A baseline directory for the series: its own, or the nearest one below it, or else the lowest one above.
    let private directoryFor (series: Version) : string voption =
        let baselines = Path.Combine(Repository.root (), "baselines")

        let available =
            [| if Directory.Exists baselines then
                   for directory in Directory.GetDirectories baselines do
                       match Version.TryParse(Path.GetFileName directory) with
                       | true, version -> struct (version, directory)
                       | _ -> () |]
            |> Array.sortBy (fun struct (version, _) -> version)

        let below = available |> Array.filter (fun struct (version, _) -> version <= series)

        match below, available with
        | [||], [||] -> ValueNone
        | [||], _ ->
            let struct (_, lowest) = available[0]
            ValueSome lowest
        | _ ->
            let struct (_, nearest) = below[below.Length - 1]
            ValueSome nearest

    let private header (version: FSharpCoreVersion) (provider: string) =
        [ $"# FSharp.Core {version.Text}"; $"# provider {provider}" ]

    let private format (version: FSharpCoreVersion) (provider: string) (observations: Observation list) =
        let text = StringBuilder()

        for line in header version provider do
            text.Append(line).Append('\n') |> ignore

        for o in observations do
            text.Append($"{o.Section}\t{o.Name}\t{o.Status.Text}\t{o.Detail}").Append('\n') |> ignore

        text.ToString()

    /// The FSharp.Core a baseline was recorded with, and its lines.
    let private read (path: string) : string voption * Observation list =
        let mutable recorded = ValueNone
        let observations = List<Observation>()

        for line in File.ReadAllLines path do
            if line.StartsWith("# FSharp.Core ", StringComparison.Ordinal) then
                recorded <- ValueSome(line.Substring "# FSharp.Core ".Length)
            elif line.Length > 0 && line[0] <> '#' then
                let parts = line.Split '\t'

                if parts.Length < 3 then
                    invalidOp $"{path}: cannot read the line: {line}"

                match Status.tryParse parts[2] with
                | ValueSome status ->
                    observations.Add
                        { Section = parts[0]
                          Name = parts[1]
                          Status = status
                          Detail = if parts.Length > 3 then parts[3] else "" }
                | ValueNone -> invalidOp $"{path}: cannot read the status '{parts[2]}' in: {line}"

        recorded, List.ofSeq observations

    let private shorten (text: string) =
        if text.Length > 300 then text.Substring(0, 300) + "..." else text

    /// Compares what a run observed with the baseline.
    let compare (mode: Mode) (baseline: Observation list) (actual: Observation list) : Verdict =
        let actualByKey = Dictionary<string, Observation>(StringComparer.Ordinal)

        for o in actual do
            actualByKey[o.Key] <- o

        let baselineKeys = HashSet<string>(baseline |> Seq.map _.Key, StringComparer.Ordinal)

        let regressions = List<string>()
        let improvements = List<string>()
        let differences = List<string>()
        let missing = List<string>()
        let added = List<string>()

        let line (b: Observation) (a: Observation) =
            let detail =
                match a.Status with
                | Status.Fail _ -> shorten a.Detail
                | _ when a.Detail <> b.Detail -> $"{shorten b.Detail}\n        -> {shorten a.Detail}"
                | _ -> ""

            let tail = if detail = "" then "" else $"\n        {detail}"
            $"  [{b.Section}] {b.Name}: {b.Status.Text} -> {a.Status.Text}{tail}"

        for b in baseline do
            match actualByKey.TryGetValue b.Key with
            | false, _ -> missing.Add $"  [{b.Section}] {b.Name}"
            | true, a ->
                match b.Status, a.Status with
                | Status.Ok, Status.Ok -> if a.Detail <> b.Detail then differences.Add(line b a)
                | Status.Ok, _ -> regressions.Add(line b a)
                // A query that failed loudly now returns wrong rows without a word
                | Status.Fail _, Status.Wrong -> regressions.Add(line b a)
                | _, Status.Ok -> improvements.Add(line b a)
                | Status.Wrong, Status.Wrong -> if a.Detail <> b.Detail then differences.Add(line b a)
                | Status.Fail before, Status.Fail after -> if before <> after then differences.Add(line b a)
                | Status.NotApplicable, Status.NotApplicable -> ()
                | _ -> differences.Add(line b a)

        for a in actual do
            if not (baselineKeys.Contains a.Key) then
                added.Add $"  [{a.Section}] {a.Name}: {a.Status.Text}"

        let report = StringBuilder()

        let section (title: string) (lines: List<string>) =
            if lines.Count > 0 then
                report.Append($"{lines.Count} {title}:\n").Append(String.Join("\n", lines)).Append('\n') |> ignore

        section "regressions (worked in the baseline, does not now)" regressions
        section "scenarios of the baseline that were not run" missing
        section "improvements (did not work in the baseline, does now)" improvements
        section "other differences" differences
        section "scenarios that are not in the baseline" added

        let passed =
            match mode with
            | Mode.Exact -> regressions.Count + missing.Count + improvements.Count + differences.Count + added.Count = 0
            | Mode.Regressions -> regressions.Count + missing.Count = 0

        { Passed = passed
          Report = report.ToString() }

    /// Records what a run observed, compares it with the baseline of the series of this FSharp.Core and says
    /// whether the run passes. When there is no baseline yet, <paramref name="allowNoBaseline"/> lets it pass.
    let verify (provider: string) (allowNoBaseline: bool) (actual: Observation list) : Verdict =
        let version = FSharpCoreVersion.current ()

        let actualDirectory = Repository.actualDirectory ()
        Directory.CreateDirectory actualDirectory |> ignore
        File.WriteAllText(Path.Combine(actualDirectory, $"{provider}.tsv"), format version provider actual)

        let summary =
            let count (predicate: Status -> bool) = actual |> List.filter (_.Status >> predicate) |> List.length

            $"{provider} with FSharp.Core {version.Text}: {actual.Length} scenarios, "
            + $"{count _.IsOk} ok, {count _.IsWrong} wrong, "
            + $"{count _.IsFail} failed, {count _.IsNotApplicable} not applicable.\n"

        if updateRequested () then
            let directory = Path.Combine(Repository.root (), "baselines", $"{version.Series.Major}.{version.Series.Minor}")
            Directory.CreateDirectory directory |> ignore
            let path = Path.Combine(directory, $"{provider}.tsv")
            File.WriteAllText(path, format version provider actual)

            { Passed = true
              Report = summary + $"The baseline was rewritten: {path}\n" }
        else
            match directoryFor version.Series with
            | ValueNone when allowNoBaseline ->
                { Passed = true
                  Report = summary + $"There is no baseline yet. Set {UpdateVariable}=1 to record one.\n" }
            | ValueNone ->
                { Passed = false
                  Report = summary + $"There is no baseline. Set {UpdateVariable}=1 to record one.\n" }
            | ValueSome directory ->
                let path = Path.Combine(directory, $"{provider}.tsv")

                if not (File.Exists path) then
                    { Passed = allowNoBaseline
                      Report = summary + $"There is no baseline {path}. Set {UpdateVariable}=1 to record one.\n" }
                else
                    let recorded, baseline = read path

                    let mode =
                        match recorded with
                        | ValueSome text when String.Equals(text, version.Text, StringComparison.Ordinal) -> Mode.Exact
                        | _ -> Mode.Regressions

                    let recordedText =
                        match recorded with
                        | ValueSome text -> text
                        | ValueNone -> "an unknown version"

                    let verdict = compare mode baseline actual

                    let intro =
                        match mode with
                        | Mode.Exact -> $"Compared with {path}, recorded with this FSharp.Core: every difference counts.\n"
                        | Mode.Regressions ->
                            $"Compared with {path}, recorded with FSharp.Core {recordedText}: only regressions count.\n"

                    { verdict with
                        Report = summary + intro + verdict.Report
                        Passed = verdict.Passed }
