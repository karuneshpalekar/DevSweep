<#
.SYNOPSIS
  Installs, runs, upgrades and uninstalls DevSweep silently as the CURRENT user and checks each step.

.DESCRIPTION
  Proves, for whoever runs it (CI runs it as the administrator runner and again, through
  test-installer-as-standard-user.ps1, as a fresh standard user):
    - the install needs no elevation: everything lands under the user's profile, the uninstall entry
      is in HKCU and nothing is written to HKLM;
    - DevSweep.exe --smoke exits 0 from the installed folder;
    - installing again over the install (with a stale file from an "older version" present and a
      running DevSweep) upgrades in place: stale files gone, DevSweep stopped, data kept;
    - a silent uninstall removes the program files, shortcuts and uninstall entry but keeps
      %LOCALAPPDATA%\DevSweep, and /REMOVEUSERDATA removes it without following a junction out of it.
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $Version,
    [string] $LogDirectory = (Join-Path $env:TEMP 'devsweep-installer-test'),
    [switch] $ExpectStandardUser,
    # Non-interactive session (a scheduled task has no desktop): the WinUI window cannot be created,
    # so --smoke is only expected to get through the engine and the rules (its first two log lines).
    [switch] $NoWindow
)

$ErrorActionPreference = 'Stop'
$appId = '{3F6C7A52-8B1E-4D5A-9C3E-6D1F0A7B2E94}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$machineKeys = @("HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId", "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId")
$app = Join-Path $env:LOCALAPPDATA 'Programs\DevSweep'
$data = Join-Path $env:LOCALAPPDATA 'DevSweep'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DevSweep.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DevSweep.lnk'
$outside = Join-Path $env:TEMP 'devsweep-installer-outside'
$step = 0
New-Item -ItemType Directory -Force $LogDirectory | Out-Null

function Say([string] $Text) { $script:step++; Write-Host ("`n== {0}. {1}" -f $script:step, $Text) }
function Assert([bool] $Condition, [string] $Message) { if (-not $Condition) { throw "ASSERT FAILED: $Message" } else { Write-Host "  ok: $Message" } }
function Wait-Until([scriptblock] $Condition, [int] $Seconds = 60) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while (-not (& $Condition) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    [bool] (& $Condition)
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdminGroup = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say "Who is installing: $($identity.Name); member of Administrators (enabled): $isAdminGroup; profile: $env:USERPROFILE"
if ($ExpectStandardUser) { Assert (-not $isAdminGroup) 'the account is a standard user (not in Administrators)' }

function Install([string[]] $Extra = @(), [string] $Name = 'install') {
    $log = Join-Path $LogDirectory "$Name.log"
    $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=$log") + $Extra
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $Setup -ArgumentList $setupArgs -Wait -PassThru
    "  setup exit code $($p.ExitCode) after {0:0.0} s" -f $clock.Elapsed.TotalSeconds
    if (Test-Path $log) { Get-Content $log | Select-String -Pattern 'Install mode|elevat|administrative|Running in|Setup version|Windows version|64-bit|Log opened' | Select-Object -First 12 | ForEach-Object { "  log: $($_.Line)" } }
    if ($p.ExitCode -ne 0) { if (Test-Path $log) { Get-Content $log -Tail 40 }; throw "setup failed ($($p.ExitCode))" }
}

function Uninstall([string[]] $Extra = @()) {
    $cmd = (Get-ItemProperty $uninstallKey).UninstallString.Trim('"')
    Write-Host "  uninstaller: $cmd"
    $p = Start-Process -FilePath $cmd -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $Extra) -Wait -PassThru
    Write-Host "  uninstaller exit code $($p.ExitCode)"
    # The uninstaller re-launches itself from %TEMP% and deletes its own files last: wait for that.
    $gone = Wait-Until { -not (Test-Path $uninstallKey) -and -not (Test-Path $app) } 90
    if (-not $gone) { Get-ChildItem $app -Recurse -ErrorAction SilentlyContinue | Select-Object -First 20 FullName | Format-Table | Out-String | Write-Host }
    return $gone
}

