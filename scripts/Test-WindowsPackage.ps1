#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [switch]$FrameworkDependent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The Windows package must be tested on Windows.' }

$archivePath = (Resolve-Path -LiteralPath $ZipPath).Path
$outputRoot = Split-Path -Parent $archivePath
$verificationRoot = Join-Path $outputRoot ('.verify-' + [guid]::NewGuid().ToString('N'))
$appProcess = $null
try {
    $exePath = Join-Path $verificationRoot 'ShakeBigMouse.exe'
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'ShakeBigMouse.exe') {
            throw 'Expected exactly ShakeBigMouse.exe at the root of the ZIP.'
        }
        [IO.Directory]::CreateDirectory($verificationRoot) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $exePath)
    } finally { $archive.Dispose() }
    # --show exits before creating a tray app when no primary instance is running.
    $startInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
    $startInfo.ArgumentList.Add('--show')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardError = $true
    if (!$FrameworkDependent) {
        $startInfo.Environment['DOTNET_ROOT'] = Join-Path $verificationRoot 'no-installed-runtime'
        $startInfo.Environment['DOTNET_ROOT_X64'] = $startInfo.Environment['DOTNET_ROOT']
    }
    $appProcess = [Diagnostics.Process]::Start($startInfo)
    $standardError = $appProcess.StandardError.ReadToEndAsync()
    if (!$appProcess.WaitForExit(20000)) {
        $appProcess.Kill()
        $appProcess.WaitForExit()
        throw "Packaged --show command did not exit within 20 seconds: $($standardError.GetAwaiter().GetResult())"
    }
    if ($appProcess.ExitCode -ne 0) {
        throw "Packaged executable exited with code $($appProcess.ExitCode): $($standardError.GetAwaiter().GetResult())"
    }
    Write-Host "Verified Windows package: $archivePath"
}
finally {
    if ($appProcess) {
        if (!$appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
        $appProcess.Dispose()
    }
    if (Test-Path -LiteralPath $verificationRoot) {
        $resolvedVerification = (Resolve-Path -LiteralPath $verificationRoot).Path
        $outputBoundary = $outputRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedVerification.StartsWith($outputBoundary, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedVerification) -notlike '.verify-*') {
            throw 'Refusing to clean a verification directory outside the package output directory.'
        }
        Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
    }
}
