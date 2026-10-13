[CmdletBinding()]
param(
    [string]$ProjectPath = "src/QodexBot/QodexBot.csproj",
    [string]$VersionFile = "src/Directory.Build.props",
    [string]$PackageName = "qodexbot",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts",
    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = $PSScriptRoot
$projectFullPath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot $ProjectPath))
$versionFileFullPath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot $VersionFile))

if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "Project file not found: $projectFullPath"
}

if (-not (Test-Path -LiteralPath $versionFileFullPath)) {
    throw "Version file not found: $versionFileFullPath"
}

[xml]$versionXml = Get-Content -LiteralPath $versionFileFullPath
$versionNode = $versionXml.SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($null -ne $versionNode) { [string]$versionNode.InnerText } else { '' }
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version not found in $versionFileFullPath"
}

$outputRootFullPath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot $OutputRoot))
$publishRoot = Join-Path $outputRootFullPath "publish"
$publishDir = Join-Path (Join-Path $publishRoot "win-x64") "aot"
$packagesRoot = Join-Path $outputRootFullPath "packages"
$zipFileName = "$PackageName-win-x64-aot-$version.zip"
$zipFullPath = Join-Path $packagesRoot $zipFileName
$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force -Confirm:$false
}
if (Test-Path -LiteralPath $zipFullPath) {
    Remove-Item -LiteralPath $zipFullPath -Force -Confirm:$false
}

$null = New-Item -ItemType Directory -Path $publishDir -Force
$null = New-Item -ItemType Directory -Path $packagesRoot -Force

Write-Host "Publishing win-x64 (Native AOT)"
$connectorDirectory = Join-Path $scriptRoot "src/QodexBot/qq-connector"
& npm ci --omit=dev --prefix $connectorDirectory
if ($LASTEXITCODE -ne 0) {
    throw "npm ci failed for the QQ QR connector"
}

$publishArguments = @(
    "publish"
    $projectFullPath
    "-c", $Configuration
    "-r", "win-x64"
    "--self-contained", "true"
    "/p:PublishAot=true"
    "/p:IsAotCompatible=true"
    "/p:DebugType=None"
    "/p:DebugSymbols=false"
    "-o", $publishDir
)

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for win-x64 (Native AOT)"
}

Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter "*.pdb" | Remove-Item -Force -Confirm:$false

if ($isWindowsPlatform) {
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipFullPath -CompressionLevel Optimal
}
else {
    $zipCommand = Get-Command zip -ErrorAction SilentlyContinue
    if ($null -eq $zipCommand) {
        throw "zip command not found. Install zip or run on Windows to use Compress-Archive."
    }

    Push-Location -LiteralPath $publishDir
    try {
        & $zipCommand.Source -r $zipFullPath .
        if ($LASTEXITCODE -ne 0) {
            throw "zip failed for win-x64 (Native AOT)"
        }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "Version: $version"
Write-Host "Publish output: $publishDir"
Write-Host "Zip output: $zipFullPath"

if (-not $NoPause) {
    Read-Host "Press Enter to exit"
}
