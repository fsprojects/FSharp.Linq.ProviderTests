#Requires -Version 7
<#
.SYNOPSIS
    Copies the query scenarios from a checkout of dotnet/fsharp into src/Scenarios.

.DESCRIPTION
    The scenarios are written and kept in dotnet/fsharp, in
    tests/FSharp.Core.UnitTests/FSharp.Core/Microsoft.FSharp.Linq. This repository runs the same scenarios against
    real LINQ providers. The files are copied without changes, except for the first line when it is the Microsoft
    copyright header: this repository has its own license.

    SOURCE.txt records the commit that the files come from.

.PARAMETER FSharpRepo
    The dotnet/fsharp checkout, for example D:\DotNet\fsharp.

.EXAMPLE
    ./scripts/sync-scenarios.ps1 -FSharpRepo D:\DotNet\fsharp
#>
param(
    [Parameter(Mandatory)] [string] $FSharpRepo
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $FSharpRepo 'tests/FSharp.Core.UnitTests/FSharp.Core/Microsoft.FSharp.Linq'
if (-not (Test-Path $source)) { throw "Not a dotnet/fsharp checkout, or one without the scenarios: $source" }

$target = Join-Path $PSScriptRoot '../src/Scenarios' | Resolve-Path
$files = @('QueryProviderShape.fs', 'QueryScenarioDomain.fs', 'QueryScenario.fs') +
    (Get-ChildItem $source -Filter 'Query*Scenarios.fs' | Sort-Object Name | ForEach-Object Name)

foreach ($name in $files) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $source $name))
    if ($lines.Length -gt 0 -and $lines[0].StartsWith('// Copyright (c) Microsoft Corporation', [StringComparison]::Ordinal)) {
        $lines = $lines | Select-Object -Skip 1
        # The header is followed by an empty line
        if ($lines.Length -gt 0 -and $lines[0] -eq '') { $lines = $lines | Select-Object -Skip 1 }
    }
    [System.IO.File]::WriteAllText((Join-Path $target $name), ([string]::Join("`n", $lines) + "`n"))
    Write-Host "copied $name"
}

$commit = git -C $FSharpRepo rev-parse HEAD
$branch = git -C $FSharpRepo branch --show-current
@(
    "dotnet/fsharp tests/FSharp.Core.UnitTests/FSharp.Core/Microsoft.FSharp.Linq"
    "commit $commit"
    "branch $branch"
    "files $($files -join ', ')"
) | Set-Content (Join-Path $target 'SOURCE.txt')
Write-Host "source: $branch $commit"
