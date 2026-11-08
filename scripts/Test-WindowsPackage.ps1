#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ZipPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The Windows package must be tested on Windows.' }

$archivePath = (Resolve-Path -LiteralPath $ZipPath).Path
$outputRoot = Split-Path -Parent $archivePath
$verificationRoot = Join-Path $outputRoot ('.verify-' + [guid]::NewGuid().ToString('N'))
$appProcess = $null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $verificationRoot
    $packageRoot = Join-Path $verificationRoot 'ShakeBigMouse-win-x64'
    $exePath = Join-Path $packageRoot 'ShakeBigMouse.exe'
    foreach ($required in @('ShakeBigMouse.exe', 'README.md', 'README.zh-CN.md', 'LICENSE')) {
        if (!(Test-Path -LiteralPath (Join-Path $packageRoot $required) -PathType Leaf)) {
            throw "Missing packaged file: $required"
        }
    }
    # --show exits before creating a tray app when no primary instance is running.
    $startInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
    $startInfo.ArgumentList.Add('--show')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DOTNET_ROOT'] = Join-Path $verificationRoot 'no-installed-runtime'
    $startInfo.Environment['DOTNET_ROOT_X64'] = $startInfo.Environment['DOTNET_ROOT']
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
