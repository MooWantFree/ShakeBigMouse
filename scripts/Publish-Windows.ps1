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
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

Push-Location -LiteralPath $repositoryRoot
try {
    $packages = foreach ($selfContained in $true, $false) {
        $name = if ($selfContained) { 'ShakeBigMouse-win-x64' } else { 'ShakeBigMouse-win-x64-framework-dependent' }
        $publishDirectory = Join-Path $stagingRoot $name
        $publishArguments = @(
            'publish', './ShakeToFindCursor/ShakeToFindCursor.csproj',
            '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', $selfContained.ToString().ToLowerInvariant(),
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
            '--output', $publishDirectory
        )
        & $dotnet @publishArguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($name) failed with exit code $LASTEXITCODE." }

        $configPath = './ShakeToFindCursor/bin/Release/net10.0-windows/win-x64/ShakeBigMouse.runtimeconfig.json'
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json -AsHashtable
        $frameworkKey = if ($selfContained) { 'includedFrameworks' } else { 'frameworks' }
        $frameworks = $config.runtimeOptions[$frameworkKey]
        if (!$frameworks -or 'Microsoft.WindowsDesktop.App' -notin $frameworks.name) {
            throw "$name has the wrong desktop runtime configuration."
        }

        $exePath = Join-Path $publishDirectory 'ShakeBigMouse.exe'
        $stagedZip = Join-Path $stagingRoot ($name + '.zip')
        Compress-Archive -LiteralPath $exePath -DestinationPath $stagedZip -CompressionLevel Optimal
        [pscustomobject]@{ ZipPath = Join-Path $outputRoot ($name + '.zip'); Runtime = 'win-x64'; SelfContained = $selfContained }
    }

    foreach ($package in $packages) {
        $stagedZip = Join-Path $stagingRoot (Split-Path -Leaf $package.ZipPath)
        [IO.File]::Move($stagedZip, $package.ZipPath, $true)
        Write-Host "Created $($package.ZipPath)"
        $package
    }
}
finally {
    Pop-Location
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
