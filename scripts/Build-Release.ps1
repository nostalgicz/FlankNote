[CmdletBinding()]
param(
    [string]$CertificatePath = $env:FLANKNOTE_SIGN_CERTIFICATE,
    [string]$CertificatePassword = $env:FLANKNOTE_SIGN_PASSWORD,
    [string]$CertificateThumbprint = $env:FLANKNOTE_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowUnsigned
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "FlankNote.csproj"
$publishProfile = "win-x64"
$publishedExe = Join-Path $projectRoot "bin\Publish\win-x64\FlankNote.exe"
$installerScript = Join-Path $projectRoot "installer\FlankNote.iss"
$installerExe = Join-Path $projectRoot "artifacts\installer\FlankNote-Setup-x64.exe"

[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw -Encoding utf8
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw "The project Version property is missing." }

function Find-Executable([string]$name, [string[]]$fallbacks) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $fallbacks) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

$innoCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$iscc = Find-Executable "ISCC.exe" $innoCandidates
if (-not $iscc) { throw "Inno Setup 6 compiler (ISCC.exe) was not found." }

$signingEnabled = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
                  -not [string]::IsNullOrWhiteSpace($CertificatePath)
if (-not $signingEnabled -and -not $AllowUnsigned) {
    throw "A code-signing certificate is required. Set FLANKNOTE_SIGN_THUMBPRINT or FLANKNOTE_SIGN_CERTIFICATE, or pass -AllowUnsigned for a local test package."
}

$signTool = $null
if ($signingEnabled) {
    $signTool = Find-Executable "signtool.exe" @()
    if (-not $signTool) {
        $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
        if (Test-Path -LiteralPath $kitsRoot) {
            $signTool = Get-ChildItem -LiteralPath $kitsRoot -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
                Sort-Object FullName -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $signTool) { throw "Windows SDK SignTool was not found." }
    if ($CertificatePath -and -not (Test-Path -LiteralPath $CertificatePath)) {
        throw "The certificate file does not exist: $CertificatePath"
    }
}

function Sign-Artifact([string]$path) {
    if (-not $signingEnabled) { return }
    $arguments = @("sign", "/fd", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $arguments += @("/sha1", $CertificateThumbprint)
    }
    else {
        $arguments += @("/f", $CertificatePath)
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $arguments += @("/p", $CertificatePassword)
        }
    }
    $arguments += @("/tr", $TimestampUrl, "/td", "SHA256", $path)
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
    & $signTool verify /pa /v $path
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $path" }
}

& dotnet publish $projectFile -c Release -r win-x64 --self-contained true -p:PublishProfile=$publishProfile
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $publishedExe)) {
    throw "Publishing FlankNote failed."
}

Sign-Artifact $publishedExe

$innoArguments = @("/DMyAppVersion=$version")
if ($signingEnabled) {
    $quotedSignTool = '$q' + $signTool + '$q'
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $certificateArguments = "/sha1 $CertificateThumbprint"
    }
    else {
        $certificateArguments = '/f $q' + $CertificatePath + '$q'
        if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
            $certificateArguments += ' /p $q' + $CertificatePassword + '$q'
        }
    }
    $innoSignCommand = "$quotedSignTool sign /fd SHA256 $certificateArguments /tr $TimestampUrl /td SHA256 `$f"
    $innoArguments += @("/DEnableSigning=1", "/Sflanknote=$innoSignCommand")
}

& $iscc @innoArguments $installerScript
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $installerExe)) {
    throw "Building the installer failed."
}

if ($signingEnabled) {
    & $signTool verify /pa /v $installerExe
    if ($LASTEXITCODE -ne 0) { throw "Installer signature verification failed." }
}
Write-Host "Release installer: $installerExe"
