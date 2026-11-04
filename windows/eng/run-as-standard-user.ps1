<#
.SYNOPSIS
  Runs the accuracy suite as a freshly created standard (non-admin) local user.

.DESCRIPTION
  CI runs as an administrator, but DevSweep runs as a standard user (PLAN section 2,
  "Admin"). This script, run elevated on a throwaway machine (the CI runner):
    1. creates a local user that is only in the Users group, with a random password;
    2. copies the built accuracy suite somewhere that user can read;
    3. runs the suite's test executable as that user through a one-off scheduled task
       (Task Scheduler logs the user on with its password and loads its profile; this
       works from the non-interactive session CI runs in, where Start-Process -Credential
       does not);
    4. prints the user's token (whoami /groups /priv), the output and a summary of the
       results (passed, failed, skipped with reasons), and fails when any test failed;
    5. removes the task and the user.
#>
param(
    [Parameter(Mandatory)] [string] $TestDirectory,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [string] $UserName = 'devsweep-std',
    [int] $TimeoutMinutes = 25
)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:SystemDrive 'DevSweepStandardUser'
$taskName = 'DevSweepStandardUserTests'

function Grant-BatchLogon([string] $account) {
    # Task Scheduler needs "Log on as a batch job" for a password logon. It normally
    # grants it itself; grant it explicitly so the run never depends on that.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DevSweepLsa {
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
    $status = [DevSweepLsa]::Grant($bytes, 'SeBatchLogonRight')
    "Granted SeBatchLogonRight to ${account}: NTSTATUS 0x{0:X8}" -f $status
}

$exe = Get-ChildItem -Path $TestDirectory -Recurse -Filter 'DevSweep.Accuracy.exe' | Select-Object -First 1
if (-not $exe) { throw "DevSweep.Accuracy.exe not found under $TestDirectory" }

$password = 'Ds1!' + [Guid]::NewGuid().ToString('N') + 'aZ'
$secure = ConvertTo-SecureString $password -AsPlainText -Force
if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) { Remove-LocalUser -Name $UserName }
New-LocalUser -Name $UserName -Password $secure -PasswordNeverExpires -AccountNeverExpires -Description 'DevSweep CI standard user' | Out-Null
Add-LocalGroupMember -Group 'Users' -Member $UserName
"Created local user $UserName (Users group only):"
net user $UserName | Select-String -Pattern 'Group Memberships'
Grant-BatchLogon $UserName

try {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$work\bin", "$work\out" | Out-Null
    Copy-Item -Recurse -Force (Join-Path $exe.DirectoryName '*') "$work\bin"
    icacls $work /grant "${UserName}:(OI)(CI)M" /T /Q | Out-Null

    $dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { Split-Path (Get-Command dotnet).Source }
    Set-Content -Encoding ascii "$work\run.cmd" @"
@echo off
set DOTNET_ROOT=$dotnetRoot
whoami /user /groups /priv > "$work\out\whoami.txt" 2>&1
echo TEMP=%TEMP% >> "$work\out\whoami.txt"
"$work\bin\$($exe.Name)" -xml "$work\out\standard-user.xml" > "$work\out\standard-user.log" 2>&1
echo %ERRORLEVEL% > "$work\out\exit.txt"
"@

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$work\run.cmd`""
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMinutes) -AllowStartIfOnBatteries
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User "$env:COMPUTERNAME\$UserName" -Password $password -RunLevel Limited | Out-Null
    Start-ScheduledTask -TaskName $taskName

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while (-not (Test-Path "$work\out\exit.txt") -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
    }

    $info = Get-ScheduledTaskInfo -TaskName $taskName
    "Task last result: 0x{0:X8}" -f $info.LastTaskResult

    "===== whoami (the account the suite ran as) ====="
    Get-Content "$work\out\whoami.txt" -ErrorAction SilentlyContinue
    "===== test output ====="
    Get-Content "$work\out\standard-user.log" -ErrorAction SilentlyContinue

    New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null
    Copy-Item "$work\out\*" $ResultsDirectory -ErrorAction SilentlyContinue

    if (-not (Test-Path "$work\out\exit.txt")) { throw "The standard-user run did not finish within $TimeoutMinutes minutes." }
    $exit = [int](Get-Content "$work\out\exit.txt").Trim()

    $xmlPath = "$work\out\standard-user.xml"
    if (Test-Path $xmlPath) {
        [xml] $xml = Get-Content $xmlPath
        $assembly = $xml.assemblies.assembly
        "===== summary as a standard user: total {0}, passed {1}, failed {2}, skipped {3} =====" -f $assembly.total, $assembly.passed, $assembly.failed, $assembly.skipped
        foreach ($test in $xml.SelectNodes('//test')) {
            if ($test.result -eq 'Skip') { "SKIPPED  {0}`n         {1}" -f $test.name, $test.reason.InnerText }
            if ($test.result -eq 'Fail') { "FAILED   {0}`n         {1}" -f $test.name, $test.failure.message.InnerText }
        }
    }

    if ($exit -ne 0) { throw "The accuracy suite failed as a standard user (exit $exit)." }
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $UserName -ErrorAction SilentlyContinue
}
