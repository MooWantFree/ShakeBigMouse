#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\windows'),
    [string]$DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'ShakeBigMouse can only be packaged on Windows.' }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command -Name $DotNetPath -CommandType Application -ErrorAction Stop).Source
$stagingRoot = Join-Path $outputRoot ('.package-' + [guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $stagingRoot 'publish'
$packageName = 'ShakeBigMouse-win-x64'
$packageDirectory = Join-Path $stagingRoot $packageName
$zipPath = Join-Path $outputRoot ($packageName + '.zip')
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

try {
    $publishArguments = @(
        'publish', (Join-Path $repositoryRoot 'ShakeToFindCursor\ShakeToFindCursor.csproj'),
        '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
        '--output', $publishDirectory
    )
    Push-Location -LiteralPath $repositoryRoot
    try {
        & $dotnet @publishArguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }

    $exePath = Join-Path $publishDirectory 'ShakeBigMouse.exe'
    if (!(Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Published executable is missing.' }
    $runtimeConfigPath = Join-Path $repositoryRoot 'ShakeToFindCursor\bin\Release\net10.0-windows\win-x64\ShakeBigMouse.runtimeconfig.json'
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    $includedFrameworks = $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']
    if (!$includedFrameworks -or @($includedFrameworks.Value).Count -eq 0) {
        throw 'The published application is not self-contained.'
    }

    Get-ChildItem -LiteralPath $publishDirectory -Force | Copy-Item -Destination $packageDirectory -Recurse -Force
    foreach ($readme in @('README.md', 'README.zh-CN.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $readme) -Destination $packageDirectory
    }
    $stagedZip = Join-Path $stagingRoot ($packageName + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $stagedZip,
        [IO.Compression.CompressionLevel]::Optimal, $true)
    Copy-Item -LiteralPath $stagedZip -Destination $zipPath -Force
    Write-Host "Created $zipPath"
    [pscustomobject]@{ ZipPath = $zipPath; Runtime = 'win-x64'; SelfContained = $true }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot).Path
        $outputBoundary = $outputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedStaging.StartsWith($outputBoundary, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedStaging) -notlike '.package-*') {
            throw 'Refusing to clean a staging directory outside the package output directory.'
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
