<#
.SYNOPSIS
  Builds and packages DevSweep for Windows: installer, portable zip, CLI zip, SHA256SUMS.txt.

.DESCRIPTION
  Everything lands in <OutputDir> (default windows/artifacts/release/<version>):
    DevSweep-<version>-win-x64-setup.exe      per-user installer (Inno Setup 6)
    DevSweep-<version>-win-x64-portable.zip   the published app folder + README-FIRST.txt
    devsweep-cli-<version>-win-x64.zip        devsweep.exe (Native AOT) + a short README
    SHA256SUMS.txt                            sha256sum format ("<hash> *<file>")

  With no -Publish nothing leaves this machine: no tag, no push, no upload.
  With -Publish the script tags windows-v<version> on HEAD, pushes the tag and creates a GitHub
  release with `gh`, as a DRAFT unless you pass -Draft:$false. Publishing needs a clean checkout of a
  commit that is already pushed.

  The version comes from windows/version.props (the single source of truth); -Version overrides it
  for this run (it is passed to MSBuild as DevSweepVersion, so the exe, installer and zip names agree).

.PARAMETER RunTests   Also run the unit, accuracy and app tests (slow).
.PARAMETER SkipBuild  Reuse an earlier build in <OutputDir>\stage instead of publishing again.
.PARAMETER Publish    Tag and create the (draft) GitHub release. Without it nothing is uploaded.
.PARAMETER Draft      With -Publish, create the release as a draft (default $true).
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $RunTests,
    [switch] $SkipBuild,
    [string] $OutputDir,
    [switch] $Publish,
    [bool] $Draft = $true
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $windows -Parent

