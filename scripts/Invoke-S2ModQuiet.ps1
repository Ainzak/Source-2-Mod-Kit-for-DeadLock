[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Cli,

    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]] $S2ModArguments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:VSLANG = '1033'

if (-not (Test-Path -LiteralPath $Cli -PathType Leaf)) {
    throw "S2ModKit CLI was not found: $Cli"
}

if ($S2ModArguments.Count -eq 0) {
    throw 'Supply at least one S2ModKit command argument.'
}

if ([System.IO.Path]::GetExtension($Cli) -eq '.dll') {
    $output = @(& dotnet $Cli @S2ModArguments 2>&1 | ForEach-Object { $_.ToString() })
}
else {
    $output = @(& $Cli @S2ModArguments 2>&1 | ForEach-Object { $_.ToString() })
}

$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    [Console]::Error.WriteLine("FAILED: s2mod (exit code $exitCode)")
    foreach ($line in $output) {
        [Console]::Error.WriteLine($line)
    }

    if ($output.Count -eq 0 -or -not ($output | Where-Object { $_.Trim().Length -gt 0 })) {
        [Console]::Error.WriteLine(
            'The CLI returned a non-zero exit code without a diagnostic. Rerun the same command directly to investigate the host or process failure.')
    }

    exit $exitCode
}

Write-Output "OK: s2mod $($S2ModArguments -join ' ')"
