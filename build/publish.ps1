<#
.SYNOPSIS
    Builds a BimGo release: tests, the self-contained app, BimGo for Revit (2025 / 2026 / 2027) as an Autodesk bundle,
    and the installer artifacts\BimGo-Setup-<version>.exe. Optionally a portable zip of the app (no setup needed).

.DESCRIPTION
    Run from anywhere (paths are relative to the repo root):
        powershell -ExecutionPolicy Bypass -File build\publish.ps1
    Steps (stops at the first failure):
      1. Reads the version from src\Directory.Build.props (<BimGoVersion>).
      2. Runs the Core tests.
      3. Publishes BimGo.App self-contained for win-x64 (folder, not single-file, no trimming) to artifacts\app\.
      4. Builds BimGo.Revit "Release R25 / R26 / R27" and lays out artifacts\bundle\BimGo.bundle\ (PackageContents.xml
         with the version + Contents\<year>\BimGo.addin + Contents\<year>\BimGo\). Checks no package dll slipped in.
      5. Optionally signs (-Sign; off for 1.0).
      6. Compiles installer\BimGo.iss with Inno Setup 6 into artifacts\.
      7. With -Portable (or -PortableOnly): zips the app into artifacts\BimGo-Portable-<version>.zip: one folder with
         BimGo.exe, "Open .bimgo files with BimGo.cmd" (runs BimGo.exe --register: the file association and a
         Start-menu shortcut for the current user, no admin), "Remove BimGo from this PC.cmd" (--unregister) and
         "READ ME FIRST.txt"; -Include adds files (e.g. a viewer copy) beside them.
    Portable only, for sending BimGo to someone who only opens .bimgo files (no Revit, no Inno Setup needed):
        powershell -ExecutionPolicy Bypass -File build\publish.ps1 -PortableOnly -Include "C:\Models\Tower (viewer).bimgo"
    Needs: the .NET 8 SDK, the .NET 10 SDK (Revit 2027), Revit 2025 / 2026 / 2027 installed (the API HintPaths), and
    Inno Setup 6 (https://jrsoftware.org/isinfo.php). Leave a year out with -Years 2025,2026.

.PARAMETER Years
    Revit years to build (default 2025, 2026, 2027). Years left out are dropped from PackageContents.xml.

.PARAMETER SkipTests
    Skip the Core tests (not for real releases).

.PARAMETER Sign
    Sign BimGo.exe, the add-in dlls and the setup with signtool (needs -CertThumbprint). Off for 1.0.

.PARAMETER Portable
    Also make the portable zip artifacts\BimGo-Portable-<version>.zip (after the installer).

.PARAMETER PortableOnly
    Only the tests, the app and the portable zip: no BimGo for Revit, no installer (needs only the .NET 8 SDK).

.PARAMETER Include
    Files to put in the portable zip beside the app (a viewer copy for the person it's for, say).
#>
param(
    [int[]] $Years = @(2025, 2026, 2027),
    [switch] $SkipTests,
    [switch] $Sign,
    [string] $CertThumbprint = '',
    [string] $InnoCompiler = '',
    [switch] $Portable,
    [switch] $PortableOnly,
    [string[]] $Include = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
if ($PortableOnly) { $Portable = $true }
foreach ($file in $Include) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "-Include: $file isn't a file." }
}

function Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check-Exit([string] $what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }
function Sign-Files([string[]] $files) {
    if (-not $Sign) { return }
    if (-not $CertThumbprint) { throw '-Sign needs -CertThumbprint.' }
    & signtool sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $files
    Check-Exit 'signtool'
}

# ---- 1. Version
[xml] $props = Get-Content (Join-Path $root 'src\Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.BimGoVersion } | Select-Object -First 1).BimGoVersion
if (-not $version) { throw 'No <BimGoVersion> in src\Directory.Build.props.' }
Step "BimGo $version"

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts | Out-Null

# ---- 2. Tests
if (-not $SkipTests) {
    Step 'Core tests'
    dotnet test (Join-Path $root 'tests\BimGo.Core.Tests') -c Release --nologo
    Check-Exit 'The Core tests'
}

# ---- 3. The app (self-contained, so standalone users need no .NET)
Step 'BimGo.App (self-contained win-x64)'
$appOut = Join-Path $artifacts 'app'
dotnet publish (Join-Path $root 'src\BimGo.App\BimGo.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishTrimmed=false -p:PublishSingleFile=false -o $appOut --nologo
Check-Exit 'Publishing BimGo.App'
Get-ChildItem $appOut -Filter *.pdb -Recurse | Remove-Item -Force
foreach ($needed in 'BimGo.exe', 'BimGo.ico', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt', 'Resources\places.tsv', 'Resources\Fonts\Inter-Regular.ttf') {
    if (-not (Test-Path (Join-Path $appOut $needed))) { throw "The app publish lacks $needed." }
}

# ---- 7. The portable zip (a function: made after the installer, or straight away with -PortableOnly)
function Make-Portable {
    Step 'Portable zip'
    $name = "BimGo-$version"
    $stage = Join-Path $artifacts 'portable'
    $folder = Join-Path $stage $name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item (Join-Path $appOut '*') $folder -Recurse

    # Double-click .bimgo files: the same registration the installer runs, for this folder's BimGo.exe
    $register = @(
        '@echo off',
        'rem Makes .bimgo files open with the BimGo.exe in this folder (for your Windows user; no admin rights needed)',
        'rem and adds BimGo to the Start menu. Run it again if you move this folder.',
        '"%~dp0BimGo.exe" --register'
    )
    Set-Content -Path (Join-Path $folder 'Open .bimgo files with BimGo.cmd') -Value $register -Encoding Ascii
    $unregister = @(
        '@echo off',
        'rem Stops .bimgo files opening with BimGo and removes it from the Start menu. Then delete this folder.',
        '"%~dp0BimGo.exe" --unregister'
    )
    Set-Content -Path (Join-Path $folder 'Remove BimGo from this PC.cmd') -Value $unregister -Encoding Ascii

    $readme = @(
        "BimGo $version (portable)",
        '',
        '1. Extract this whole folder first (right-click the zip > Extract All...), for example into Documents.',
        '   BimGo does not run from inside the zip, and BimGo.exe needs every file beside it.',
        '2. Double-click "Open .bimgo files with BimGo.cmd" once: .bimgo files then open in BimGo with a double-click,',
        '   and BimGo is in the Start menu. (Or start BimGo.exe and drag a .bimgo onto its window.)',
        '3. Windows may say "Windows protected your PC" (BimGo is not code-signed): choose More info > Run anyway.',
        '',
        'Nothing else is needed: no installer, no .NET, no Revit. Settings are kept in %AppData%\BimGo.',
        'To remove BimGo: double-click "Remove BimGo from this PC.cmd", then delete this folder.',
        '',
        'User guide: https://github.com/aussieBIMguru/BimGo/tree/main/docs'
    )
    Set-Content -Path (Join-Path $folder 'READ ME FIRST.txt') -Value $readme -Encoding UTF8

    # Files sent along (a viewer copy): beside the app, at the top of the folder
    foreach ($file in $Include) { Copy-Item -LiteralPath $file -Destination $folder }

    $zip = Join-Path $artifacts "BimGo-Portable-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Remove-Item $stage -Recurse -Force
    $size = '{0:N0} MB' -f ((Get-Item $zip).Length / 1MB)
    Step "Done: $zip ($size)"
}

if ($PortableOnly) {
    Sign-Files @(Join-Path $appOut 'BimGo.exe')
    Make-Portable
    return
}

# ---- 4. BimGo for Revit as a bundle
$bundle = Join-Path $artifacts 'bundle\BimGo.bundle'
New-Item -ItemType Directory -Path (Join-Path $bundle 'Contents') -Force | Out-Null
foreach ($year in $Years) {
    $short = "$year".Substring(2)
    Step "BimGo for Revit $year"
    dotnet build (Join-Path $root 'src\BimGo.sln') -c "Release R$short" --nologo
    Check-Exit "Building Release R$short"

    $built = Join-Path $root "src\BimGo.Revit\bin\Release\$year"
    if (-not (Test-Path (Join-Path $built 'BimGo.addin'))) { throw "No BimGo.addin in $built." }
    $target = Join-Path $bundle "Contents\$year"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item (Join-Path $built 'BimGo.addin') $target
    Copy-Item (Join-Path $built 'BimGo') $target -Recurse
    Get-ChildItem $target -Filter *.pdb -Recurse | Remove-Item -Force

    # The add-in loads inside Revit: no NuGet package may come with it
    $packages = Get-ChildItem $target -Recurse -Filter *.dll | Where-Object { $_.Name -notmatch '^BimGo\.(Revit|Core)\.dll$' }
    if ($packages) { throw "Unexpected dlls in the Revit $year add-in: $($packages.Name -join ', ')" }
}
# PackageContents.xml: the version filled in, and only the years built (a listed year without files would make that
# Revit report a missing add-in)
[xml] $package = (Get-Content (Join-Path $root 'installer\PackageContents.xml') -Raw).Replace('@VERSION@', $version)
foreach ($component in @($package.ApplicationPackage.Components)) {
    $entryYear = [int]($component.ComponentEntry.ModuleName -replace '^\./Contents/(\d{4})/.*$', '$1')
    if ($Years -notcontains $entryYear) { [void] $package.ApplicationPackage.RemoveChild($component) }
}
$package.Save((Join-Path $bundle 'PackageContents.xml'))

# ---- 5. Signing (off for 1.0)
Sign-Files (@(Join-Path $appOut 'BimGo.exe') + (Get-ChildItem $bundle -Recurse -Filter 'BimGo.*.dll').FullName)

# ---- 6. The installer
Step 'Installer'
if (-not $InnoCompiler) {
    $InnoCompiler = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                      "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $InnoCompiler) { throw 'Inno Setup 6 (ISCC.exe) was not found: install it or pass -InnoCompiler.' }
& $InnoCompiler "/DAppVersion=$version" "/O$artifacts" (Join-Path $root 'installer\BimGo.iss')
Check-Exit 'Inno Setup'

$setup = Join-Path $artifacts "BimGo-Setup-$version.exe"
Sign-Files @($setup)
Step "Done: $setup"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
Write-Host "SHA-256 $hash"
"$hash  BimGo-Setup-$version.exe" | Set-Content (Join-Path $artifacts "BimGo-Setup-$version.sha256.txt")

if ($Portable) { Make-Portable }
