<#
.SYNOPSIS
  Runs scripts/Verify-DevSweep.ps1 as a freshly created standard (non-admin) local user.

.DESCRIPTION
  Same idea as eng/run-as-standard-user.ps1 (run elevated on a throwaway machine, i.e. the CI
  runner): create a local user that is only in Users, grant it "log on as a batch job", run the
  accuracy kit as that user through a one-off scheduled task (RunLevel Limited, so no elevation
  exists), print its report, copy the report folder next to the other results, fail when the kit
  reports a FAIL, and remove the task and the user.

  DEVSWEEP_VERIFY_DISPOSABLE=1 is set for the task because the scheduled-task session does not inherit
  the runner's environment; it only allows the kit's -TestRecycleQuotaMb on this throwaway machine.
#>
param(
    [Parameter(Mandatory)] [string] $KitScript,
    [Parameter(Mandatory)] [string] $Cli,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [switch] $RunCleanTest,
    [int] $RecycleQuotaMb = 300,
    [string] $UserName = 'devsweep-verify',
    [int] $TimeoutMinutes = 25
)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:SystemDrive 'DevSweepVerifyStandardUser'
$taskName = 'DevSweepVerifyStandardUser'

function Grant-BatchLogon([string] $account) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DevSweepVerifyLsa {
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
    "Granted SeBatchLogonRight to ${account}: NTSTATUS 0x{0:X8}" -f [DevSweepVerifyLsa]::Grant($bytes, 'SeBatchLogonRight')
}

$kit = (Resolve-Path $KitScript).Path
$cliFile = (Resolve-Path $Cli).Path
$pwsh = (Get-Command pwsh).Source
$password = 'Ds1!' + [Guid]::NewGuid().ToString('N') + 'aZ'
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $UserName }
New-LocalUser -Name $UserName -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires -AccountNeverExpires -Description 'DevSweep CI verification-kit user' | Out-Null
Add-LocalGroupMember -Group 'Users' -Member $UserName
"Created local user $UserName (Users group only)"
Grant-BatchLogon $UserName

try {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$work\out" | Out-Null
    Copy-Item -Force $kit "$work\Verify-DevSweep.ps1"
    Copy-Item -Force $cliFile "$work\devsweep.exe"
    icacls $work /grant "${UserName}:(OI)(CI)M" /T /Q | Out-Null

    $extra = if ($RunCleanTest) { "-RunCleanTest -TestRecycleQuotaMb $RecycleQuotaMb" } else { '' }
    Set-Content -Encoding ascii "$work\run.cmd" @"
@echo off
set DEVSWEEP_VERIFY_DISPOSABLE=1
whoami /user /groups /priv > "$work\out\whoami.txt" 2>&1
echo TEMP=%TEMP% >> "$work\out\whoami.txt"
"$pwsh" -NoProfile -ExecutionPolicy Bypass -File "$work\Verify-DevSweep.ps1" -DevSweepPath "$work\devsweep.exe" -OutputPath "$work\out" -NoPause $extra > "$work\out\kit.log" 2>&1
echo %ERRORLEVEL% > "$work\out\exit.txt"
"@

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$work\run.cmd`""
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMinutes) -AllowStartIfOnBatteries
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User "$env:COMPUTERNAME\$UserName" -Password $password -RunLevel Limited | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path "$work\out\exit.txt") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }

    "===== whoami (the account the kit ran as) ====="
    Get-Content "$work\out\whoami.txt" -ErrorAction SilentlyContinue
    "===== kit output ====="
    Get-Content "$work\out\kit.log" -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null
    Copy-Item "$work\out\*" $ResultsDirectory -Recurse -ErrorAction SilentlyContinue

    if (-not (Test-Path "$work\out\exit.txt")) { throw "The standard-user kit run did not finish within $TimeoutMinutes minutes." }
    $exit = [int](Get-Content "$work\out\exit.txt").Trim()
    if ($exit -ne 0) { throw "The accuracy kit reported a failure as a standard user (exit $exit)." }
    "The accuracy kit passed as the standard user $UserName."
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $UserName -ErrorAction SilentlyContinue
}