function Assert-Installed([string] $Why) {
    Assert (Test-Path "$app\DevSweep.exe") "DevSweep.exe is in $app ($Why)"
    Assert (Test-Path "$app\unins000.exe") 'the uninstaller is there'
    Assert ($app.StartsWith($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase)) 'the install location is under the user profile'
    Assert (Test-Path $uninstallKey) 'the Apps & features entry is in HKCU'
    $entry = Get-ItemProperty $uninstallKey
    "  entry: $($entry.DisplayName) $($entry.DisplayVersion), $($entry.InstallLocation), publisher $($entry.Publisher)"
    Assert ($entry.DisplayVersion -eq $Version) "the entry says version $Version"
    Assert ($entry.InstallLocation.TrimEnd('\') -ieq $app) 'the entry points at the install folder'
    foreach ($key in $machineKeys) { Assert (-not (Test-Path $key)) "nothing was written to $($key.Substring(0, 40))..." }
    Assert (Test-Path $startMenu) 'the Start Menu shortcut exists'
    $shell = New-Object -ComObject WScript.Shell
    Assert ($shell.CreateShortcut($startMenu).TargetPath -ieq "$app\DevSweep.exe") 'the Start Menu shortcut points at the installed DevSweep.exe'
    $info = (Get-Item "$app\DevSweep.exe").VersionInfo
    "  DevSweep.exe file version $($info.FileVersion), product version $($info.ProductVersion)"
    Assert ($info.ProductVersion.StartsWith($Version)) "DevSweep.exe is stamped with $Version"
    Assert (-not (Get-ChildItem $app -Recurse -Filter *.pdb)) 'no .pdb files were installed'
}

function Run-Smoke {
    $log = Join-Path $env:TEMP 'devsweep-app-smoke.txt'
    Remove-Item $log -ErrorAction SilentlyContinue
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath "$app\DevSweep.exe" -ArgumentList '--smoke' -PassThru -WorkingDirectory $env:USERPROFILE
    if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw 'DevSweep --smoke did not exit within 120 s' }
    $p.WaitForExit()
    "  --smoke exit code $($p.ExitCode) after {0:0.0} s" -f $clock.Elapsed.TotalSeconds
    if (Test-Path $log) { Get-Content $log | ForEach-Object { "  smoke: $_" } }
    if ($NoWindow) {
        Assert ((Test-Path $log) -and (Select-String -Path $log -Pattern 'rules: \d+ loaded from \d+ packs, 0 errors' -Quiet)) 'the installed DevSweep.exe started, built the engine and loaded every rule pack with 0 errors (no desktop here for the window)'
        return
    }
    Assert ($p.ExitCode -eq 0) 'DevSweep.exe --smoke exits 0 from the installed folder'
    Assert ((Test-Path $log) -and (Select-String -Path $log -Pattern 'smoke: ok' -Quiet)) "the smoke log says 'smoke: ok'"
}

# ---------------------------------------------------------------- clean slate
Say 'Clean slate'
Remove-Item -Recurse -Force $app, $data, $outside, $startMenu, $desktop -ErrorAction SilentlyContinue
Assert (-not (Test-Path $uninstallKey)) 'no earlier DevSweep install is registered'

# ---------------------------------------------------------------- install
Say 'Silent install (default tasks: Start Menu only)'
Install -Name 'install-1'
Assert-Installed 'first install'
Assert (-not (Test-Path $desktop)) 'no desktop shortcut unless asked for'

Say 'Run --smoke from the installed location'
Run-Smoke

# ---------------------------------------------------------------- user data
Say 'Create user data (History, settings, quarantine, and a junction out of it)'
New-Item -ItemType Directory -Force "$data\Quarantine\item1", $outside | Out-Null
Set-Content "$data\history.json" '{"sentinel":"history"}'
Set-Content "$data\app-settings.json" '{"sentinel":"settings"}'
Set-Content "$data\Quarantine\item1\restorable.txt" 'a quarantined item you may want back'
Set-Content "$outside\must-survive.txt" 'outside the data folder'
# History/quarantine files the app may legitimately tidy when it runs; this one is only ours.
New-Item -ItemType Directory -Force "$data\Quarantine\keep" | Out-Null
Set-Content "$data\installer-test-sentinel.txt" 'user data'
Set-Content "$data\Quarantine\keep\restorable.bin" 'a quarantined item you may want back'
New-Item -ItemType Junction -Path "$data\Quarantine\link-out" -Target $outside | Out-Null
Assert (Test-Path "$data\Quarantine\link-out\must-survive.txt") 'the junction resolves'

# ---------------------------------------------------------------- upgrade
Say 'Upgrade in place over the install (running DevSweep, a stale file, desktop shortcut now ticked)'
Set-Content "$app\stale-from-an-older-version.dll" 'old'
New-Item -ItemType Directory -Force "$app\old-folder" | Out-Null
Set-Content "$app\old-folder\old.txt" 'old'
# --background is what the login entry passes: a long-lived tray instance with no window (M5). It must still
# be running after 8 s (a GUI that exits quickly proves nothing), and Setup must stop it by name.
$running = Start-Process -FilePath "$app\DevSweep.exe" -ArgumentList '--background' -PassThru -WorkingDirectory $env:USERPROFILE
Start-Sleep -Seconds 8
$wasRunning = -not $running.HasExited
"  DevSweep (tray, --background) started for the upgrade: still running after 8 s = $wasRunning"
Assert $wasRunning 'the tray instance (DevSweep.exe --background) stays running until Setup stops it'
Install -Extra @('/TASKS=desktopicon') -Name 'install-2-upgrade'
if ($running.HasExited) { "  the GUI instance had exited with code $($running.ExitCode)" }
Write-Host '  data folder now:'; Get-ChildItem $data -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { "    $($_.FullName.Substring($data.Length))" }
Assert (-not (Get-Process -Name DevSweep -ErrorAction SilentlyContinue)) 'no DevSweep process is left running'
if ($wasRunning) { Assert $running.HasExited 'the running DevSweep was stopped by Setup' }
Assert-Installed 'after the upgrade'
Assert (-not (Test-Path "$app\stale-from-an-older-version.dll")) 'the stale file from the old version was removed'
Assert (-not (Test-Path "$app\old-folder")) 'the stale folder from the old version was removed'
Assert (Test-Path $desktop) 'the desktop shortcut was created by the optional task'
Assert ((Test-Path "$data\installer-test-sentinel.txt") -and (Test-Path "$data\Quarantine\keep\restorable.bin")) 'the upgrade kept the data'
Run-Smoke

# ---------------------------------------------------------------- uninstall, data kept
Say 'Silent uninstall (default: keep user data)'
Assert (Uninstall) 'the uninstall finished'
Assert (-not (Test-Path "$app\DevSweep.exe")) 'DevSweep.exe is gone'
Assert (-not (Test-Path $app)) 'the program folder is gone'
Assert (-not (Test-Path $startMenu)) 'the Start Menu shortcut is gone'
Assert (-not (Test-Path $desktop)) 'the desktop shortcut is gone'
Assert (-not (Test-Path $uninstallKey)) 'the Apps & features entry is gone'
Assert (Test-Path "$data\installer-test-sentinel.txt") 'the data folder was kept'
Assert (Test-Path "$data\Quarantine\keep\restorable.bin") 'the quarantine was kept'

# ---------------------------------------------------------------- opt-in removal
Say 'Install again, then uninstall with /REMOVEUSERDATA'
Install -Name 'install-3'
Assert-Installed 'reinstall after uninstall'
Assert (Test-Path "$data\installer-test-sentinel.txt") 'the data from before is still there for the new install'
Assert (Uninstall -Extra @('/REMOVEUSERDATA')) 'the uninstall finished'
Assert (-not (Test-Path $app)) 'the program folder is gone'
Assert (-not (Test-Path $uninstallKey)) 'the Apps & features entry is gone'
Assert (-not (Test-Path $data)) '%LOCALAPPDATA%\DevSweep was removed because the user opted in'
Assert (Test-Path "$outside\must-survive.txt") 'a junction inside the data folder was not followed: its target survived'

Remove-Item -Recurse -Force $outside -ErrorAction SilentlyContinue
Write-Host "`nInstaller test passed for $($identity.Name)." -ForegroundColor Green
