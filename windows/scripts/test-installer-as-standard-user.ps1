<#
.SYNOPSIS
  Runs scripts/test-installer.ps1 as a freshly created standard (non-admin) local user.

.DESCRIPTION
  Same idea as eng/run-as-standard-user.ps1 (run elevated on a throwaway machine, i.e. the CI
  runner): create a local user that is only in Users, grant it "log on as a batch job", run the
  install/smoke/upgrade/uninstall cycle as that user through a one-off scheduled task (RunLevel
  Limited, so no elevation exists to prompt for), print its log, fail on any failed assertion, and
  remove the task and the user.
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [string] $UserName = 'devsweep-inst',
    [int] $TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:SystemDrive 'DevSweepInstallerTest'
$taskName = 'DevSweepInstallerTest'

function Grant-BatchLogon([string] $account) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DevSweepInstLsa {
    [StructLayout(LayoutKind.Sequential)] struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    public static uint Grant(byte[] sid, string right) {
        var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES)) };
        IntPtr policy;
        uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000800 | 0x00000010, out policy);
        if (status != 0) return status;
        var name = new LSA_UNICODE_STRING { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) };
        try { return LsaAddAccountRights(policy, sid, new[] { name }, 1); }
        finally { Marshal.FreeHGlobal(name.Buffer); LsaClose(policy); }
    }
}
'@
    $sid = (New-Object System.Security.Principal.NTAccount($account)).Translate([System.Security.Principal.SecurityIdentifier])
    $bytes = New-Object byte[] $sid.BinaryLength
    $sid.GetBinaryForm($bytes, 0)
    "Granted SeBatchLogonRight to ${account}: NTSTATUS 0x{0:X8}" -f [DevSweepInstLsa]::Grant($bytes, 'SeBatchLogonRight')
}

$setupFile = (Resolve-Path $Setup).Path
$pwsh = (Get-Command pwsh).Source
$password = 'Ds1!' + [Guid]::NewGuid().ToString('N') + 'aZ'
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $UserName }
New-LocalUser -Name $UserName -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires -AccountNeverExpires -Description 'DevSweep CI installer test user' | Out-Null
Add-LocalGroupMember -Group 'Users' -Member $UserName
"Created local user $UserName (Users group only)"
Grant-BatchLogon $UserName

try {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$work\out" | Out-Null
    Copy-Item -Force $setupFile "$work\setup.exe"
    Copy-Item -Force (Join-Path $PSScriptRoot 'test-installer.ps1') "$work\test-installer.ps1"
    icacls $work /grant "${UserName}:(OI)(CI)M" /T /Q | Out-Null

    Set-Content -Encoding ascii "$work\run.cmd" @"
@echo off
whoami /user /groups /priv > "$work\out\whoami.txt" 2>&1
"$pwsh" -NoProfile -ExecutionPolicy Bypass -File "$work\test-installer.ps1" -Setup "$work\setup.exe" -Version $Version -LogDirectory "$work\out" -ExpectStandardUser > "$work\out\installer-test.log" 2>&1
echo %ERRORLEVEL% > "$work\out\exit.txt"
"@

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$work\run.cmd`""
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMinutes) -AllowStartIfOnBatteries
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User "$env:COMPUTERNAME\$UserName" -Password $password -RunLevel Limited | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path "$work\out\exit.txt") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }

    "===== whoami (the account the installer ran as) ====="
    Get-Content "$work\out\whoami.txt" -ErrorAction SilentlyContinue
    "===== installer test output ====="
    Get-Content "$work\out\installer-test.log" -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null
    Copy-Item "$work\out\*" $ResultsDirectory -ErrorAction SilentlyContinue

    if (-not (Test-Path "$work\out\exit.txt")) { throw "The standard-user installer test did not finish within $TimeoutMinutes minutes." }
    $exit = [int](Get-Content "$work\out\exit.txt").Trim()
    if ($exit -ne 0) { throw "The installer test failed as a standard user (exit $exit)." }
    "The installer test passed as the standard user $UserName."
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $UserName -ErrorAction SilentlyContinue
}
