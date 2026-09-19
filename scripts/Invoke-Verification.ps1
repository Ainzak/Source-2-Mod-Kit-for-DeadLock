[CmdletBinding()]
param(
    [ValidateSet('build', 'test', 'all')]
    [string] $Mode = 'all',

    [string] $Solution = (Join-Path (Split-Path -Parent $PSScriptRoot) 'S2ModKit.slnx'),

    [ValidateRange(1, 1000000)]
    [int] $MinimumExpectedTests = 524
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:VSLANG = '1033'
$env:MSBUILDDISABLENODEREUSE = '1'

if (-not (Test-Path -LiteralPath $Solution -PathType Leaf)) {
    throw "Solution was not found: $Solution"
}

function Invoke-QuietDotNet {
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = @(& dotnet @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Output "OK: $Label"
        return
    }

    [Console]::Error.WriteLine("FAILED: $Label (dotnet exit code $exitCode)")
    foreach ($line in $output) {
        [Console]::Error.WriteLine($line)
    }

    $meaningfulOutput = @($output | Where-Object {
        $text = $_.Trim()
        $text.Length -gt 0 `
            -and $text -notmatch '^Build FAILED\.$' `
            -and $text -notmatch '^\d+ Warning\(s\)$' `
            -and $text -notmatch '^\d+ Error\(s\)$' `
            -and $text -notmatch '^Time Elapsed '
    })
    if ($meaningfulOutput.Count -eq 0) {
        [Console]::Error.WriteLine(
            'dotnet returned a non-zero exit code without an actionable diagnostic. The command ran with one MSBuild worker and build-server reuse disabled; inspect system resource limits or rerun the failing dotnet command with --verbosity normal.')
    }

    exit $exitCode
}

if ($Mode -in @('build', 'all')) {
    Invoke-QuietDotNet -Label 'Release build' -Arguments @(
        'build', $Solution, '--configuration', 'Release', '--no-restore', '--disable-build-servers',
        '--verbosity', 'quiet', '-maxcpucount:1', '-nodeReuse:false')
}

if ($Mode -in @('test', 'all')) {
    Invoke-QuietDotNet -Label "portable tests (minimum $MinimumExpectedTests)" -Arguments @(
        'test', $Solution, '--configuration', 'Release', '--no-build', '--minimum-expected-tests', $MinimumExpectedTests,
        '--verbosity', 'quiet')
}