function Invoke-Native([string] $What, [scriptblock] $Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

function Step([string] $Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

# --- version: windows/version.props is the source of truth ---
$props = [xml] (Get-Content -Raw (Join-Path $windows 'version.props'))
$fileVersion = $props.SelectSingleNode('//DevSweepVersion').InnerText
if (-not $Version) { $Version = $fileVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "'$Version' is not a version like 1.2.3 or 1.2.3-rc.1" }
$numeric = ($Version -split '-')[0] + '.0'
$tag = "windows-v$Version"
if ($Version -ne $fileVersion) { Write-Warning "Building $Version, but windows/version.props says $fileVersion. Update version.props when this is the release you mean." }

if (-not $OutputDir) { $OutputDir = Join-Path $windows "artifacts/release/$Version" }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$stage = Join-Path $OutputDir 'stage'
$appStage = Join-Path $stage 'app'
$cliStage = Join-Path $stage 'cli'
$setupName = "DevSweep-$Version-win-x64-setup.exe"
$portableName = "DevSweep-$Version-win-x64-portable.zip"
$cliName = "devsweep-cli-$Version-win-x64.zip"
"DevSweep for Windows $Version  ->  $OutputDir"

# --- build ---
if (-not $SkipBuild) {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    Push-Location $windows
    try {
        if ($RunTests) {
            Step 'Tests'
            Invoke-Native 'build' { dotnet build DevSweep.slnx -c Release -warnaserror "-p:DevSweepVersion=$Version" }
            Invoke-Native 'core tests' { dotnet test tests/DevSweep.Core.Tests/DevSweep.Core.Tests.csproj -c Release --no-build }
            Invoke-Native 'accuracy tests' { dotnet test tests/DevSweep.Accuracy/DevSweep.Accuracy.csproj -c Release --no-build }
            Invoke-Native 'app tests' { dotnet test tests/DevSweep.App.Tests/DevSweep.App.Tests.csproj -c Release }
        }
        Step 'Publish DevSweep.exe (self-contained, Native AOT)'
        Invoke-Native 'app publish' { dotnet publish src/DevSweep.App/DevSweep.App.csproj -c Release -r win-x64 -o $appStage -warnaserror "-p:DevSweepVersion=$Version" }
        Step 'Publish devsweep.exe (Native AOT)'
        Invoke-Native 'cli publish' { dotnet publish src/DevSweep.Cli/DevSweep.Cli.csproj -c Release -r win-x64 -o $cliStage -warnaserror "-p:DevSweepVersion=$Version" }
    }
    finally { Pop-Location }
}
foreach ($required in "$appStage/DevSweep.exe", "$cliStage/devsweep.exe") {
    if (-not (Test-Path $required)) { throw "$required is missing; run without -SkipBuild" }
}
if (Test-Path "$appStage/DevSweep.dll") { throw 'expected a Native AOT DevSweep.exe, found a managed DevSweep.dll' }
Get-ChildItem $appStage -Recurse -Filter *.pdb | Remove-Item -Force
Get-ChildItem $cliStage -Recurse -Filter *.pdb | Remove-Item -Force

# The version must really be stamped into both exes.
foreach ($exe in "$appStage/DevSweep.exe", "$cliStage/devsweep.exe") {
    $info = (Get-Item $exe).VersionInfo
    "{0}: file version {1}, product version {2}" -f (Split-Path $exe -Leaf), $info.FileVersion, $info.ProductVersion
    if (-not $info.ProductVersion.StartsWith($Version)) { throw "$exe has product version '$($info.ProductVersion)', expected $Version" }
    if (-not $info.FileVersion.StartsWith(($Version -split '-')[0])) { throw "$exe has file version '$($info.FileVersion)', expected $Version" }
}

function New-Zip([string] $Folder, [string] $Zip) {
    Remove-Item $Zip -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($Folder, $Zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    "{0}: {1:N0} bytes" -f (Split-Path $Zip -Leaf), (Get-Item $Zip).Length
}

function Copy-Text([string] $From, [string] $To) {
    (Get-Content -Raw $From).Replace('{VERSION}', $Version) | Set-Content -Encoding utf8NoBOM -NoNewline $To
}

# --- portable zip ---
Step 'Portable zip'
$portableRoot = Join-Path $stage "DevSweep-$Version-portable"
Remove-Item -Recurse -Force $portableRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $portableRoot | Out-Null
Copy-Item -Recurse -Force "$appStage/*" $portableRoot
Copy-Text (Join-Path $windows 'installer/README-FIRST.txt') (Join-Path $portableRoot 'README-FIRST.txt')
New-Zip $portableRoot (Join-Path $OutputDir $portableName)

# --- CLI zip ---
Step 'CLI zip'
$cliRoot = Join-Path $stage "devsweep-cli-$Version-win-x64"
Remove-Item -Recurse -Force $cliRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $cliRoot | Out-Null
Copy-Item -Force "$cliStage/devsweep.exe" $cliRoot
Copy-Text (Join-Path $windows 'installer/CLI-README.txt') (Join-Path $cliRoot 'README.txt')
New-Zip $cliRoot (Join-Path $OutputDir $cliName)

# --- installer (Inno Setup 6) ---
Step 'Installer'
function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { $found = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    $found
}
$iscc = Find-Iscc
if (-not $iscc -and $env:CI -and (Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Host 'Inno Setup not found; installing it with Chocolatey (CI only).'
    Invoke-Native 'choco install innosetup' { choco install innosetup -y --no-progress }
    $iscc = Find-Iscc
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup).' }
"ISCC: $iscc"
Invoke-Native 'Inno Setup' {
    & $iscc /Qp "/DAppVersion=$Version" "/DNumericVersion=$numeric" "/DSourceDir=$appStage" "/DOutputDir=$OutputDir" (Join-Path $windows 'installer/DevSweep.iss')
}
$setup = Join-Path $OutputDir $setupName
if (-not (Test-Path $setup)) { throw "Inno Setup did not produce $setup" }
"{0}: {1:N0} bytes" -f $setupName, (Get-Item $setup).Length

# --- checksums ---
Step 'SHA256SUMS.txt'
$assets = @($setupName, $portableName, $cliName)
$sums = foreach ($name in $assets) { '{0} *{1}' -f (Get-FileHash -Algorithm SHA256 (Join-Path $OutputDir $name)).Hash.ToLowerInvariant(), $name }
$sumsPath = Join-Path $OutputDir 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsPath, (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
$sums

if ($env:GITHUB_OUTPUT) {
    "version=$Version" | Add-Content $env:GITHUB_OUTPUT
    "outdir=$OutputDir" | Add-Content $env:GITHUB_OUTPUT
    "setup=$setup" | Add-Content $env:GITHUB_OUTPUT
}

# --- publish (only with -Publish) ---
$notes = Join-Path $windows "docs/release-notes/$Version.md"
if (-not $Publish) {
    Write-Host "`nDone. Nothing was tagged, pushed or uploaded (no -Publish)." -ForegroundColor Green
    Write-Host "To create the draft release ${tag}:  ./scripts/release.ps1 -Version $Version -SkipBuild -Publish"
    return
}

Step "Publish $tag (draft: $Draft)"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) is required for -Publish' }
if (-not (Test-Path $notes)) { throw "release notes $notes are missing" }
Push-Location $repo
try {
    if (git status --porcelain) { throw 'the working tree is not clean; commit or stash first' }
    $head = (git rev-parse HEAD).Trim()
    git fetch origin --quiet
    if (-not (git branch -r --contains $head)) { throw "HEAD $head is not on any pushed branch; push it first" }
    git rev-parse -q --verify "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) { throw "tag $tag already exists" }
    $global:LASTEXITCODE = 0
    Invoke-Native 'gh auth' { gh auth status }
    Invoke-Native 'git tag' { git tag -a $tag -m "DevSweep for Windows $Version" $head }
    Invoke-Native 'git push tag' { git push origin "refs/tags/$tag" }
    $ghArgs = @('release', 'create', $tag, '--verify-tag', '--title', "DevSweep for Windows $Version", '--notes-file', $notes)
    if ($Draft) { $ghArgs += '--draft' }
    if ($Version.Contains('-')) { $ghArgs += '--prerelease' }
    $ghArgs += @((Join-Path $OutputDir $setupName), (Join-Path $OutputDir $portableName), (Join-Path $OutputDir $cliName), $sumsPath)
    Invoke-Native 'gh release create' { gh @ghArgs }
}
finally { Pop-Location }
Write-Host "Release $tag created ($(if ($Draft) { 'draft' } else { 'published' }))." -ForegroundColor Green
