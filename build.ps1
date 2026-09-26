[CmdletBinding()]
param(
    [switch]$SkipSelfTest
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = Join-Path $root "src\DesktopNest"
$assetDirectory = Join-Path $root "assets"
$outputDirectory = Join-Path $root "dist"
$iconPath = Join-Path $assetDirectory "DesktopNest.ico"
$manifestPath = Join-Path $sourceDirectory "app.manifest"
$outputPath = Join-Path $outputDirectory "DesktopNest.exe"

$compilerCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ([String]::IsNullOrWhiteSpace($compiler)) {
    throw "The Windows C# compiler was not found."
}
$frameworkDirectory = Split-Path -Parent $compiler
$wpfReferenceDirectory = Join-Path $frameworkDirectory "WPF"
if (-not (Test-Path -LiteralPath $wpfReferenceDirectory)) {
    throw "The Windows Presentation Foundation reference assemblies were not found."
}

[System.IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

& powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File (Join-Path $root "tools\GenerateIcon.ps1") `
    -OutputPath $iconPath
if ($LASTEXITCODE -ne 0) {
    throw "Icon generation failed."
}

$sources = @(
    Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.cs" -File |
        Sort-Object Name |
        ForEach-Object { $_.FullName }
)

$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll",
    "System.Web.Extensions.dll",
    (Join-Path $wpfReferenceDirectory "PresentationCore.dll"),
    (Join-Path $wpfReferenceDirectory "WindowsBase.dll"),
    (Join-Path $frameworkDirectory "System.Xaml.dll")
)

$compilerArguments = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/warn:4",
    "/codepage:65001",
    "/utf8output",
    "/win32icon:$iconPath",
    "/win32manifest:$manifestPath",
    "/out:$outputPath"
)

foreach ($reference in $references) {
    $compilerArguments += "/reference:$reference"
}
$compilerArguments += $sources

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Compiling DesktopNest.exe failed. Exit code: $LASTEXITCODE"
}

if (-not $SkipSelfTest) {
    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = $outputPath
    $processInfo.Arguments = "--self-test"
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $processInfo
    $process.Start() | Out-Null
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if (-not [String]::IsNullOrWhiteSpace($standardOutput)) {
        Write-Output $standardOutput.TrimEnd()
    }
    if (-not [String]::IsNullOrWhiteSpace($standardError)) {
        Write-Error $standardError.TrimEnd()
    }
    if ($process.ExitCode -ne 0) {
        throw "DesktopNest --self-test failed. Exit code: $($process.ExitCode)"
    }
}

Write-Output "Build complete: $outputPath"
