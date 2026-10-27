[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputRoot,
    [string] $CMake = 'cmake'
)

# Builds the bundled meshoptimizer codec from a pinned upstream source archive (MIT, zeux/meshoptimizer). The archive
# hash is checked before anything is built; the output folder receives meshoptimizer.dll and meshoptimizer.json
# (version, commit, source hash, binary hash) that Publish-Release.ps1 requires.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$version = '1.3'
$commit = '9e1f07b159d3cb777f1c67ed31fc11fd117986f4'
$sourceSha256 = 'e753b45fbc410031bd69cb278180ae31fa0d5430d0c3e662e3a3b100467ce0b3'
$archiveUrl = "https://codeload.github.com/zeux/meshoptimizer/tar.gz/$commit"

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ((Test-Path -LiteralPath $OutputRoot) -and @(Get-ChildItem -LiteralPath $OutputRoot -Force).Count -gt 0) {
    throw "Output must be a new or empty directory: $OutputRoot"
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$work = Join-Path $OutputRoot '.work'
$source = Join-Path $work 'src'
$build = Join-Path $work 'build'
$archive = Join-Path $work "meshoptimizer-$commit.tar.gz"
New-Item -ItemType Directory -Path $source, $build | Out-Null

Invoke-WebRequest -Uri $archiveUrl -OutFile $archive -UseBasicParsing
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sourceSha256) {
    throw "meshoptimizer source archive hash $actual does not match the pinned $sourceSha256."
}

& (Join-Path $env:SystemRoot 'System32/tar.exe') -xzf $archive -C $source --strip-components=1
if ($LASTEXITCODE -ne 0) { throw "Extracting the source archive failed." }
& $CMake -S $source -B $build -G 'Visual Studio 17 2022' -A x64 -DMESHOPT_BUILD_SHARED_LIBS=ON -DMESHOPT_BUILD_DEMO=OFF -DMESHOPT_BUILD_GLTFPACK=OFF | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }
& $CMake --build $build --config Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

$dll = Join-Path $OutputRoot 'meshoptimizer.dll'
Copy-Item -LiteralPath (Join-Path $build 'Release/meshoptimizer.dll') -Destination $dll
Copy-Item -LiteralPath (Join-Path $source 'LICENSE.md') -Destination (Join-Path $OutputRoot 'meshoptimizer-LICENSE.md')
[ordered]@{
    name = 'meshoptimizer'
    version = $version
    repository = 'https://github.com/zeux/meshoptimizer'
    commit = $commit
    sourceSha256 = $sourceSha256
    license = 'MIT'
    binarySha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'meshoptimizer.json') -Encoding utf8
Remove-Item -LiteralPath $work -Recurse -Force
Write-Output "meshoptimizer $version built: $dll"
