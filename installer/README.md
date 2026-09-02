# FlankNote installer

The release pipeline publishes a self-contained Windows x64 executable and packages it with Inno Setup 6. The installer uses a per-user location, creates Start menu application and uninstall shortcuts, and registers an uninstall entry in Windows Settings.

## Prerequisites

- .NET 10 SDK
- Inno Setup 6 (`ISCC.exe`)
- Windows SDK SignTool
- A trusted Authenticode code-signing certificate

Configure either a certificate from the current-user certificate store:

```powershell
$env:FLANKNOTE_SIGN_THUMBPRINT = "CERTIFICATE_THUMBPRINT"
.\scripts\Build-Release.ps1
```

Or a PFX certificate:

```powershell
$env:FLANKNOTE_SIGN_CERTIFICATE = "C:\secure\FlankNote-signing.pfx"
$env:FLANKNOTE_SIGN_PASSWORD = "certificate password"
.\scripts\Build-Release.ps1
```

For local installer testing only, an explicitly unsigned package can be generated with:

```powershell
.\scripts\Build-Release.ps1 -AllowUnsigned
```

Unsigned builds are not suitable for public release. A self-signed certificate does not establish public trust or remove Microsoft Defender SmartScreen reputation warnings.
