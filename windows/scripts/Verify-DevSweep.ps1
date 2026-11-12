<#
.SYNOPSIS
  Checks DevSweep's numbers and claims against Windows itself, on THIS machine, and writes a report.

.DESCRIPTION
  DevSweep for Windows promises four things (windows/PLAN.md, section 1): sizes are true, detection
  is true, removal is true, and it says when it does not know. This script tests those promises on the
  machine it runs on. It runs DevSweep's command-line program (devsweep.exe), then re-measures and
  re-checks what DevSweep said in a different way (its own small file walker, the registry, where.exe,
  Get-NetTCPConnection, the Recycle Bin's own files), and compares the two.

  What it does and does not do:
    * By default it is READ-ONLY. It runs `devsweep scan --json --exact`, `inventory --json`,
      `runtimes --json --offline` and `ports --json --all`, and reads the registry, Start Menu,
      Scoop and Chocolatey folders, the PATH, and file sizes. It changes nothing, needs no
      administrator rights, needs no network, and uploads nothing. Nothing leaves this PC.
    * With -RunCleanTest (opt in) it also creates a small throwaway fixture inside a folder it
      creates itself under %TEMP%, lets DevSweep's cleaner move it to the Recycle Bin or DevSweep's
      quarantine, checks where it went, restores it, and deletes a second fixture for good to check
      the freed space. It refuses to touch anything it did not create (a marker file proves it).
    * The report (Verify-DevSweep-<time>\report.md and report.json, plus raw\ outputs) is written to
      your Desktop (or -OutputPath). Your user name is replaced by <user> in it unless -NoRedact.
      Read it before you share it; sharing is up to you.

  Exit code: 0 when nothing FAILED (warnings are fine), 1 when any check FAILED, 2 when the kit could
  not run (for example devsweep.exe was not found).

.PARAMETER DevSweepPath
  devsweep.exe (the command-line program), or a folder that holds it. Default: next to this script,
  the installed folder, PATH, the current folder.

.PARAMETER OutputPath
  Folder in which to create the Verify-DevSweep-<time> report folder. Default: the Desktop.

.PARAMETER TopFindings
  How many of the largest findings to re-measure (default 15).

.PARAMETER RunCleanTest
  Also run the removal tests on a fixture this script creates under %TEMP% (see above).

.PARAMETER CleanRepeats
  How many times the freed-space test is repeated; the median is judged (free space is noisy). Default 3.

.PARAMETER TestRecycleQuotaMb
  Only for a disposable machine (the CI runner): set the Recycle Bin's size limit for the fixture's
  drive for the duration of the clean test, so that the "fits in the bin" path is exercised even
  when Windows has no explicit limit configured. Refused unless GITHUB_ACTIONS=true or
  DEVSWEEP_VERIFY_DISPOSABLE=1. The previous setting is put back afterwards.

.PARAMETER NoRedact
  Keep your user name and OneDrive organisation name in the report.

.PARAMETER SelfTest
  Check the comparison logic on made-up data (and the native helpers on a temporary folder) and exit.

.PARAMETER NoPause
  Do not wait for Enter at the end (the script waits only when it was started by double-click).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Verify-DevSweep.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Verify-DevSweep.ps1 -RunCleanTest
#>
[CmdletBinding()]
param(
    [string] $DevSweepPath,
    [string] $OutputPath,
    [ValidateRange(1, 200)] [int] $TopFindings = 15,
    [switch] $RunCleanTest,
    [ValidateRange(1, 9)] [int] $CleanRepeats = 3,
    [int] $TestRecycleQuotaMb = 0,
    [switch] $NoRedact,
    [switch] $SelfTest,
    [switch] $NoPause,
    [ValidateRange(10, 3600)] [int] $SizeTimeLimitSeconds = 180,
    [ValidateRange(60, 7200)] [int] $CliTimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$script:KitVersion = '1.0'
$script:IsWindowsHost = ($env:OS -eq 'Windows_NT')

# ============================================================================================
# Native helpers (their own code: nothing is shared with DevSweep).
#   * DsvNative.Measure: walks a path with FindFirstFileEx, never enters a reparse point (a cloud
#     folder is the one exception, enumerating it reads no file), never opens a cloud-only file,
#     takes allocation size (FILE_STANDARD_INFO, GetCompressedFileSizeW for compressed and sparse
#     files) and counts a hard-linked file once (volume serial + file index).
#   * helpers for long paths, hashing, sparse files and removal of the kit's own fixture.
# Written in C# 5 so Windows PowerShell 5.1 can compile it too.
# ============================================================================================
$script:NativeSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class DsvTotals
{
    public long Bytes;
    public long Freeable;
    public long LogicalBytes;
    public long Files;
    public long Directories;
    public long HardLinkDuplicates;
    public long SharedLinkBytes;
    public long Reparse;
    public long CloudPlaceholders;
    public long Unreadable;
    public long AccessDenied;
    public long Missing;
    public bool Complete;
    public bool TimedOut;
    public long Milliseconds;
}

public static class DsvNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    static readonly IntPtr Invalid = new IntPtr(-1);
    const uint InvalidAttributes = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATA data);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr handle, out BY_HANDLE_FILE_INFORMATION info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(IntPtr handle, int infoClass, out FILE_STANDARD_INFO info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetCompressedFileSizeW(string name, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFileAttributesW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetFileAttributesW(string name, uint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteFileW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool RemoveDirectoryW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectoryW(string name, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(IntPtr handle, long distance, out long newPosition, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEndOfFile(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder buffer, uint length);

    public static string Ext(string path)
    {
        if (path.StartsWith(@"\\?\")) { return path; }
        if (path.StartsWith(@"\\")) { return @"\\?\UNC\" + path.Substring(2); }
        return @"\\?\" + path;
    }

    public static bool Exists(string path) { return GetFileAttributesW(Ext(path)) != InvalidAttributes; }

    public static uint Attributes(string path) { return GetFileAttributesW(Ext(path)); }

    public static string VolumeGuid(string root)
    {
        StringBuilder sb = new StringBuilder(64);
        string r = root.EndsWith("\\") ? root : root + "\\";
        return GetVolumeNameForVolumeMountPointW(r, sb, 64) ? sb.ToString() : null;
    }

    sealed class Walker
    {
        readonly DsvTotals t = new DsvTotals();
        readonly Dictionary<string, long[]> linked = new Dictionary<string, long[]>();
        readonly Stack<string> dirs = new Stack<string>();
        readonly Stopwatch sw = Stopwatch.StartNew();
        readonly long limitMs;

        public Walker(int seconds) { limitMs = seconds * 1000L; t.Complete = true; }

        void Fail(int error)
        {
            if (error == 2 || error == 3) { t.Missing++; return; }
            if (error == 5) { t.AccessDenied++; t.Complete = false; return; }
            t.Unreadable++; t.Complete = false;
        }

        public void AddRoot(string path)
        {
            string ext = Ext(path.Length > 3 ? path.TrimEnd('\\') : path);
            WIN32_FIND_DATA d;
            IntPtr h = FindFirstFileExW(ext, 1, out d, 0, IntPtr.Zero, 0);
            if (h == Invalid) { Fail(Marshal.GetLastWin32Error()); return; }
            FindClose(h);
            Handle(ext, d);
            Drain();
        }

        void Handle(string ext, WIN32_FIND_DATA d)
        {
            uint a = d.dwFileAttributes;
            bool isDir = (a & 0x10) != 0;
            bool reparse = (a & 0x400) != 0;
            uint tag = d.dwReserved0;
            if (reparse)
            {
                bool cloud = (tag & 0xFFFF0FFF) == 0x9000001A;
                bool measurable = isDir ? cloud : (cloud || tag == 0x80000017 || tag == 0x80000013);
                if (!measurable) { t.Reparse++; return; }
            }
            if (isDir) { t.Directories++; dirs.Push(ext); }
            else { MeasureFile(ext, d); }
        }

        void Drain()
        {
            while (dirs.Count > 0)
            {
                if (sw.ElapsedMilliseconds > limitMs) { t.TimedOut = true; t.Complete = false; dirs.Clear(); return; }
                string dir = dirs.Pop();
                WIN32_FIND_DATA d;
                IntPtr h = FindFirstFileExW(dir + "\\*", 1, out d, 0, IntPtr.Zero, 2);
                if (h == Invalid) { int e = Marshal.GetLastWin32Error(); if (e != 2 && e != 18) { Fail(e); } continue; }
                try
                {
                    do
                    {
                        string name = d.cFileName;
                        if (name == "." || name == "..") { continue; }
                        Handle(dir + "\\" + name, d);
                    }
                    while (FindNextFileW(h, out d));
                    int last = Marshal.GetLastWin32Error();
                    if (last != 18) { Fail(last); }
                }
                finally { FindClose(h); }
            }
        }

        void MeasureFile(string ext, WIN32_FIND_DATA d)
        {
            t.Files++;
            // Cloud-only files: never opened (opening one downloads it). They take no space here.
            if ((d.dwFileAttributes & (0x400000u | 0x40000u | 0x1000u)) != 0) { t.CloudPlaceholders++; return; }
            IntPtr h = CreateFileW(ext, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h == Invalid) { Fail(Marshal.GetLastWin32Error()); return; }
            BY_HANDLE_FILE_INFORMATION bi;
            FILE_STANDARD_INFO si;
            bool ok1 = GetFileInformationByHandle(h, out bi);
            bool ok2 = GetFileInformationByHandleEx(h, 1, out si, (uint)Marshal.SizeOf(typeof(FILE_STANDARD_INFO)));
            CloseHandle(h);
            if (!ok1 || !ok2) { t.Unreadable++; t.Complete = false; return; }
            long alloc = si.AllocationSize;
            if ((bi.dwFileAttributes & (0x800u | 0x200u)) != 0)
            {
                uint hi;
                uint lo = GetCompressedFileSizeW(ext, out hi);
                if (!(lo == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)) { alloc = ((long)hi << 32) | lo; }
            }
            t.LogicalBytes += ((long)bi.nFileSizeHigh << 32) | bi.nFileSizeLow;
            if (bi.nNumberOfLinks > 1)
            {
                string key = bi.dwVolumeSerialNumber.ToString("x8") + ":" + bi.nFileIndexHigh.ToString("x8") + ":" + bi.nFileIndexLow.ToString("x8");
                long[] entry;
                if (linked.TryGetValue(key, out entry)) { entry[2]++; t.HardLinkDuplicates++; }
                else { linked[key] = new long[] { alloc, bi.nNumberOfLinks, 1 }; t.Bytes += alloc; }
            }
            else
            {
                t.Bytes += alloc;
                t.Freeable += alloc;
            }
        }

        public DsvTotals Finish()
        {
            foreach (KeyValuePair<string, long[]> pair in linked)
            {
                if (pair.Value[2] >= pair.Value[1]) { t.Freeable += pair.Value[0]; }
                else { t.SharedLinkBytes += pair.Value[0]; }
            }
            t.Milliseconds = sw.ElapsedMilliseconds;
            return t;
        }
    }

    public static DsvTotals Measure(string[] paths, int seconds)
    {
        Walker w = new Walker(seconds);
        foreach (string p in paths) { w.AddRoot(p); }
        return w.Finish();
    }

    // ---- helpers for the clean test (long paths, hashing, sparse files, the kit's own fixture) ----

    static string ParentOf(string path)
    {
        int i = path.LastIndexOf('\\');
        return i <= 0 ? null : path.Substring(0, i);
    }

    public static void EnsureDir(string path)
    {
        List<string> missing = new List<string>();
        string cur = path.TrimEnd('\\');
        while (cur != null && cur.Length > 3 && GetFileAttributesW(Ext(cur)) == InvalidAttributes)
        {
            missing.Add(cur);
            cur = ParentOf(cur);
        }
        missing.Reverse();
        foreach (string d in missing)
        {
            if (!CreateDirectoryW(Ext(d), IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                if (e != 183) { throw new IOException("CreateDirectory failed (" + e + ") for " + d); }
            }
        }
    }

    public static void WriteBytes(string path, byte[] data)
    {
        string parent = ParentOf(path);
        if (parent != null) { EnsureDir(parent); }
        IntPtr h = CreateFileW(Ext(path), 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
        if (h == Invalid) { throw new IOException("CreateFile failed (" + Marshal.GetLastWin32Error() + ") for " + path); }
        using (FileStream fs = new FileStream(new SafeFileHandle(h, true), FileAccess.Write))
        {
            fs.Write(data, 0, data.Length);
        }
    }

    public static string Sha256(string path)
    {
        IntPtr h = CreateFileW(Ext(path), 0x80000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == Invalid) { throw new IOException("open failed (" + Marshal.GetLastWin32Error() + ") for " + path); }
        using (FileStream fs = new FileStream(new SafeFileHandle(h, true), FileAccess.Read))
        using (SHA256 sha = SHA256.Create())
        {
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }

    public static string FileIdentity(string path)
    {
        IntPtr h = CreateFileW(Ext(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h == Invalid) { return null; }
        BY_HANDLE_FILE_INFORMATION bi;
        bool ok = GetFileInformationByHandle(h, out bi);
        CloseHandle(h);
        if (!ok) { return null; }
        return bi.dwVolumeSerialNumber.ToString("x8") + ":" + bi.nFileIndexHigh.ToString("x8") + ":" + bi.nFileIndexLow.ToString("x8") + ":" + bi.nNumberOfLinks;
    }

    public static void MakeSparse(string path, long length)
    {
        string parent = ParentOf(path);
        if (parent != null) { EnsureDir(parent); }
        IntPtr h = CreateFileW(Ext(path), 0xC0000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
        if (h == Invalid) { throw new IOException("CreateFile failed (" + Marshal.GetLastWin32Error() + ") for " + path); }
        try
        {
            uint returned;
            if (!DeviceIoControl(h, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero)) { throw new IOException("FSCTL_SET_SPARSE failed (" + Marshal.GetLastWin32Error() + "): this file system has no sparse files."); }
            long pos;
            if (!SetFilePointerEx(h, length, out pos, 0) || !SetEndOfFile(h)) { throw new IOException("SetEndOfFile failed (" + Marshal.GetLastWin32Error() + ")"); }
        }
        finally { CloseHandle(h); }
    }

    // One line per entry: relative path, kind (D folder, F file, L link), length, sha256 (small, plain files), kept attributes.
    public static string[] Manifest(string root, long maxHashBytes)
    {
        List<string> lines = new List<string>();
        Collect(Ext(root.TrimEnd('\\')), "", lines, maxHashBytes);
        lines.Sort(StringComparer.Ordinal);
        return lines.ToArray();
    }

    static void Collect(string ext, string rel, List<string> lines, long maxHash)
    {
        List<string> names = new List<string>();
        List<WIN32_FIND_DATA> entries = new List<WIN32_FIND_DATA>();
        WIN32_FIND_DATA d;
        IntPtr h = FindFirstFileExW(ext + "\\*", 1, out d, 0, IntPtr.Zero, 0);
        if (h == Invalid) { return; }
        try
        {
            do
            {
                if (d.cFileName == "." || d.cFileName == "..") { continue; }
                names.Add(d.cFileName);
                entries.Add(d);
            }
            while (FindNextFileW(h, out d));
        }
        finally { FindClose(h); }
        for (int i = 0; i < names.Count; i++)
        {
            WIN32_FIND_DATA e = entries[i];
            string childRel = rel.Length == 0 ? names[i] : rel + "\\" + names[i];
            string childExt = ext + "\\" + names[i];
            uint a = e.dwFileAttributes;
            uint kept = a & 0xA07;
            long len = ((long)e.nFileSizeHigh << 32) | e.nFileSizeLow;
            if ((a & 0x400) != 0)
            {
                lines.Add(childRel + "\tL\t0\t-\t" + kept.ToString("x"));
            }
            else if ((a & 0x10) != 0)
            {
                lines.Add(childRel + "\tD\t0\t-\t" + kept.ToString("x"));
                Collect(childExt, childRel, lines, maxHash);
            }
            else
            {
                string hash = "-";
                if (len <= maxHash && (a & 0x200) == 0 && (a & (0x400000u | 0x40000u | 0x1000u)) == 0)
                {
                    try { hash = Sha256(childExt); } catch (IOException) { hash = "unreadable"; }
                }
                lines.Add(childRel + "\tF\t" + len + "\t" + hash + "\t" + kept.ToString("x"));
            }
        }
    }

    // Removes the kit's own fixture. Refuses any path that does not contain "devsweep-verify-". Links are removed, never followed.
    public static void DeleteTree(string path)
    {
        if (path.IndexOf("devsweep-verify-", StringComparison.OrdinalIgnoreCase) < 0) { throw new InvalidOperationException("not a verification fixture: " + path); }
        DeleteTreeCore(Ext(path.TrimEnd('\\')));
    }

    static void DeleteTreeCore(string ext)
    {
        uint a = GetFileAttributesW(ext);
        if (a == InvalidAttributes) { return; }
        bool isDir = (a & 0x10) != 0;
        bool reparse = (a & 0x400) != 0;
        if (isDir && !reparse)
        {
            List<string> names = new List<string>();
            WIN32_FIND_DATA d;
            IntPtr h = FindFirstFileExW(ext + "\\*", 1, out d, 0, IntPtr.Zero, 0);
            if (h != Invalid)
            {
                try
                {
                    do { if (d.cFileName != "." && d.cFileName != "..") { names.Add(d.cFileName); } }
                    while (FindNextFileW(h, out d));
                }
                finally { FindClose(h); }
            }
            foreach (string n in names) { DeleteTreeCore(ext + "\\" + n); }
            SetFileAttributesW(ext, 0x80);
            if (!RemoveDirectoryW(ext)) { throw new IOException("RemoveDirectory failed (" + Marshal.GetLastWin32Error() + ") for " + ext); }
        }
        else if (isDir)
        {
            if (!RemoveDirectoryW(ext)) { throw new IOException("removing the link failed (" + Marshal.GetLastWin32Error() + ") for " + ext); }
        }
        else
        {
            SetFileAttributesW(ext, 0x80);
            if (!DeleteFileW(ext)) { throw new IOException("DeleteFile failed (" + Marshal.GetLastWin32Error() + ") for " + ext); }
        }
    }
}
'@

function Initialize-Native {
    if ($script:IsWindowsHost -and -not ('DsvNative' -as [type])) {
        Add-Type -TypeDefinition $script:NativeSource -Language CSharp
    }
}

# ============================================================================================
# Pure comparison logic (no machine access; these are what -SelfTest checks)
# ============================================================================================

function Get-SizeTolerance {
    # PLAN section 1: within 1% or 10 MB, whichever is larger.
    param([long] $A, [long] $B)
    $onePercent = [long][Math]::Floor([Math]::Max([Math]::Abs([double]$A), [Math]::Abs([double]$B)) / 100)
    return [long][Math]::Max([long]10485760, $onePercent)
}

function Compare-Size {
    param([long] $Shown, [long] $Independent)
    $tolerance = Get-SizeTolerance $Shown $Independent
    $difference = $Shown - $Independent
    return [pscustomobject]@{
        Shown       = $Shown
        Independent = $Independent
        Difference  = $difference
        Tolerance   = $tolerance
        Within      = ([Math]::Abs($difference) -le $tolerance)
    }
}

$script:NoiseWords = @('x64', 'x86', 'amd64', '64bit', '32bit', 'bit', 'win64', 'win32', 'user', 'machine', 'version')

function Get-NameKey {
    # "Postman x64 11.2.0" -> "postman"; "GitHub Desktop" -> "githubdesktop"
    param([string] $Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $n = $Name.ToLowerInvariant()
    $n = [regex]::Replace($n, '\d+(\.\d+)+', ' ')
    $n = [regex]::Replace($n, '[^a-z0-9]+', ' ')
    $words = @(($n -split ' ') | Where-Object { $_ -and ($script:NoiseWords -notcontains $_) })
    return ($words -join '')
}

function Test-NameMatch {
    # Generous on purpose: a safety check should rather flag too much than too little.
    # The candidate may be longer than the app's name, never shorter ("Docker" does not mean "Docker Desktop").
    param([string] $AppName, [string] $Candidate)
    $a = Get-NameKey $AppName
    $c = Get-NameKey $Candidate
    if ($a.Length -eq 0 -or $c.Length -eq 0) { return $false }
    if ($a -eq $c) { return $true }
    if ($a.Length -ge 4 -and $c.Contains($a)) { return $true }
    return $false
}

function Get-FindingApp {
    param($Finding)
    if ($Finding.facts -and $Finding.facts.app) { return [string]$Finding.facts.app }
    return [string]$Finding.title
}

function Test-IsLeftoverFinding {
    param($Finding)
    if ($Finding.facts -and $Finding.facts.app) { return $true }
    $risk = [string]$Finding.risk
    return ($risk -ieq 'Leftover')
}

function Find-FalseLeftover {
    # For each leftover finding: which independent sources say the app is installed.
    param([object[]] $Findings, [object[]] $Independent)
    foreach ($finding in @($Findings)) {
        $app = Get-FindingApp $finding
        $hits = @($Independent | Where-Object { Test-NameMatch $app $_.Name })
        [pscustomobject]@{
            Finding = $finding
            App     = $app
            Hits    = $hits
            Strong  = @($hits | Where-Object { $_.Strength -eq 'Strong' }).Count
            Weak    = @($hits | Where-Object { $_.Strength -ne 'Strong' }).Count
        }
    }
}

function Compare-Listeners {
    # Both lists hold "pid:port" strings for listening TCP sockets.
    param([string[]] $DevSweep, [string[]] $Independent)
    $dev = @($DevSweep | Where-Object { $_ })
    $ind = @($Independent | Where-Object { $_ })
    $indPorts = @($ind | ForEach-Object { $_.Split(':')[1] })
    $missing = @(); $ownerDiffers = @()
    foreach ($k in $ind) {
        if ($dev -contains $k) { continue }
        $port = $k.Split(':')[1]
        $same = @($dev | Where-Object { $_.Split(':')[1] -eq $port })
        if ($same.Count -gt 0) { $ownerDiffers += ('{0} (DevSweep says {1})' -f $k, $same[0]) } else { $missing += $k }
    }
    $notConfirmed = @($dev | Where-Object { ($ind -notcontains $_) -and ($indPorts -notcontains $_.Split(':')[1]) })
    return [pscustomobject]@{ MissingFromDevSweep = $missing; OwnerDiffers = $ownerDiffers; NotConfirmed = $notConfirmed }
}

function ConvertTo-ComparablePath {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $p = $Path.Trim().Trim('"').Replace('/', '\')
    if ($p.StartsWith('\\?\')) { $p = $p.Substring(4) }
    return $p.TrimEnd('\').ToLowerInvariant()
}

function Compare-PathLists {
    # What an independent search found on PATH against what DevSweep listed.
    param([string[]] $DevSweepPaths, [string[]] $IndependentPaths)
    $dev = @($DevSweepPaths | ForEach-Object { ConvertTo-ComparablePath $_ } | Where-Object { $_ })
    $ind = @($IndependentPaths | Where-Object { $_ })
    $missing = @($ind | Where-Object { $dev -notcontains (ConvertTo-ComparablePath $_) })
    $firstMatches = $true
    if ($ind.Count -gt 0 -and $dev.Count -gt 0) { $firstMatches = ((ConvertTo-ComparablePath $ind[0]) -eq $dev[0]) }
    return [pscustomobject]@{ Missing = $missing; FirstMatches = $firstMatches }
}

function Test-StoreAliasPath {
    # %LOCALAPPDATA%\Microsoft\WindowsApps\<name>.exe is an app-execution alias, not an install.
    param([string] $Path, [string] $LocalAppData)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $p = ConvertTo-ComparablePath $Path
    if ($LocalAppData) {
        $root = (ConvertTo-ComparablePath $LocalAppData) + '\microsoft\windowsapps\'
        return ($p.StartsWith($root) -and $p.IndexOf('\', $root.Length) -lt 0)
    }
    return ($p -match '\\appdata\\local\\microsoft\\windowsapps\\[^\\]+$')
}

function Get-ExpectedDestination {
    # What Windows itself does with a Recycle Bin request, so DevSweep's claim can be judged
    # without using DevSweep's own policy code: fixed drives only; the bin is off (NukeOnDelete);
    # a limit smaller than the item (the shell deletes such items for good); the shell cannot
    # take paths over 259 characters. Anything else DevSweep cannot be sure of goes to its quarantine.
    param(
        [string] $DriveKind,
        [int] $PathLength,
        $QuotaBytes,
        [bool] $NukeOnDelete,
        [long] $ItemBytes
    )
    if ($DriveKind -ne 'Fixed') { return [pscustomobject]@{ Destination = 'Quarantine'; Why = "$DriveKind drives have no Recycle Bin" } }
    if ($NukeOnDelete) { return [pscustomobject]@{ Destination = 'Quarantine'; Why = 'the Recycle Bin is turned off for this drive' } }
    if ($PathLength -gt 259) { return [pscustomobject]@{ Destination = 'Quarantine'; Why = 'the path is longer than the shell can take' } }
    if ($null -eq $QuotaBytes) { return [pscustomobject]@{ Destination = 'Quarantine'; Why = 'the Recycle Bin limit is not known, so DevSweep cannot be sure it would keep the item' } }
    if ($ItemBytes -gt [long]$QuotaBytes) { return [pscustomobject]@{ Destination = 'Quarantine'; Why = 'the item is bigger than the Recycle Bin limit (Windows would delete it for good)' } }
    return [pscustomobject]@{ Destination = 'RecycleBin'; Why = 'it fits in the Recycle Bin' }
}

function Read-RecycleInfo {
    # The $I record: version, size, deletion time, original path (UTF-16).
    param([byte[]] $Bytes)
    if ($Bytes.Length -lt 28) { return $null }
    $version = [BitConverter]::ToInt64($Bytes, 0)
    $size = [BitConverter]::ToInt64($Bytes, 8)
    if ($version -eq 2) {
        $chars = [BitConverter]::ToInt32($Bytes, 24)
        if ($chars -lt 1 -or (28 + 2 * $chars) -gt $Bytes.Length) { return $null }
        $path = [Text.Encoding]::Unicode.GetString($Bytes, 28, 2 * $chars).TrimEnd([char]0)
    }
    elseif ($version -eq 1) {
        $path = [Text.Encoding]::Unicode.GetString($Bytes, 24, [Math]::Min(520, $Bytes.Length - 24)).TrimEnd([char]0)
    }
    else { return $null }
    return [pscustomobject]@{ Version = $version; Size = $size; OriginalPath = $path }
}

function Protect-Text {
    # Replaces the user name (and the OneDrive organisation name) so a report can be shared.
    param([string] $Text, [string[]] $Names, [switch] $Off)
    if ($Off -or [string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    foreach ($name in @($Names | Where-Object { $_ -and $_.Length -ge 2 } | Sort-Object { $_.Length } -Descending -Unique)) {
        $escaped = [regex]::Escape($name)
        $out = [regex]::Replace($out, '(?i)(?<=[\\/])' + $escaped + '(?=[\\/]|$|["''\s,;:\]\)])', '<user>')
        $out = [regex]::Replace($out, '(?i)(?<=\\Users\\)' + $escaped + '(?=\W|$)', '<user>')
    }
    $out = [regex]::Replace($out, '(?i)OneDrive - [^\\/"\r\n]+', 'OneDrive - <organisation>')
    return $out
}

function Get-Overall {
    param([object[]] $Results)
    $fail = @($Results | Where-Object { $_.Status -eq 'FAIL' }).Count
    $warn = @($Results | Where-Object { $_.Status -eq 'WARN' }).Count
    if ($fail -gt 0) { return 'FAIL' }
    if ($warn -gt 0) { return 'WARN' }
    return 'PASS'
}

function Get-Median {
    param([double[]] $Values)
    $v = @($Values | Sort-Object)
    if ($v.Count -eq 0) { return 0 }
    $mid = [int][Math]::Floor($v.Count / 2)
    if ($v.Count % 2 -eq 1) { return $v[$mid] }
    return ($v[$mid - 1] + $v[$mid]) / 2
}

function Format-Bytes {
    param([double] $Bytes)
    $sign = ''
    if ($Bytes -lt 0) { $sign = '-'; $Bytes = -$Bytes }
    if ($Bytes -ge 1GB) { return ('{0}{1:N2} GB' -f $sign, ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0}{1:N1} MB' -f $sign, ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0}{1:N1} KB' -f $sign, ($Bytes / 1KB)) }
    return ('{0}{1:N0} B' -f $sign, $Bytes)
}

function Get-Prop {
    param($Object, [string] $Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p -or $null -eq $p.Value) { return $Default }
    return $p.Value
}

# ============================================================================================
# What a failed check usually means, and where to look (shown in the report next to each problem)
# ============================================================================================
$script:Guidance = @{
    'cli'              = @{ Means = 'The DevSweep program did not run or printed something that is not valid JSON. Nothing else could be checked against it.'; Look = 'Run the same command by hand (the exact command is in the detail). windows/docs/m6-packaging.md (what is in each download), windows/README.md (CLI).' }
    'size'             = @{ Means = 'The size DevSweep shows differs from a second measurement by more than 1% or 10 MB. Usual causes: hard links counted twice or once too often, a junction or link followed (or not), a compressed or sparse file counted at its full length, a cloud-only file counted as on disk, or the folder changed while measuring.'; Look = 'PLAN section 2, rows "Sizes" and "Walking"; windows/src/DevSweep.Core/Platform (DirectoryWalker, FileMeasurer, HardLinkLedger); run `devsweep measure <path> --json` and compare hardLinkDuplicates, reparsePoints, cloudPlaceholderCount. Accuracy tests: windows/tests/DevSweep.Accuracy (WalkerTests, HardLinkTests, AllocationSizeTests).' }
    'size-incomplete'  = @{ Means = 'One of the two measurements could not read everything (access denied, a time limit, or a file in use), so no verdict is possible. DevSweep should then have said "at least" or shown a warning (promise 4).'; Look = 'If DevSweep showed the size as exact while the kit could not read files, look at windows/src/DevSweep.Core/Platform/SizeConfidence.cs. Otherwise re-run with fewer programs open or more time (-SizeTimeLimitSeconds).' }
    'false-leftover'   = @{ Means = 'DevSweep calls an app''s data a "leftover" because the app is removed, but Windows still lists the app as installed. Cleaning it would delete a working app''s data. This is a release blocker (PLAN section 6, "No-false-positive gate").'; Look = 'windows/rules/90-leftovers.yml and rules/packs/91-leftovers.yml (the app''s names, packageIds, exes, folders); windows/src/DevSweep.Core/Inventory (collectors, AppMatcher, AppInventory.IsInstalled); windows/docs/m3a-rules.md.' }
    'weak-leftover'    = @{ Means = 'DevSweep calls an app''s data a leftover while a folder with its name still exists in Program Files. That folder may be an empty remainder of an uninstall (then the finding is right) or a portable install.'; Look = 'Open the folder. If a program is in it, add its exe name to the rule''s `exes`/`folders` in windows/rules/90-leftovers.yml.' }
    'inventory-incomplete' = @{ Means = 'DevSweep reported leftovers although at least one place it reads installed apps from could not be read completely. It must then say "unknown" and offer nothing.'; Look = 'windows/src/DevSweep.Core/Inventory/AppInventory.cs (IsInstalled returns Unknown when a source is incomplete); windows/docs/m3a-rules.md.' }
    'missed-install'   = @{ Means = 'Windows lists an installed program in its Uninstall registry key that DevSweep''s inventory does not know. If a rule were written for that app, its data would be wrongly called a leftover.'; Look = 'windows/src/DevSweep.Core/Inventory/Collectors (registry collector) and AppMerger/AppMatcher. Some entries are meant to be skipped (updates, system components); confirm that this one should not be.' }
    'runtime-alias'    = @{ Means = 'A Microsoft Store "app execution alias" (python.exe in WindowsApps that only opens the Store) is listed as an installed runtime. It is not an install.'; Look = 'windows/docs/m3b-health.md (Store aliases); windows/src/DevSweep.Core/Health/Runtimes/Platform/AppExecLink.cs.' }
    'runtime-missing'  = @{ Means = 'A tool Windows finds on the PATH (or in its own registry keys, or through the py launcher or JAVA_HOME) is missing from DevSweep''s list of installs, or not on the PATH DevSweep believes a new terminal has.'; Look = 'windows/docs/m3b-health.md (discovery, PATH built from the registry); windows/src/DevSweep.Core/Health/Runtimes/Discovery and PathSearch. Tools that only exist in one shell (this window''s PATH) are reported separately as information.' }
    'runtime-ghost'    = @{ Means = 'DevSweep lists an install whose folder or program is not on disk.'; Look = 'windows/docs/m3b-health.md; the probe for that tool in windows/src/DevSweep.Core/Health/Runtimes/Discovery.' }
    'ports'            = @{ Means = 'The listening TCP ports DevSweep shows differ from what Windows reports (Get-NetTCPConnection), by port or by owning process.'; Look = 'windows/docs/m3b-health.md (Ports); windows/src/DevSweep.Core/Health/Ports/SocketTables.cs (GetExtendedTcpTable). A socket that opened or closed during the check is not an error; run again.' }
    'clean-destination' = @{ Means = 'DevSweep said an item went to the Recycle Bin or its quarantine, but the files are not where it said, or it chose a different place than Windows'' own rules imply. A "Recycle Bin" claim that is not backed by a $R/$I pair would mean the item was deleted for good.'; Look = 'PLAN section 2, row "Removal"; windows/src/DevSweep.Core/Platform/Recycling (RecyclePolicy, RecycleBin, RecycleBinIndex) and Platform/Quarantine; windows/src/DevSweep.Core/Cleaning/Cleaner.cs.' }
    'clean-outside'    = @{ Means = 'Something outside the item DevSweep was asked to remove changed (for example the target of a junction inside it). The cleaner must delete a link, never what it points to.'; Look = 'PLAN section 2, rows "Walking" and "Removal"; windows/src/DevSweep.Core/Safety (SafetyGuard, VerifiedItem) and Platform/TreeDeleter.cs, ReparseGuard.cs.' }
    'clean-restore'    = @{ Means = 'After restoring from History, the files are not byte-identical to what was there before (missing, different content, lost link or attributes).'; Look = 'windows/src/DevSweep.Core/Cleaning/HistoryStore.cs, Platform/Recycling/RecycleBinRestore.cs, Platform/Quarantine/QuarantineStore.cs.' }
    'clean-freed'      = @{ Means = 'The space that came free on the drive after a permanent delete differs from the size DevSweep showed by more than 1% or 10 MB. Free space is noisy (other programs write too), so the median of several runs is judged.'; Look = 'PLAN section 6, "Freed-space reconciliation"; windows/src/DevSweep.Core/Cleaning/Model.cs (FreedSpace) and Cleaner.cs; if only this check fails, repeat with -CleanRepeats 5 on a quiet machine.' }
    'clean-refusal'    = @{ Means = 'The test-only command that moves fixtures accepted a path it should have refused (not created by this script, or outside %TEMP%).'; Look = 'windows/src/DevSweep.Cli/Commands/Verify/VerifyCleanCommand.cs.' }
    'kit'              = @{ Means = 'The verification kit itself could not finish one of its checks (not a DevSweep problem by itself).'; Look = 'The detail says what happened. Send the report folder to the DevSweep developers if it keeps happening.' }
}

# ============================================================================================
# Results, console output
# ============================================================================================
$script:Results = New-Object 'System.Collections.Generic.List[object]'
$script:Sections = [ordered]@{}

function Add-Result {
    param(
        [string] $Area,
        [string] $Id,
        [ValidateSet('PASS', 'WARN', 'FAIL', 'INFO')] [string] $Status,
        [string] $Title,
        [string] $Detail = '',
        [string] $Kind = ''
    )
    $means = ''
    $look = ''
    if ($Kind -and $script:Guidance.ContainsKey($Kind) -and ($Status -eq 'WARN' -or $Status -eq 'FAIL')) {
        $means = $script:Guidance[$Kind].Means
        $look = $script:Guidance[$Kind].Look
    }
    $result = [pscustomobject]@{ Area = $Area; Id = $Id; Status = $Status; Title = $Title; Detail = $Detail; Kind = $Kind; Meaning = $means; LookAt = $look }
    $script:Results.Add($result)
    $color = switch ($Status) { 'PASS' { 'Green' } 'WARN' { 'Yellow' } 'FAIL' { 'Red' } default { 'Gray' } }
    Write-Host ('  [{0}] {1}' -f $Status, $Title) -ForegroundColor $color
    if ($Detail -and ($Status -eq 'FAIL' -or $Status -eq 'WARN')) { Write-Host ('         ' + ($Detail -replace "`r?`n", "`n         ")) -ForegroundColor DarkGray }
}

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

# ============================================================================================
# Running programs
# ============================================================================================
function ConvertTo-ArgString {
    param([string[]] $ArgumentList)
    $parts = foreach ($a in @($ArgumentList)) {
        if ($null -eq $a) { continue }
        if ($a.Length -eq 0) { '""' }
        elseif ($a -match '[\s"]') { '"' + ([regex]::Replace(([regex]::Replace($a, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1')) + '"' }
        else { $a }
    }
    return ($parts -join ' ')
}

function Invoke-Process {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList = @(),
        [int] $TimeoutSeconds = 120,
        [hashtable] $Environment,
        [string] $WorkingDirectory
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = ConvertTo-ArgString $ArgumentList
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [Text.Encoding]::UTF8
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    if ($Environment) { foreach ($key in $Environment.Keys) { $psi.EnvironmentVariables[[string]$key] = [string]$Environment[$key] } }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        try { $process.Kill() } catch { }
    }
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut   = $stdout.Result
        StdErr   = $stderr.Result
        TimedOut = $timedOut
        Seconds  = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
        Command  = ($FilePath + ' ' + $psi.Arguments)
    }
}

function Test-ConsoleExe {
    # PE header: Subsystem 3 is a console program (devsweep.exe), 2 the window app (DevSweep.exe).
    param([string] $Path)
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $buffer = New-Object byte[] 64
            if ($stream.Read($buffer, 0, 64) -lt 64) { return $false }
            $peOffset = [BitConverter]::ToInt32($buffer, 60)
            [void]$stream.Seek($peOffset + 24 + 68, [IO.SeekOrigin]::Begin)
            $sub = New-Object byte[] 2
            if ($stream.Read($sub, 0, 2) -lt 2) { return $false }
            return ([BitConverter]::ToUInt16($sub, 0) -eq 3)
        }
        finally { $stream.Dispose() }
    }
    catch { return $false }
}

function Find-DevSweepCli {
    param([string] $Explicit)
    $names = @('devsweep.exe')
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit -PathType Container) { foreach ($n in $names) { $candidates.Add((Join-Path $Explicit $n)); $candidates.Add((Join-Path (Join-Path $Explicit 'scripts') $n)) } }
        else { $candidates.Add($Explicit) }
    }
    else {
        if ($PSScriptRoot) {
            foreach ($n in $names) { $candidates.Add((Join-Path $PSScriptRoot $n)); $candidates.Add((Join-Path (Split-Path $PSScriptRoot -Parent) $n)) }
        }
        if ($env:LOCALAPPDATA) {
            $installed = Join-Path $env:LOCALAPPDATA 'Programs\DevSweep'
            foreach ($n in $names) { $candidates.Add((Join-Path (Join-Path $installed 'scripts') $n)); $candidates.Add((Join-Path $installed $n)) }
        }
        $onPath = Get-Command 'devsweep.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($onPath) { $candidates.Add($onPath.Source) }
        foreach ($n in $names) { $candidates.Add((Join-Path (Get-Location).Path $n)); $candidates.Add((Join-Path (Join-Path (Get-Location).Path 'scripts') $n)) }
    }
    $seen = @{}
    foreach ($c in $candidates) {
        if (-not $c -or $seen.ContainsKey($c.ToLowerInvariant())) { continue }
        $seen[$c.ToLowerInvariant()] = $true
        if ((Test-Path -LiteralPath $c -PathType Leaf) -and (Test-ConsoleExe $c)) { return (Resolve-Path -LiteralPath $c).Path }
    }
    return $null
}

# ============================================================================================
# Independent ground truth
# ============================================================================================
function Get-IndependentApps {
    $apps = New-Object 'System.Collections.Generic.List[object]'
    function Add-App($source, $name, $strength, $detail) {
        if ($name) { $apps.Add([pscustomobject]@{ Source = $source; Name = [string]$name; Strength = $strength; Detail = [string]$detail }) }
    }
    # 1. Uninstall keys, all four views.
    $views = @(
        @{ Hive = 'LocalMachine'; View = 'Registry64'; Label = 'installed programs, all users (64-bit view)' },
        @{ Hive = 'LocalMachine'; View = 'Registry32'; Label = 'installed programs, all users (32-bit view)' },
        @{ Hive = 'CurrentUser'; View = 'Registry64'; Label = 'installed programs, this user (64-bit view)' },
        @{ Hive = 'CurrentUser'; View = 'Registry32'; Label = 'installed programs, this user (32-bit view)' })
    $registryEntries = 0
    foreach ($v in $views) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]$v.Hive, [Microsoft.Win32.RegistryView]$v.View)
            $key = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
            if (-not $key) { continue }
            foreach ($subName in $key.GetSubKeyNames()) {
                $sub = $key.OpenSubKey($subName)
                if (-not $sub) { continue }
                $display = $sub.GetValue('DisplayName')
                $system = $sub.GetValue('SystemComponent')
                $parent = $sub.GetValue('ParentKeyName')
                $location = $sub.GetValue('InstallLocation')
                $publisher = $sub.GetValue('Publisher')
                $sub.Close()
                if (-not $display -or $system -eq 1 -or $parent) { continue }
                $registryEntries++
                Add-App $v.Label $display 'Strong' (@($location, $publisher) -join ' | ')
            }
        }
        catch { }
    }
    # 2. Store / MSIX packages, by Get-AppxPackage and by the package repository in the registry.
    $appx = @()
    try { $appx = @(Get-AppxPackage -ErrorAction Stop -WarningAction SilentlyContinue) } catch { $appx = @() }
    foreach ($p in $appx) { Add-App 'Store packages (Get-AppxPackage)' $p.Name 'Strong' $p.PackageFamilyName }
    try {
        $repo = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages')
        if ($repo) {
            foreach ($n in $repo.GetSubKeyNames()) { Add-App 'Store packages (registry)' ($n.Split('_')[0]) 'Strong' $n }
        }
    }
    catch { }
    # 3. Scoop and Chocolatey folders.
    $scoopRoots = @($env:SCOOP, $(if ($env:USERPROFILE) { Join-Path $env:USERPROFILE 'scoop' }), $env:SCOOP_GLOBAL, $(if ($env:ProgramData) { Join-Path $env:ProgramData 'scoop' })) | Where-Object { $_ }
    foreach ($root in $scoopRoots) {
        $appsDir = Join-Path $root 'apps'
        if (Test-Path -LiteralPath $appsDir) {
            foreach ($d in Get-ChildItem -LiteralPath $appsDir -Directory -ErrorAction SilentlyContinue) { if ($d.Name -ne 'scoop') { Add-App 'Scoop apps' $d.Name 'Strong' $d.FullName } }
        }
    }
    $choco = if ($env:ChocolateyInstall) { $env:ChocolateyInstall } elseif ($env:ProgramData) { Join-Path $env:ProgramData 'chocolatey' } else { $null }
    if ($choco -and (Test-Path -LiteralPath (Join-Path $choco 'lib'))) {
        foreach ($d in Get-ChildItem -LiteralPath (Join-Path $choco 'lib') -Directory -ErrorAction SilentlyContinue) { Add-App 'Chocolatey packages' $d.Name 'Strong' $d.FullName }
    }
    # 4. Start Menu shortcuts.
    foreach ($menu in @($(if ($env:APPDATA) { Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs' }), $(if ($env:ProgramData) { Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs' }))) {
        if ($menu -and (Test-Path -LiteralPath $menu)) {
            foreach ($f in Get-ChildItem -LiteralPath $menu -Recurse -Filter '*.lnk' -File -ErrorAction SilentlyContinue) { Add-App 'Start menu shortcuts' $f.BaseName 'Strong' $f.FullName }
        }
    }
    # 5. Program Files folders: weaker evidence (an uninstall can leave an empty folder).
    foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' }))) {
        if ($pf -and (Test-Path -LiteralPath $pf)) {
            foreach ($d in Get-ChildItem -LiteralPath $pf -Directory -ErrorAction SilentlyContinue) { Add-App 'Program Files folders' $d.Name 'Weak' $d.FullName }
        }
    }
    return [pscustomobject]@{ Apps = $apps.ToArray(); RegistryEntries = $registryEntries }
}

function Get-RegistryEnvironmentPath {
    # The PATH a new program started from Explorer gets: machine PATH, then user PATH, expanded.
    $machine = $null; $user = $null; $ext = $null
    try {
        $mk = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Session Manager\Environment')
        if ($mk) { $machine = $mk.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); $ext = $mk.GetValue('PATHEXT', '') }
        $uk = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
        if ($uk) { $user = $uk.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    }
    catch { }
    $joined = (@($machine, $user) | Where-Object { $_ }) -join ';'
    if (-not $ext) { $ext = '.COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC' }
    return [pscustomobject]@{ Path = [Environment]::ExpandEnvironmentVariables($joined); PathExt = $ext }
}

function Get-RegistryInstallDirs {
    # Where Windows' own registry says runtimes were installed.
    $found = New-Object 'System.Collections.Generic.List[object]'
    $views = @(@('LocalMachine', 'Registry64'), @('LocalMachine', 'Registry32'), @('CurrentUser', 'Registry64'))
    foreach ($v in $views) {
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]$v[0], [Microsoft.Win32.RegistryView]$v[1])
            foreach ($vendor in @('JavaSoft\JDK', 'JavaSoft\JRE', 'JavaSoft\Java Development Kit')) {
                $k = $base.OpenSubKey('SOFTWARE\' + $vendor)
                if ($k) { foreach ($n in $k.GetSubKeyNames()) { $s = $k.OpenSubKey($n); $d = $s.GetValue('JavaHome'); if ($d) { $found.Add([pscustomobject]@{ Tool = 'java'; Dir = [string]$d; Exe = 'bin\java.exe' }) } } }
            }
            foreach ($vendor in @('Eclipse Adoptium\JDK', 'Microsoft\JDK', 'Azul Systems\Zulu', 'Amazon Corretto\JDK')) {
                $k = $base.OpenSubKey('SOFTWARE\' + $vendor)
                if ($k) { foreach ($n in $k.GetSubKeyNames()) { $s = $k.OpenSubKey($n + '\hotspot\MSI'); if ($s) { $d = $s.GetValue('Path'); if ($d) { $found.Add([pscustomobject]@{ Tool = 'java'; Dir = [string]$d; Exe = 'bin\java.exe' }) } } } }
            }
            $py = $base.OpenSubKey('SOFTWARE\Python\PythonCore')
            if ($py) { foreach ($n in $py.GetSubKeyNames()) { $s = $py.OpenSubKey($n + '\InstallPath'); if ($s) { $d = $s.GetValue(''); if ($d) { $found.Add([pscustomobject]@{ Tool = 'python'; Dir = [string]$d; Exe = 'python.exe' }) } } } }
            $node = $base.OpenSubKey('SOFTWARE\Node.js')
            if ($node) { $d = $node.GetValue('InstallPath'); if ($d) { $found.Add([pscustomobject]@{ Tool = 'node'; Dir = [string]$d; Exe = 'node.exe' }) } }
            $go = $base.OpenSubKey('SOFTWARE\GoLang')
            if ($go) { $d = $go.GetValue('InstallLocation'); if ($d) { $found.Add([pscustomobject]@{ Tool = 'go'; Dir = [string]$d; Exe = 'bin\go.exe' }) } }
        }
        catch { }
    }
    return @($found | Where-Object { $_.Dir } | Sort-Object { $_.Tool + '|' + $_.Dir.ToLowerInvariant() } -Unique)
}

function Get-ListeningTcp {
    # Independent view of listening TCP sockets as "pid:port".
    $keys = New-Object 'System.Collections.Generic.HashSet[string]'
    $source = ''
    try {
        foreach ($c in @(Get-NetTCPConnection -State Listen -ErrorAction Stop)) { [void]$keys.Add(('{0}:{1}' -f $c.OwningProcess, $c.LocalPort)) }
        $source = 'Get-NetTCPConnection'
    }
    catch {
        $net = Invoke-Process -FilePath (Join-Path $env:SystemRoot 'System32\netstat.exe') -ArgumentList @('-ano', '-p', 'TCP') -TimeoutSeconds 60
        foreach ($line in ($net.StdOut -split "`r?`n")) {
            if ($line -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$') { [void]$keys.Add(('{0}:{1}' -f $Matches[2], $Matches[1])) }
        }
        $source = 'netstat -ano'
    }
    return [pscustomobject]@{ Keys = @($keys); Source = $source }
}

# ============================================================================================
# Environment capture (no secrets: no environment variables except a few paths, no file contents)
# ============================================================================================
function Get-EnvironmentFacts {
    param([string] $CliPath, $IndependentApps)
    $facts = [ordered]@{}
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $facts.windows = [ordered]@{
            product = $os.Caption; build = $os.BuildNumber; ubr = (Get-Prop $cv 'UBR'); displayVersion = (Get-Prop $cv 'DisplayVersion')
            edition = (Get-Prop $cv 'EditionID'); architecture = $os.OSArchitecture; powershell = $PSVersionTable.PSVersion.ToString()
        }
    }
    catch { $facts.windows = [ordered]@{ error = $_.Exception.Message } }
    $kinds = @{ 2 = 'Removable'; 3 = 'Fixed'; 4 = 'Network'; 5 = 'Optical'; 6 = 'RamDisk' }
    $volumes = @()
    try {
        foreach ($d in @(Get-CimInstance Win32_LogicalDisk)) {
            $devdrive = $null
            if ($d.DriveType -eq 3 -and $d.DeviceID) {
                try {
                    $q = Invoke-Process -FilePath (Join-Path $env:SystemRoot 'System32\fsutil.exe') -ArgumentList @('devdrv', 'query', $d.DeviceID) -TimeoutSeconds 20
                    if ($q.StdOut -match 'trusted developer volume|is a developer volume') { $devdrive = $true } elseif ($q.StdOut -match 'not a developer volume|not a trusted') { $devdrive = $false }
                }
                catch { }
            }
            $volumes += [ordered]@{ drive = $d.DeviceID; type = $kinds[[int]$d.DriveType]; fileSystem = $d.FileSystem; sizeBytes = [int64]$d.Size; freeBytes = [int64]$d.FreeSpace; devDrive = $devdrive }
        }
    }
    catch { }
    $facts.volumes = $volumes
    $facts.refsPresent = (@($volumes | Where-Object { $_.fileSystem -eq 'ReFS' }).Count -gt 0)
    $facts.devDrivePresent = (@($volumes | Where-Object { $_.devDrive -eq $true }).Count -gt 0)
    # Known folders: are Desktop and Documents redirected (OneDrive or elsewhere)?
    $known = [ordered]@{}
    $profile = $env:USERPROFILE
    foreach ($pair in @(@('Desktop', 'Desktop'), @('Documents', 'MyDocuments'), @('Pictures', 'MyPictures'))) {
        try {
            $p = [Environment]::GetFolderPath($pair[1])
            $redirected = $false; $why = ''
            if ($p -and $profile -and -not $p.StartsWith($profile, [StringComparison]::OrdinalIgnoreCase)) { $redirected = $true; $why = 'outside the profile folder' }
            elseif ($p -match '\\OneDrive') { $redirected = $true; $why = 'inside OneDrive' }
            $known[$pair[0]] = [ordered]@{ path = $p; redirected = $redirected; why = $why }
        }
        catch { }
    }
    $facts.knownFolders = $known
    $facts.oneDriveRedirectedKnownFolders = (@($known.Values | Where-Object { $_.why -eq 'inside OneDrive' }).Count -gt 0)
    # Tools at a high level: present or not, and who installed it (by folder), never versions or contents.
    $tools = [ordered]@{}
    foreach ($t in @('winget', 'scoop', 'choco', 'git', 'node', 'npm', 'python', 'py', 'java', 'dotnet', 'docker', 'wsl', 'code', 'cargo', 'go')) {
        $c = Get-Command $t -ErrorAction SilentlyContinue | Where-Object { $_.CommandType -in 'Application', 'ExternalScript' } | Select-Object -First 1
        if ($c) {
            $src = [string]$c.Source
            $by = if ($src -match '\\scoop\\') { 'Scoop' } elseif ($src -match '\\chocolatey\\') { 'Chocolatey' } elseif ($src -match '\\WindowsApps\\') { 'Store/alias' } else { 'other' }
            $tools[$t] = $by
        }
        else { $tools[$t] = 'not found' }
    }
    $facts.tools = $tools
    $ideNames = @()
    try {
        $all = @($IndependentApps.Apps) | Where-Object { $_.Source -like 'installed programs*' }
        foreach ($rx in @('Visual Studio (Community|Professional|Enterprise|Build Tools)', 'Visual Studio Code', 'Cursor', 'Android Studio', 'IntelliJ|PyCharm|WebStorm|Rider|GoLand|CLion|PhpStorm|RubyMine|DataGrip|JetBrains', 'Docker Desktop', 'Windows Subsystem for Linux', 'Git$|^Git ')) {
            $hit = @($all | Where-Object { $_.Name -match $rx } | Select-Object -First 3 -ExpandProperty Name)
            if ($hit.Count -gt 0) { $ideNames += ($hit | Select-Object -Unique) }
        }
    }
    catch { }
    $facts.ideAndTools = @($ideNames | Select-Object -Unique)
    # Which of the three reference profiles (PLAN section 6) this PC resembles.
    $profiles = @()
    $names = ($facts.ideAndTools -join '|')
    if ($tools['winget'] -ne 'not found' -and $names -match 'Visual Studio (Community|Professional|Enterprise)' -and $tools['dotnet'] -ne 'not found' -and $tools['wsl'] -ne 'not found') { $profiles += 'a (winget + Visual Studio + .NET + WSL)' }
    if ($tools['scoop'] -ne 'not found' -and ($tools['node'] -ne 'not found' -or $tools['python'] -ne 'not found') -and $names -match 'Docker Desktop') { $profiles += 'b (Scoop + Node/Python + Docker Desktop)' }
    if ($tools['choco'] -ne 'not found' -and $names -match 'Android Studio' -and $names -match 'JetBrains|IntelliJ|PyCharm|WebStorm|Rider|GoLand|CLion|PhpStorm|RubyMine|DataGrip' -and $facts.oneDriveRedirectedKnownFolders) { $profiles += 'c (Chocolatey + Android Studio + JetBrains + OneDrive Documents)' }
    $facts.looksLikeReferenceProfiles = $profiles
    if ($CliPath) {
        $vi = (Get-Item -LiteralPath $CliPath).VersionInfo
        $facts.devsweep = [ordered]@{ path = $CliPath; fileVersion = $vi.FileVersion; productVersion = $vi.ProductVersion }
    }
    $facts.kit = [ordered]@{ version = $script:KitVersion; started = (Get-Date).ToString('s'); ci = ($env:GITHUB_ACTIONS -eq 'true'); elevated = $(try { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { $null }) }
    return $facts
}

# ============================================================================================
# The read-only checks
# ============================================================================================
function Test-Sizes {
    param($Scan, [int] $Top, [int] $TimeLimit)
    Write-Step "Sizes: the $Top largest findings, re-measured independently"
    $findings = @($Scan.findings)
    $measurable = @($findings | Where-Object {
            $_.size -gt 0 -and @($_.paths).Count -gt 0 -and [string]$_.measurement -ne 'Reported'
        } | Sort-Object { [long]$_.size } -Descending | Select-Object -First $Top)
    $reported = @($findings | Where-Object { [string]$_.measurement -eq 'Reported' }).Count
    if ($measurable.Count -eq 0) {
        Add-Result 'Sizes' 'sizes.none' 'INFO' 'No finding with a file size to re-measure on this PC.' ("DevSweep found {0} finding(s); {1} had a size reported by a tool (not measurable from files)." -f $findings.Count, $reported)
        return @()
    }
    $rows = @()
    foreach ($f in $measurable) {
        $paths = [string[]]@($f.paths)
        $first = [DsvNative]::Measure($paths, $TimeLimit)
        $shown = [long]$f.size
        $cmp = Compare-Size $shown ([long]$first.Bytes)
        $status = 'PASS'; $note = ''; $kind = ''
        $uncertain = ([string]$f.confidence -in 'Partial', 'AccessDenied')
        if ($first.Missing -gt 0 -and $first.Files -eq 0 -and $first.Directories -eq 0) {
            $status = 'WARN'; $note = 'The folder is gone or changed since the scan.'; $kind = 'size-incomplete'
        }
        elseif (-not $first.Complete) {
            $status = 'WARN'; $kind = 'size-incomplete'
            $note = 'The kit could not read everything (' + $(if ($first.TimedOut) { 'time limit' } else { ('{0} unreadable, {1} access denied' -f $first.Unreadable, $first.AccessDenied) }) + ').'
            if ($uncertain) { $note += ' DevSweep also marked its size ' + [string]$f.confidence + '.' }
        }
        elseif (-not $cmp.Within) {
            $second = [DsvNative]::Measure($paths, $TimeLimit)
            $again = Compare-Size ([long]$first.Bytes) ([long]$second.Bytes)
            if (-not $again.Within) {
                $status = 'WARN'; $kind = 'size-incomplete'; $note = 'The folder was changing while measured (two kit measurements differ by ' + (Format-Bytes $again.Difference) + '); no verdict.'
            }
            else {
                $status = 'FAIL'; $kind = 'size'
                $note = 'DevSweep {0}, independent {1}: difference {2}, allowed {3}.' -f (Format-Bytes $shown), (Format-Bytes $first.Bytes), (Format-Bytes $cmp.Difference), (Format-Bytes $cmp.Tolerance)
            }
        }
        $row = [pscustomobject]@{
            Finding = [string]$f.title; Rule = [string]$f.ruleId; Paths = $paths; Confidence = [string]$f.confidence
            DevSweepBytes = $shown; IndependentBytes = [long]$first.Bytes; DifferenceBytes = [long]$cmp.Difference; ToleranceBytes = [long]$cmp.Tolerance
            DevSweepFreeable = [long](Get-Prop $f 'freeableBytes' 0); IndependentFreeable = [long]$first.Freeable
            Files = [long]$first.Files; HardLinkDuplicates = [long]$first.HardLinkDuplicates; Reparse = [long]$first.Reparse; CloudPlaceholders = [long]$first.CloudPlaceholders
            Complete = [bool]$first.Complete; Seconds = [Math]::Round($first.Milliseconds / 1000.0, 1); Status = $status; Note = $note
        }
        $rows += $row
        $title = '{0}: DevSweep {1}, independent {2}' -f $f.title, (Format-Bytes $shown), (Format-Bytes $first.Bytes)
        Add-Result 'Sizes' ('size.' + [string]$f.id) $status $title $note $kind
    }
    if ($reported -gt 0) { Add-Result 'Sizes' 'sizes.reported' 'INFO' ("{0} finding(s) have a size reported by their own tool (for example docker system df); they are not measured from files and were not re-measured." -f $reported) }
    $note2 = 'Alternate data streams are not counted by the kit (normally 0 bytes on disk); an unexplained small difference on a Downloads-like folder can be that.'
    Add-Result 'Sizes' 'sizes.method' 'INFO' ("Independent method: FindFirstFileEx walk, FILE_STANDARD_INFO allocation, GetCompressedFileSizeW for compressed and sparse files, hard links counted once by file ID, reparse points not entered, cloud-only files not opened. " + $note2)
    return $rows
}

function Test-Leftovers {
    param($Scan, $InventoryJson, $Independent)
    Write-Step 'Leftovers: is any app DevSweep calls "removed" still installed?'
    $leftovers = @(@($Scan.findings) | Where-Object { Test-IsLeftoverFinding $_ })
    $rows = @()
    $incomplete = @(@($InventoryJson.sources) | Where-Object { @($_.problems).Count -gt 0 })
    if ($leftovers.Count -eq 0) {
        Add-Result 'Leftovers' 'leftovers.none' 'PASS' 'DevSweep reported no leftovers on this PC, so there is nothing to call false.' ('Independent evidence collected: {0} program names from {1} registry entries and other sources.' -f @($Independent.Apps).Count, $Independent.RegistryEntries)
    }
    else {
        if ($incomplete.Count -gt 0) {
            Add-Result 'Leftovers' 'leftovers.incomplete' 'FAIL' 'Leftovers were offered although an inventory source could not be read completely.' (($incomplete | ForEach-Object { ('{0}: {1}' -f $_.source, (@($_.problems) -join '; ')) }) -join "`n") 'inventory-incomplete'
        }
        foreach ($r in @(Find-FalseLeftover -Findings $leftovers -Independent $Independent.Apps)) {
            $evidence = @($r.Hits | Select-Object -First 5 | ForEach-Object { '{0}: {1}' -f $_.Source, $_.Name }) -join '; '
            $status = 'PASS'; $kind = ''
            if ($r.Strong -gt 0) { $status = 'FAIL'; $kind = 'false-leftover' }
            elseif ($r.Weak -gt 0) { $status = 'WARN'; $kind = 'weak-leftover' }
            $title = if ($status -eq 'PASS') { '{0}: not installed (checked registry in four views, Store packages, Scoop, Chocolatey, Start menu, Program Files)' -f $r.App }
            else { '{0}: DevSweep says leftover, but found installed: {1}' -f $r.App, $evidence }
            Add-Result 'Leftovers' ('leftover.' + $r.App) $status $title $evidence $kind
            $rows += [pscustomobject]@{ App = $r.App; Rule = [string]$r.Finding.ruleId; Status = $status; Evidence = $evidence; Paths = @($r.Finding.paths) }
        }
    }
    # Missed installs: Windows lists a program the inventory does not know.
    $inventoryNames = @()
    foreach ($a in @($InventoryJson.apps)) { $inventoryNames += @($a.name) + @($a.names) + @($a.packageIds) + @($a.packageFamilyNames) }
    $inventoryKeys = @{}
    foreach ($n in $inventoryNames) { $k = Get-NameKey ([string]$n); if ($k) { $inventoryKeys[$k] = $true } }
    $missed = @()
    foreach ($a in @($Independent.Apps | Where-Object { $_.Source -like 'installed programs*' })) {
        $k = Get-NameKey $a.Name
        if (-not $k -or $inventoryKeys.ContainsKey($k)) { continue }
        $matched = $false
        foreach ($ik in $inventoryKeys.Keys) { if ($ik.Contains($k) -or ($k.Length -ge 4 -and $k.Contains($ik) -and $ik.Length -ge 4)) { $matched = $true; break } }
        if (-not $matched) { $missed += $a.Name }
    }
    $missed = @($missed | Select-Object -Unique)
    $totalRegistry = @($Independent.Apps | Where-Object { $_.Source -like 'installed programs*' } | Select-Object -ExpandProperty Name -Unique).Count
    if ($missed.Count -eq 0) {
        Add-Result 'Leftovers' 'inventory.missed' 'PASS' ("Every one of the {0} programs Windows lists as installed is also in DevSweep's inventory." -f $totalRegistry)
    }
    else {
        $share = if ($totalRegistry -gt 0) { [Math]::Round(100.0 * $missed.Count / $totalRegistry, 1) } else { 0 }
        Add-Result 'Leftovers' 'inventory.missed' 'WARN' ("{0} of {1} programs in the Uninstall registry keys ({2}%) are not in DevSweep's inventory: {3}" -f $missed.Count, $totalRegistry, $share, (($missed | Select-Object -First 15) -join ', ')) (($missed) -join "`n") 'missed-install'
    }
    return [pscustomobject]@{ Leftovers = $rows; Missed = $missed; RegistryPrograms = $totalRegistry }
}

function Test-Runtimes {
    param($Versions, [string] $WorkDirectory)
    Write-Step 'Runtimes and PATH: what is installed, and what a new terminal runs'
    $localAppData = $env:LOCALAPPDATA
    $reg = Get-RegistryEnvironmentPath
    $rows = @()
    $reports = @(@($Versions.runtimes) | Where-Object { $_ })
    $where = Join-Path $env:SystemRoot 'System32\where.exe'
    foreach ($r in $reports) {
        $id = [string]$r.id
        $installs = @($r.installs)
        $command = [string](Get-Prop (Get-Prop $r 'resolution') 'command' '')
        $hits = @(Get-Prop (Get-Prop $r 'resolution') 'hits' @())
        $hitPaths = @($hits | ForEach-Object { [string]$_.path })
        # (1) Store aliases must not be installs.
        $aliasInstalls = @($installs | Where-Object { (Test-StoreAliasPath ([string](Get-Prop $_ 'executable' '')) $localAppData) -or (Test-StoreAliasPath ([string](Get-Prop $_ 'path' '')) $localAppData) })
        if ($aliasInstalls.Count -gt 0) {
            Add-Result 'Runtimes' ("runtime.alias.$id") 'FAIL' ("{0}: a Microsoft Store alias is listed as an install: {1}" -f $r.name, (($aliasInstalls | ForEach-Object { $_.path }) -join '; ')) '' 'runtime-alias'
        }
        # (2) Listed installs exist on disk.
        foreach ($i in $installs) {
            $p = [string](Get-Prop $i 'path' ''); $exe = [string](Get-Prop $i 'executable' '')
            $present = ($p -and (Test-Path -LiteralPath $p)) -or ($exe -and (Test-Path -LiteralPath $exe))
            if (-not $present) { Add-Result 'Runtimes' ("runtime.ghost.$id") 'FAIL' ("{0} {1}: DevSweep lists an install that is not on disk: {2}" -f $r.name, $i.version, $p) '' 'runtime-ghost' }
        }
        # (3) The command: where.exe with the registry PATH (what Explorer gives a new program) against DevSweep's hits.
        if ($command) {
            $ind = @()
            try {
                $w = Invoke-Process -FilePath $where -ArgumentList @($command) -TimeoutSeconds 30 -WorkingDirectory $WorkDirectory -Environment @{ PATH = $reg.Path; PATHEXT = $reg.PathExt }
                $ind = @($w.StdOut -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^INFO:' })
            }
            catch { Add-Result 'Runtimes' ("runtime.where.$id") 'WARN' "where.exe could not run for $command : $($_.Exception.Message)" '' 'kit' }
            $cmp = Compare-PathLists -DevSweepPaths $hitPaths -IndependentPaths $ind
            $status = 'PASS'; $detail = ''
            if ($ind.Count -gt 0 -and $hitPaths.Count -eq 0) { $status = 'FAIL'; $detail = "where.exe finds $command at $($ind[0]) but DevSweep says it is not on the PATH." }
            elseif (@($cmp.Missing).Count -gt 0) { $status = 'WARN'; $detail = "where.exe also finds: $(@($cmp.Missing) -join '; ')" }
            elseif (-not $cmp.FirstMatches) { $status = 'WARN'; $detail = "DevSweep says a new terminal runs $($hitPaths[0]); where.exe says $($ind[0])." }
            $title = if ($ind.Count -eq 0 -and $hitPaths.Count -eq 0) { "$command : not on the PATH, as where.exe also says" } elseif ($status -eq 'PASS') { ("$command : DevSweep's PATH matches where.exe ({0} place(s); first {1})" -f $ind.Count, $ind[0]) } else { "$command : PATH differs from where.exe" }
            Add-Result 'Runtimes' ("runtime.path.$id") $status $title $detail 'runtime-missing'
            # Aliases on the PATH are fine as long as they are not installs (checked above).
            $aliasHits = @($ind | Where-Object { Test-StoreAliasPath $_ $localAppData })
            # Seen only in this shell's own PATH (information only; the registry PATH is what a new terminal gets).
            $shell = @()
            try {
                $w2 = Invoke-Process -FilePath $where -ArgumentList @($command) -TimeoutSeconds 30 -WorkingDirectory $WorkDirectory
                $shell = @($w2.StdOut -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notmatch '^INFO:' })
            }
            catch { }
            $onlyHere = @($shell | Where-Object { @($ind | ForEach-Object { ConvertTo-ComparablePath $_ }) -notcontains (ConvertTo-ComparablePath $_) })
            if ($onlyHere.Count -gt 0) { Add-Result 'Runtimes' ("runtime.shellonly.$id") 'INFO' ("$command is also visible in this window's PATH only (not in the registry PATH): " + ($onlyHere -join '; ')) }
            $rows += [pscustomobject]@{ Tool = $id; Command = $command; Installs = @($installs | ForEach-Object { '{0} ({1})' -f $_.version, $_.path }); DevSweepHits = $hitPaths; WhereHits = $ind; AliasHits = $aliasHits; Status = $status }
        }
    }
    # (4) Installs found through other independent doors.
    $listed = @{}
    foreach ($r in $reports) { $listed[[string]$r.id] = @(@($r.installs) | ForEach-Object { @([string](Get-Prop $_ 'path' ''), [string](Get-Prop $_ 'executable' '')) } | Where-Object { $_ } | ForEach-Object { ConvertTo-ComparablePath $_ }) }
    function Test-Listed($tool, $dir) {
        $d = ConvertTo-ComparablePath $dir
        foreach ($l in @($listed[$tool])) { if ($l -eq $d -or $l.StartsWith($d + '\') -or $d.StartsWith($l + '\')) { return $true } }
        return $false
    }
    $regDirs = @(Get-RegistryInstallDirs)
    $missing = @()
    foreach ($e in $regDirs) {
        if (-not $listed.ContainsKey($e.Tool)) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $e.Dir $e.Exe))) { continue }
        if (-not (Test-Listed $e.Tool $e.Dir)) { $missing += ('{0}: {1} (registry)' -f $e.Tool, $e.Dir) }
    }
    $py = Get-Command 'py.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($py -and $listed.ContainsKey('python')) {
        try {
            $q = Invoke-Process -FilePath $py.Source -ArgumentList @('-0p') -TimeoutSeconds 30
            foreach ($line in ($q.StdOut -split "`r?`n")) {
                if ($line -match '(?<p>[A-Za-z]:\\.*?\.exe)\s*$') {
                    $exe = $Matches['p']
                    if (-not (Test-StoreAliasPath $exe $localAppData) -and -not (Test-Listed 'python' (Split-Path $exe -Parent))) { $missing += ('python: {0} (py launcher)' -f $exe) }
                }
            }
        }
        catch { }
    }
    foreach ($scope in @('User', 'Machine')) {
        $jh = [Environment]::GetEnvironmentVariable('JAVA_HOME', $scope)
        if ($jh -and $listed.ContainsKey('java') -and (Test-Path -LiteralPath (Join-Path $jh 'bin\java.exe')) -and -not (Test-Listed 'java' $jh)) { $missing += ('java: {0} (JAVA_HOME, {1})' -f $jh, $scope) }
    }
    $missing = @($missing | Select-Object -Unique)
    if ($missing.Count -eq 0) { Add-Result 'Runtimes' 'runtime.registry' 'PASS' ("Every runtime install Windows' registry, the py launcher and JAVA_HOME point to is in DevSweep's list ({0} registry entries looked at)." -f $regDirs.Count) }
    else { Add-Result 'Runtimes' 'runtime.registry' 'WARN' ("{0} install(s) found independently are not in DevSweep's list: {1}" -f $missing.Count, ($missing -join '; ')) ($missing -join "`n") 'runtime-missing' }
    return [pscustomobject]@{ Rows = $rows; MissingFromDevSweep = $missing; RegistryPath = $reg.Path }
}

function Test-Ports {
    param($PortsJson)
    Write-Step 'Ports: listening TCP sockets against Windows'
    $before = Get-ListeningTcp
    $dev = @(@($PortsJson.ports) | Where-Object { [string]$_.protocol -ieq 'tcp' } | ForEach-Object { '{0}:{1}' -f $_.pid, $_.port })
    $after = Get-ListeningTcp
    # A socket must be there in both snapshots to count; one in only one of them opened or closed meanwhile.
    $stable = @($before.Keys | Where-Object { $after.Keys -contains $_ })
    $union = @($before.Keys + $after.Keys | Select-Object -Unique)
    $stable = @($stable | Where-Object { -not $_.StartsWith('0:') })
    $cmp = Compare-Listeners -DevSweep $dev -Independent $stable
    $notConfirmed = @($dev | Where-Object { $union -notcontains $_ })
    $ownerDiffers = @($cmp.OwnerDiffers)
    $missing = @($cmp.MissingFromDevSweep)
    $status = 'PASS'
    if ($missing.Count -gt 0) { $status = 'FAIL' } elseif ($ownerDiffers.Count -gt 0 -or $notConfirmed.Count -gt 0) { $status = 'WARN' }
    $title = ("{0} listening TCP socket(s) by {1}; DevSweep lists {2}." -f $stable.Count, $before.Source, $dev.Count)
    $detailLines = @()
    if ($missing.Count -gt 0) { $detailLines += ('Windows reports, DevSweep does not (pid:port): ' + ($missing -join ', ')) }
    if ($ownerDiffers.Count -gt 0) { $detailLines += ('Same port, different owner: ' + ($ownerDiffers -join ', ')) }
    if ($notConfirmed.Count -gt 0) { $detailLines += ('DevSweep lists, Windows did not report in either snapshot: ' + ($notConfirmed -join ', ')) }
    Add-Result 'Ports' 'ports.tcp' $status $title ($detailLines -join "`n") 'ports'
    return [pscustomobject]@{ Windows = $stable; DevSweep = $dev; MissingFromDevSweep = $missing; OwnerDiffers = $ownerDiffers; NotConfirmed = $notConfirmed; Source = $before.Source }
}

# ============================================================================================
# The opt-in clean test
# ============================================================================================
function Get-FreeBytes {
    param([string] $Path)
    $root = [IO.Path]::GetPathRoot($Path)
    $samples = @()
    for ($i = 0; $i -lt 3; $i++) { $samples += [double](New-Object System.IO.DriveInfo $root).AvailableFreeSpace; Start-Sleep -Milliseconds 250 }
    return [long](Get-Median $samples)
}

function New-RandomFile {
    param([string] $Path, [long] $Bytes)
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    try {
        $buffer = New-Object byte[] (1MB)
        $left = $Bytes
        while ($left -gt 0) {
            $n = [int][Math]::Min([long]$buffer.Length, $left)
            $rng.GetBytes($buffer)
            $stream.Write($buffer, 0, $n)
            $left -= $n
        }
    }
    finally { $stream.Dispose(); $rng.Dispose() }
}

function Get-RecycleQuota {
    # The Recycle Bin's limit for a drive, read the way Windows stores it; $null when nothing is configured.
    param([string] $Root)
    $result = [pscustomobject]@{ Bytes = $null; Nuke = $false; Source = 'no limit is configured' }
    try {
        $policy = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer'
        foreach ($hive in @([Microsoft.Win32.Registry]::LocalMachine, [Microsoft.Win32.Registry]::CurrentUser)) {
            $k = $hive.OpenSubKey($policy)
            if ($k) {
                if ($k.GetValue('NoRecycleFiles') -eq 1) { $result.Nuke = $true; $result.Source = 'group policy NoRecycleFiles'; return $result }
            }
        }
        $guidPath = [DsvNative]::VolumeGuid($Root)
        if ($guidPath -match '\{[0-9a-fA-F-]+\}') {
            $guid = $Matches[0]
            $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\' + $guid)
            if ($k) {
                if ($k.GetValue('NukeOnDelete') -eq 1) { $result.Nuke = $true; $result.Source = 'BitBucket NukeOnDelete'; return $result }
                $mb = $k.GetValue('MaxCapacity')
                if ($mb -is [int] -and $mb -gt 0) { $result.Bytes = [long]$mb * 1MB; $result.Source = 'BitBucket MaxCapacity'; return $result }
            }
        }
    }
    catch { }
    return $result
}

function Set-TestRecycleQuota {
    # Disposable machines only. Returns what is needed to put things back.
    param([string] $Root, [int] $Megabytes)
    $guidPath = [DsvNative]::VolumeGuid($Root)
    if (-not ($guidPath -match '\{[0-9a-fA-F-]+\}')) { return $null }
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\' + $Matches[0]
    $state = [pscustomobject]@{ Path = $path; KeyExisted = (Test-Path $path); Max = $null; Nuke = $null }
    if ($state.KeyExisted) {
        $state.Max = (Get-ItemProperty -Path $path -Name MaxCapacity -ErrorAction SilentlyContinue).MaxCapacity
        $state.Nuke = (Get-ItemProperty -Path $path -Name NukeOnDelete -ErrorAction SilentlyContinue).NukeOnDelete
    }
    else { New-Item -Path $path -Force | Out-Null }
    Set-ItemProperty -Path $path -Name MaxCapacity -Value $Megabytes -Type DWord
    Set-ItemProperty -Path $path -Name NukeOnDelete -Value 0 -Type DWord
    return $state
}

function Restore-TestRecycleQuota {
    param($State)
    if ($null -eq $State) { return }
    try {
        if (-not $State.KeyExisted) { Remove-Item -Path $State.Path -Recurse -Force -ErrorAction SilentlyContinue; return }
        if ($null -eq $State.Max) { Remove-ItemProperty -Path $State.Path -Name MaxCapacity -ErrorAction SilentlyContinue } else { Set-ItemProperty -Path $State.Path -Name MaxCapacity -Value $State.Max -Type DWord }
        if ($null -eq $State.Nuke) { Remove-ItemProperty -Path $State.Path -Name NukeOnDelete -ErrorAction SilentlyContinue } else { Set-ItemProperty -Path $State.Path -Name NukeOnDelete -Value $State.Nuke -Type DWord }
    }
    catch { }
}

function Assert-OwnFixture {
    # The script refuses to create or remove anything outside a folder it made: under %TEMP%, named devsweep-verify-*, with the marker.
    param([string] $Root)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $leaf = Split-Path $full -Leaf
    if (-not $full.StartsWith($temp + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "refusing: $full is not inside $temp" }
    if ($leaf -notlike 'devsweep-verify-*') { throw "refusing: $full is not a devsweep-verify-* folder" }
    if (-not (Test-Path -LiteralPath (Join-Path $full '.devsweep-verify-fixture'))) { throw "refusing: $full has no marker file" }
}

function Invoke-VerifyClean {
    param([string] $Cli, [string[]] $ArgumentList, [int] $TimeoutSeconds = 300)
    $p = Invoke-Process -FilePath $Cli -ArgumentList (@('__verify-clean') + $ArgumentList) -TimeoutSeconds $TimeoutSeconds
    $json = $null
    try { $json = $p.StdOut | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Process = $p; Json = $json }
}

function Compare-Manifests {
    # Strict: path, kind, length, content hash. Attributes are reported separately.
    param([string[]] $Before, [string[]] $After)
    $strip = { param($l) $f = $l.Split("`t"); ($f[0..3] -join "`t") }
    $b = @{}; foreach ($l in $Before) { $f = $l.Split("`t"); $b[$f[0]] = $l }
    $a = @{}; foreach ($l in $After) { $f = $l.Split("`t"); $a[$f[0]] = $l }
    $content = @(); $attrs = @()
    foreach ($k in $b.Keys) {
        if (-not $a.ContainsKey($k)) { $content += "missing after restore: $k"; continue }
        if ((& $strip $b[$k]) -ne (& $strip $a[$k])) { $content += "changed: $k"; continue }
        if ($b[$k].Split("`t")[4] -ne $a[$k].Split("`t")[4]) { $attrs += "attributes changed: $k" }
    }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $content += "appeared: $k" } }
    return [pscustomobject]@{ Content = $content; Attributes = $attrs }
}

function Invoke-CleanTest {
    param([string] $Cli, [string] $WorkDirectory)
    Write-Step 'Clean test (opt in): a fixture this script creates, moved and restored by DevSweep''s cleaner'
    $rows = [ordered]@{}
    $root = Join-Path ([IO.Path]::GetTempPath()) ('devsweep-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $root | Out-Null
    Set-Content -LiteralPath (Join-Path $root '.devsweep-verify-fixture') -Value ("created by Verify-DevSweep.ps1 at " + (Get-Date).ToString('s'))
    Assert-OwnFixture $root
    $history = Join-Path $root 'history.json'
    $volume = [IO.Path]::GetPathRoot($root)
    $driveInfo = New-Object System.IO.DriveInfo $volume
    $quotaState = $null
    try {
        Write-Host "  fixture folder: $root"
        if ($TestRecycleQuotaMb -gt 0) {
            if ($env:GITHUB_ACTIONS -eq 'true' -or $env:DEVSWEEP_VERIFY_DISPOSABLE -eq '1') { $quotaState = Set-TestRecycleQuota -Root $volume -Megabytes $TestRecycleQuotaMb; Write-Host "  Recycle Bin limit set to $TestRecycleQuotaMb MB for this test (disposable machine); it is put back afterwards." }
            else { Add-Result 'Clean' 'clean.quota' 'WARN' '-TestRecycleQuotaMb ignored: it is only allowed on a disposable machine (GITHUB_ACTIONS=true or DEVSWEEP_VERIFY_DISPOSABLE=1).' '' 'kit' }
        }
        $quota = Get-RecycleQuota $volume
        $driveKind = switch ($driveInfo.DriveType) { 'Fixed' { 'Fixed' } default { [string]$driveInfo.DriveType } }
        Add-Result 'Clean' 'clean.setup' 'INFO' ("Drive {0} ({1}, {2}); Recycle Bin limit: {3} ({4})." -f $volume, $driveInfo.DriveFormat, $driveKind, $(if ($null -ne $quota.Bytes) { Format-Bytes $quota.Bytes } else { 'not configured' }), $quota.Source)

        # --- refusal checks: the test-only command must never act on anything but this script's own fixture ---
        $stranger = Join-Path ([IO.Path]::GetTempPath()) ('devsweep-verify-stranger-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $stranger | Out-Null
        try {
            Set-Content -LiteralPath (Join-Path $stranger 'keep.txt') -Value 'must survive'
            $itemA = Join-Path $stranger 'keep.txt'
            $r1 = Invoke-VerifyClean $Cli @('run', $itemA, '--allow-root', $stranger, '--history', (Join-Path $stranger 'h.json'))
            $r2 = Invoke-VerifyClean $Cli @('run', $itemA, '--allow-root', $root, '--history', $history)
            $r3 = Invoke-VerifyClean $Cli @('run', $root, '--allow-root', $root, '--history', $history)
            $refused = ($r1.Json -and $r1.Json.succeeded -eq $false) -and ($r2.Json -and $r2.Json.succeeded -eq $false) -and ($r3.Json -and $r3.Json.succeeded -eq $false)
            $survived = Test-Path -LiteralPath $itemA
            if ($refused -and $survived -and (Test-Path -LiteralPath $root)) { Add-Result 'Clean' 'clean.refusal' 'PASS' 'The test-only command refused a folder without the marker, a path outside the fixture root, and the root itself; nothing was touched.' }
            else { Add-Result 'Clean' 'clean.refusal' 'FAIL' 'The test-only command did not refuse everything it should have.' ("no marker: {0}; outside root: {1}; the root itself: {2}; stranger file survived: {3}" -f $r1.Process.StdOut.Trim(), $r2.Process.StdOut.Trim(), $r3.Process.StdOut.Trim(), $survived) 'clean-refusal' }
        }
        finally { try { [DsvNative]::DeleteTree($stranger) } catch { } }

        # --- the decoy outside every item: a junction inside an item points here ---
        $decoy = Join-Path $root 'decoy'
        New-Item -ItemType Directory -Path $decoy | Out-Null
        New-RandomFile (Join-Path $decoy 'sentinel.bin') (512KB)
        $decoyBefore = [DsvNative]::Manifest($decoy, 64MB)
        $items = Join-Path $root 'items'
        New-Item -ItemType Directory -Path $items | Out-Null

        function New-MixedItem([string] $Path, [bool] $Big) {
            New-Item -ItemType Directory -Path $Path | Out-Null
            New-RandomFile (Join-Path $Path 'random.bin') $(if ($Big) { 96MB } else { 4MB })
            $many = Join-Path $Path 'many'; New-Item -ItemType Directory -Path $many | Out-Null
            $rand = New-Object System.Random 42
            for ($i = 0; $i -lt 150; $i++) { $b = New-Object byte[] ($rand.Next(1, 20000)); $rand.NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $many ("f{0:D3}.dat" -f $i)), $b) }
            $hard = Join-Path $Path 'hard'; New-Item -ItemType Directory -Path $hard | Out-Null
            New-RandomFile (Join-Path $hard 'a.bin') (2MB)
            New-Item -ItemType HardLink -Path (Join-Path $hard 'b.bin') -Target (Join-Path $hard 'a.bin') | Out-Null
            New-Item -ItemType Junction -Path (Join-Path $Path 'link-to-decoy') -Target $decoy | Out-Null
            $text = ('compressible line of text ' * 40 + "`r`n") * 4000
            [IO.File]::WriteAllText((Join-Path $Path 'compressible.txt'), $text)
            $c = Invoke-Process -FilePath (Join-Path $env:SystemRoot 'System32\compact.exe') -ArgumentList @('/c', '/q', (Join-Path $Path 'compressible.txt')) -TimeoutSeconds 60
            Set-Content -LiteralPath (Join-Path $Path 'readonly.txt') -Value 'read only'
            (Get-Item -LiteralPath (Join-Path $Path 'readonly.txt')).IsReadOnly = $true
        }

        # ---------- cases that move the item and put it back ----------
        $cases = @()
        $mixed = Join-Path $items 'mixed'
        New-MixedItem $mixed $false
        $cases += [pscustomobject]@{ Name = 'mixed'; Path = $mixed; Sparse = $false }
        # A plain folder (no read-only file, no junction): where the Recycle Bin itself can be checked ($I/$R files).
        $plain = Join-Path $items 'plain'
        New-Item -ItemType Directory -Path $plain | Out-Null
        New-RandomFile (Join-Path $plain 'a.bin') (3MB)
        $plainRand = New-Object System.Random 7
        for ($i = 0; $i -lt 25; $i++) { $b = New-Object byte[] ($plainRand.Next(1, 9000)); $plainRand.NextBytes($b); [IO.File]::WriteAllBytes((Join-Path $plain ("p{0:D2}.dat" -f $i)), $b) }
        $cases += [pscustomobject]@{ Name = 'plain'; Path = $plain; Sparse = $false }
        $longName = ('l' * 200)
        $long = Join-Path $items $longName
        [DsvNative]::EnsureDir($long)
        [DsvNative]::WriteBytes((Join-Path $long 'inside.bin'), (New-Object byte[] 300000))
        $cases += [pscustomobject]@{ Name = 'long path'; Path = $long; Sparse = $false }
        $bigPath = Join-Path $items 'sparse-big'
        New-Item -ItemType Directory -Path $bigPath | Out-Null
        $bigLogical = if ($null -ne $quota.Bytes) { $quota.Bytes + 32MB } else { 1GB }
        $sparseOk = $true
        if ($bigLogical -gt 8TB) { $sparseOk = $false }
        else { try { [DsvNative]::MakeSparse((Join-Path $bigPath 'huge.sparse'), $bigLogical) } catch { $sparseOk = $false; Add-Result 'Clean' 'clean.sparse' 'INFO' ('The sparse "bigger than the Recycle Bin" fixture was skipped: ' + $_.Exception.Message) } }
        if ($sparseOk) { $cases += [pscustomobject]@{ Name = 'bigger than the Recycle Bin limit (sparse)'; Path = $bigPath; Sparse = $true } }

        foreach ($case in $cases) {
            $name = $case.Name
            $path = $case.Path
            $length = $path.Length
            $before = [DsvNative]::Manifest($path, 64MB)
            $totals = [DsvNative]::Measure([string[]]@($path), 120)
            $bytes = [long][Math]::Max($totals.Bytes, $totals.LogicalBytes)
            $expected = Get-ExpectedDestination -DriveKind $driveKind -PathLength $length -QuotaBytes $quota.Bytes -NukeOnDelete $quota.Nuke -ItemBytes $bytes
            $freeBefore = Get-FreeBytes $root
            $run = Invoke-VerifyClean $Cli @('run', $path, '--allow-root', $root, '--history', $history)
            Start-Sleep -Milliseconds 400
            $freeAfter = Get-FreeBytes $root
            $json = $run.Json
            if (-not $json) { Add-Result 'Clean' "clean.$name" 'FAIL' "${name}: the cleaner printed nothing readable." ($run.Process.StdOut + $run.Process.StdErr) 'clean-destination'; continue }
            if (-not $json.succeeded) {
                Add-Result 'Clean' "clean.$name" 'WARN' "${name}: DevSweep refused or could not move it ($($json.message)); nothing was changed (a refusal is safe, but the case could not be verified)." ([string]$json.message) 'clean-destination'
                continue
            }
            $entry = $json.entry
            $moved = @($entry.items)[0]
            $destination = [string]$moved.destination
            $stillThere = [DsvNative]::Exists($path)
            $problems = @()
            if ($stillThere) { $problems += 'the original path still exists' }
            switch ($destination) {
                'RecycleBin' {
                    $r = [string]$moved.location; $i = [string]$moved.recycleInfo
                    if (-not $r -or -not [DsvNative]::Exists($r)) { $problems += 'claimed Recycle Bin but no $R item exists at the recorded place' }
                    if (-not $i -or -not [DsvNative]::Exists($i)) { $problems += 'claimed Recycle Bin but no $I record exists' }
                    else {
                        $info = Read-RecycleInfo ([IO.File]::ReadAllBytes($i))
                        if (-not $info) { $problems += 'the $I record could not be read' }
                        elseif ($info.OriginalPath.TrimEnd('\') -ine $path.TrimEnd('\')) { $problems += ('the $I record says the original was ' + $info.OriginalPath) }
                    }
                    if ($r -and $r -notmatch '\\\$Recycle\.Bin\\S-') { $problems += 'the $R item is not in a $Recycle.Bin folder of this drive' }
                }
                'Quarantine' {
                    $r = [string]$moved.location
                    if (-not $r -or -not [DsvNative]::Exists($r)) { $problems += 'claimed quarantine but nothing exists at the recorded place' }
                    if ($r -match '\$Recycle\.Bin') { $problems += 'claimed quarantine but the place is inside the Recycle Bin' }
                }
                default { $problems += "the item went to '$destination', not to the Recycle Bin or quarantine (a permanent delete was not asked for)" }
            }
            $decoyNow = [DsvNative]::Manifest($decoy, 64MB)
            $outside = Compare-Manifests $decoyBefore $decoyNow
            if (@($outside.Content).Count -gt 0) { $problems += ('OUTSIDE THE ITEM: ' + (@($outside.Content) -join '; ')) }
            $destStatus = 'PASS'
            if ($problems.Count -gt 0) { $destStatus = 'FAIL' }
            $agree = ($destination -eq $expected.Destination)
            $msg = "${name}: DevSweep put it in $destination" + $(if ($agree) { " (Windows' rules agree: $($expected.Why))" } else { " but Windows' rules suggest $($expected.Destination) ($($expected.Why))" })
            if ($destStatus -eq 'PASS' -and -not $agree) {
                if ($destination -eq 'RecycleBin') { $destStatus = 'FAIL'; $problems += "an item that Windows would delete for good was claimed as recycled ($($expected.Why))" }
                else { $destStatus = 'WARN'; $problems += "DevSweep was more careful than Windows' rules require ($($expected.Why)); that is safe, but the Recycle Bin was not used. DevSweep's own reason: $([string]$moved.note)" }
            }
            Add-Result 'Clean' "clean.dest.$name" $destStatus $msg ($problems -join "`n") $(if (@($outside.Content).Count -gt 0) { 'clean-outside' } else { 'clean-destination' })
            # a move frees nothing: the item is still on the drive
            $delta = $freeAfter - $freeBefore
            $tol = Get-SizeTolerance $bytes 0
            $moveStatus = if ([Math]::Abs($delta) -le [Math]::Max([long]$tol, [long]10485760)) { 'PASS' } else { 'WARN' }
            Add-Result 'Clean' "clean.move-free.$name" $moveStatus ("${name}: free space changed by {0} after the move (a move to the Recycle Bin or quarantine frees nothing until it is emptied)." -f (Format-Bytes $delta)) $(if ($moveStatus -eq 'WARN') { 'Free space is noisy; other programs write too. Repeat if this looks wrong.' } else { '' }) 'clean-freed'
            $rows[$name] = [ordered]@{ destination = $destination; expected = $expected.Destination; itemBytes = $bytes; freeDeltaAfterMove = $delta; pathLength = $length }
            # restore, byte for byte
            $historyId = [string]$json.historyId
            $rs = Invoke-VerifyClean $Cli @('restore', $historyId, '--allow-root', $root, '--history', $history)
            if (-not ($rs.Json -and $rs.Json.succeeded)) {
                Add-Result 'Clean' "clean.restore.$name" 'FAIL' "${name}: the restore failed." ($rs.Process.StdOut + $rs.Process.StdErr) 'clean-restore'
                continue
            }
            $after = [DsvNative]::Manifest($path, 64MB)
            $cmp = Compare-Manifests $before $after
            $restoreProblems = @($cmp.Content)
            if ($case.Name -eq 'mixed') {
                $a = [DsvNative]::FileIdentity((Join-Path $path 'hard\a.bin')); $b = [DsvNative]::FileIdentity((Join-Path $path 'hard\b.bin'))
                if (-not $a -or -not $b -or ($a.Split(':')[0..2] -join ':') -ne ($b.Split(':')[0..2] -join ':') -or $a.Split(':')[3] -ne '2') { $restoreProblems += 'the hard link pair is no longer one file with two names' }
                $link = Get-Item -LiteralPath (Join-Path $path 'link-to-decoy') -Force -ErrorAction SilentlyContinue
                if (-not $link -or -not ($link.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $restoreProblems += 'the junction was not restored as a junction' }
                elseif (@($link.Target)[0] -and ((@($link.Target)[0] -replace '^\\\?\?\\', '').TrimEnd('\') -ine $decoy.TrimEnd('\'))) { $restoreProblems += ('the junction now points to ' + @($link.Target)[0]) }
            }
            if ($case.Sparse) {
                $len = (New-Object System.IO.FileInfo ([DsvNative]::Ext((Join-Path $path 'huge.sparse')))).Length
                if ($len -ne $bigLogical) { $restoreProblems += "the sparse file's length is $len, expected $bigLogical" }
            }
            $decoyAfter = Compare-Manifests $decoyBefore ([DsvNative]::Manifest($decoy, 64MB))
            $restoreProblems += @($decoyAfter.Content | ForEach-Object { 'OUTSIDE THE ITEM: ' + $_ })
            $rStatus = 'PASS'
            if ($restoreProblems.Count -gt 0) { $rStatus = 'FAIL' } elseif (@($cmp.Attributes).Count -gt 0) { $rStatus = 'WARN' }
            Add-Result 'Clean' "clean.restore.$name" $rStatus ("{0}: restored from {1}; {2} entries compared by content and size{3}." -f $name, $destination, @($before).Count, $(if ($case.Sparse) { ' (the sparse file by length only)' } else { ' (files up to 64 MB by SHA-256)' })) ((@($restoreProblems) + @($cmp.Attributes)) -join "`n") 'clean-restore'
        }

        # ---------- freed space: delete for good, repeated, median ----------
        $expectedList = @(); $actualList = @(); $reconList = @(); $firstOutside = @()
        for ($n = 1; $n -le $CleanRepeats; $n++) {
            $path = Join-Path $items ('delete-' + $n)
            New-MixedItem $path $true
            $totals = [DsvNative]::Measure([string[]]@($path), 120)
            $freeBefore = Get-FreeBytes $root
            $run = Invoke-VerifyClean $Cli @('run', $path, '--allow-root', $root, '--history', $history, '--delete')
            Start-Sleep -Milliseconds 1000
            $freeAfter = Get-FreeBytes $root
            if (-not ($run.Json -and $run.Json.succeeded)) { Add-Result 'Clean' "clean.delete.$n" 'WARN' "Delete run $n did not succeed: $($run.Process.StdOut)$($run.Process.StdErr)" '' 'clean-freed'; continue }
            $expectedList += [double]$totals.Freeable
            $actualList += [double]($freeAfter - $freeBefore)
            if ($run.Json.entry.reconciliation) { $reconList += [double]$run.Json.entry.reconciliation.freedBytes }
            $still = [DsvNative]::Exists($path)
            if ($still) { Add-Result 'Clean' "clean.delete.$n" 'FAIL' "Delete run ${n}: the item is still there." '' 'clean-destination' }
            $firstOutside += @((Compare-Manifests $decoyBefore ([DsvNative]::Manifest($decoy, 64MB))).Content)
        }
        if ($actualList.Count -gt 0) {
            $medExpected = [long](Get-Median $expectedList)
            $medActual = [long](Get-Median $actualList)
            $cmp = Compare-Size $medExpected $medActual
            $spread = ([double](($actualList | Measure-Object -Maximum).Maximum - ($actualList | Measure-Object -Minimum).Minimum))
            $status = 'PASS'; $detail = ''
            if (-not $cmp.Within) {
                if ($spread -gt ($cmp.Tolerance / 2)) { $status = 'WARN'; $detail = ('The {0} runs disagree with each other by {1}: free space on this drive was changing for other reasons, so there is no clean verdict. Run again on a quieter machine.' -f $actualList.Count, (Format-Bytes $spread)) }
                else { $status = 'FAIL'; $detail = ('Expected to free {0}, the drive got back {1} (median of {2} runs; allowed difference {3}).' -f (Format-Bytes $medExpected), (Format-Bytes $medActual), $actualList.Count, (Format-Bytes $cmp.Tolerance)) }
            }
            if (@($firstOutside).Count -gt 0) { Add-Result 'Clean' 'clean.delete.outside' 'FAIL' 'A permanent delete changed something outside the item (the junction target).' (@($firstOutside) -join "`n") 'clean-outside' }
            else { Add-Result 'Clean' 'clean.delete.outside' 'PASS' 'Permanent deletes of an item containing a junction left the junction''s target untouched.' }
            Add-Result 'Clean' 'clean.freed' $status ("Freed space after deleting for good: expected {0}, the drive got back {1} (median of {2} runs: {3})." -f (Format-Bytes $medExpected), (Format-Bytes $medActual), $actualList.Count, ((@($actualList) | ForEach-Object { Format-Bytes $_ }) -join ', ')) $detail 'clean-freed'
            $rows['deleteForGood'] = [ordered]@{ expectedFreedBytes = $medExpected; actualFreedBytes = $medActual; runsActual = @($actualList); runsExpected = @($expectedList); devsweepReconciliationFreed = @($reconList); toleranceBytes = $cmp.Tolerance }
        }
        # The decoy must also be intact at the very end.
        $decoyEnd = Compare-Manifests $decoyBefore ([DsvNative]::Manifest($decoy, 64MB))
        if (@($decoyEnd.Content).Count -eq 0) { Add-Result 'Clean' 'clean.decoy' 'PASS' 'The decoy folder behind the junction is byte-identical at the end of the test.' }
        else { Add-Result 'Clean' 'clean.decoy' 'FAIL' 'The decoy folder behind the junction changed.' (@($decoyEnd.Content) -join "`n") 'clean-outside' }
    }
    finally {
        Restore-TestRecycleQuota $quotaState
        try { Assert-OwnFixture $root; [DsvNative]::DeleteTree($root) } catch { Add-Result 'Clean' 'clean.cleanup' 'WARN' ("The fixture folder could not be removed completely: $root ($($_.Exception.Message))") '' 'kit' }
    }
    return $rows
}

# ============================================================================================
# Self-test: the comparison logic on made-up data, plus the native helpers on a temporary folder
# ============================================================================================
function Invoke-SelfTest {
    $script:SelfFailures = 0
    $script:SelfCount = 0
    function Assert-That([string] $Name, $Condition) {
        $script:SelfCount++
        if ($Condition) { Write-Host "  ok   $Name" -ForegroundColor Green }
        else { $script:SelfFailures++; Write-Host "  FAIL $Name" -ForegroundColor Red }
    }
    Write-Step 'Self-test: comparison logic on synthetic data'
    # sizes: within 1% or 10 MB, whichever is larger
    Assert-That 'tolerance is 10 MB for small items' ((Get-SizeTolerance 100MB 100MB) -eq 10485760)
    Assert-That 'tolerance is 1% for 10 GB' ((Get-SizeTolerance 10GB 10GB) -eq 107374182)
    Assert-That '1000 MB vs 1005 MB is within' ((Compare-Size 1000MB 1005MB).Within)
    Assert-That '1000 MB vs 1030 MB is not within' (-not (Compare-Size 1000MB 1030MB).Within)
    Assert-That '10 GB vs 10 GB + 90 MB is within (1%)' ((Compare-Size 10GB (10GB + 90MB)).Within)
    Assert-That 'a hard link counted twice (2 GB vs 1 GB) is not within' (-not (Compare-Size 2GB 1GB).Within)
    # names
    Assert-That 'name key drops version and architecture' ((Get-NameKey 'Postman x64 11.2.0') -eq 'postman')
    Assert-That 'name key joins words' ((Get-NameKey 'GitHub Desktop') -eq 'githubdesktop')
    Assert-That 'Postman matches "Postman x64 11.2.0"' (Test-NameMatch 'Postman' 'Postman x64 11.2.0')
    Assert-That 'Slack matches the Store package name' (Test-NameMatch 'Slack' 'SlackTechnologies.Slack')
    Assert-That 'Discord does not match Dropbox' (-not (Test-NameMatch 'Discord' 'Dropbox'))
    Assert-That '"Docker" does not make "Docker Desktop" installed' (-not (Test-NameMatch 'Docker Desktop' 'Docker'))
    Assert-That 'a three-letter name needs an exact match' (-not (Test-NameMatch 'Zed' 'Zedboard'))
    # false leftovers
    $independent = @(
        [pscustomobject]@{ Source = 'installed programs, this user (64-bit view)'; Name = 'Postman'; Strength = 'Strong'; Detail = '' },
        [pscustomobject]@{ Source = 'Program Files folders'; Name = 'Insomnia'; Strength = 'Weak'; Detail = '' })
    $findings = @(
        [pscustomobject]@{ title = 'Postman'; facts = [pscustomobject]@{ app = 'Postman' }; risk = 'Leftover' },
        [pscustomobject]@{ title = 'Insomnia'; facts = [pscustomobject]@{ app = 'Insomnia' }; risk = 'Leftover' },
        [pscustomobject]@{ title = 'Slack'; facts = [pscustomobject]@{ app = 'Slack' }; risk = 'Leftover' })
    $fl = @(Find-FalseLeftover -Findings $findings -Independent $independent)
    Assert-That 'installed app is a FALSE LEFTOVER (strong evidence)' ($fl[0].Strong -eq 1)
    Assert-That 'a Program Files folder alone is weak evidence' ($fl[1].Strong -eq 0 -and $fl[1].Weak -eq 1)
    Assert-That 'an app nobody lists is a true leftover' ($fl[2].Strong -eq 0 -and $fl[2].Weak -eq 0)
    Assert-That 'a finding with facts.app is a leftover' (Test-IsLeftoverFinding $findings[0])
    Assert-That 'a cache finding is not a leftover' (-not (Test-IsLeftoverFinding ([pscustomobject]@{ title = 'npm cache'; risk = 'Rebuilds' })))
    # ports
    $c1 = Compare-Listeners -DevSweep @('100:80', '200:443') -Independent @('100:80', '200:443')
    Assert-That 'equal listener lists have no differences' (@($c1.MissingFromDevSweep).Count -eq 0 -and @($c1.OwnerDiffers).Count -eq 0 -and @($c1.NotConfirmed).Count -eq 0)
    $c2 = Compare-Listeners -DevSweep @('100:80') -Independent @('100:80', '300:22')
    Assert-That 'a socket Windows has and DevSweep lacks is reported' (@($c2.MissingFromDevSweep) -contains '300:22')
    $c3 = Compare-Listeners -DevSweep @('100:80') -Independent @('999:80')
    Assert-That 'same port with another owner is "owner differs", not missing' (@($c3.OwnerDiffers).Count -eq 1 -and @($c3.MissingFromDevSweep).Count -eq 0)
    $c4 = Compare-Listeners -DevSweep @('100:80', '400:8080') -Independent @('100:80')
    Assert-That 'a socket DevSweep has and Windows lacks is "not confirmed"' (@($c4.NotConfirmed) -contains '400:8080')
    # PATH and aliases
    $p1 = Compare-PathLists -DevSweepPaths @('C:\Tools\node.exe', 'D:\Other\node.exe') -IndependentPaths @('c:\tools\node.exe', 'D:\Other\node.exe')
    Assert-That 'path lists compare ignoring case, same first match' (@($p1.Missing).Count -eq 0 -and $p1.FirstMatches)
    $p2 = Compare-PathLists -DevSweepPaths @('C:\Tools\node.exe') -IndependentPaths @('C:\Tools\node.exe', 'E:\Extra\node.exe')
    Assert-That 'an extra independent hit is reported' (@($p2.Missing).Count -eq 1)
    $p3 = Compare-PathLists -DevSweepPaths @('E:\x\node.exe', 'C:\Tools\node.exe') -IndependentPaths @('C:\Tools\node.exe', 'E:\x\node.exe')
    Assert-That 'a different first match is reported' (-not $p3.FirstMatches)
    Assert-That 'WindowsApps python.exe is a Store alias' (Test-StoreAliasPath 'C:\Users\a\AppData\Local\Microsoft\WindowsApps\python.exe' 'C:\Users\a\AppData\Local')
    Assert-That 'a real package under Program Files is not an alias' (-not (Test-StoreAliasPath 'C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.12_x64\python.exe' 'C:\Users\a\AppData\Local'))
    Assert-That 'a normal install is not an alias' (-not (Test-StoreAliasPath 'C:\Python312\python.exe' 'C:\Users\a\AppData\Local'))
    # where Windows would put a removed item
    Assert-That 'small item on a fixed drive with a limit goes to the Recycle Bin' ((Get-ExpectedDestination 'Fixed' 100 1GB $false 10MB).Destination -eq 'RecycleBin')
    Assert-That 'item bigger than the limit goes to quarantine' ((Get-ExpectedDestination 'Fixed' 100 1GB $false 2GB).Destination -eq 'Quarantine')
    Assert-That 'unknown limit goes to quarantine' ((Get-ExpectedDestination 'Fixed' 100 $null $false 10MB).Destination -eq 'Quarantine')
    Assert-That 'path over 259 characters goes to quarantine' ((Get-ExpectedDestination 'Fixed' 300 1GB $false 10MB).Destination -eq 'Quarantine')
    Assert-That 'bin turned off goes to quarantine' ((Get-ExpectedDestination 'Fixed' 100 1GB $true 10MB).Destination -eq 'Quarantine')
    Assert-That 'removable drive goes to quarantine' ((Get-ExpectedDestination 'Removable' 100 1GB $false 10MB).Destination -eq 'Quarantine')
    # $I records
    $path = 'C:\Temp\x'
    $bytes = New-Object byte[] (28 + 2 * ($path.Length + 1))
    [BitConverter]::GetBytes([int64]2).CopyTo($bytes, 0); [BitConverter]::GetBytes([int64]1234).CopyTo($bytes, 8); [BitConverter]::GetBytes([int]($path.Length + 1)).CopyTo($bytes, 24)
    [Text.Encoding]::Unicode.GetBytes($path).CopyTo($bytes, 28)
    $info = Read-RecycleInfo $bytes
    Assert-That 'a $I record v2 is read' ($info -and $info.OriginalPath -eq $path -and $info.Size -eq 1234)
    # redaction and summary
    Assert-That 'user name is replaced in a path' ((Protect-Text 'C:\Users\alice\AppData\x' @('alice')) -eq 'C:\Users\<user>\AppData\x')
    Assert-That 'a word that only contains the name is kept' ((Protect-Text 'C:\Users\malice\x' @('alice')) -like '*malice*')
    Assert-That 'OneDrive organisation is replaced' ((Protect-Text 'C:\Users\x\OneDrive - Contoso Ltd\Documents' @()) -like '*OneDrive - <organisation>\Documents')
    Assert-That '-Off keeps the text' ((Protect-Text 'C:\Users\alice\x' @('alice') -Off) -eq 'C:\Users\alice\x')
    $ok = @([pscustomobject]@{ Status = 'PASS' }, [pscustomobject]@{ Status = 'INFO' })
    Assert-That 'overall PASS' ((Get-Overall $ok) -eq 'PASS')
    Assert-That 'overall WARN' ((Get-Overall ($ok + [pscustomobject]@{ Status = 'WARN' })) -eq 'WARN')
    Assert-That 'overall FAIL wins' ((Get-Overall ($ok + [pscustomobject]@{ Status = 'WARN' } + [pscustomobject]@{ Status = 'FAIL' })) -eq 'FAIL')
    Assert-That 'median of an even list' ((Get-Median @(1, 2, 3, 10)) -eq 2.5)
    Assert-That 'argument quoting keeps spaces together' ((ConvertTo-ArgString @('a b', 'c')) -eq '"a b" c')
    $mf = Compare-Manifests @("a`tF`t3`tabc`t0", "b`tF`t3`tdef`t0") @("a`tF`t3`tabc`t0", "b`tF`t3`tXXX`t0")
    Assert-That 'manifest comparison finds a changed file' (@($mf.Content).Count -eq 1)
    $mf2 = Compare-Manifests @("a`tF`t3`tabc`t0") @("a`tF`t3`tabc`t1")
    Assert-That 'manifest comparison reports attributes separately' (@($mf2.Content).Count -eq 0 -and @($mf2.Attributes).Count -eq 1)

    if ($script:IsWindowsHost) {
        Write-Step 'Self-test: native helpers on a temporary folder'
        Initialize-Native
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('devsweep-verify-selftest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            $tree = Join-Path $tmp 'tree'; $decoy = Join-Path $tmp 'decoy'
            New-Item -ItemType Directory -Path $tree, $decoy | Out-Null
            New-RandomFile (Join-Path $tree 'one.bin') (1MB)
            New-RandomFile (Join-Path $decoy 'big.bin') (8MB)
            $plain = [DsvNative]::Measure([string[]]@($tree), 30)
            Assert-That 'a 1 MB file measures about 1 MB' ($plain.Bytes -ge 1MB -and $plain.Bytes -le (1MB + 64KB) -and $plain.Complete)
            New-Item -ItemType HardLink -Path (Join-Path $tree 'two.bin') -Target (Join-Path $tree 'one.bin') | Out-Null
            $linked = [DsvNative]::Measure([string[]]@($tree), 30)
            Assert-That 'a hard link is counted once' ($linked.Bytes -eq $plain.Bytes -and $linked.HardLinkDuplicates -eq 1)
            Assert-That 'both names inside the tree: freeable is the file' ($linked.Freeable -eq $plain.Bytes -and $linked.SharedLinkBytes -eq 0)
            $outsideLink = Join-Path $tmp 'outside-link.bin'
            New-Item -ItemType HardLink -Path $outsideLink -Target (Join-Path $tree 'one.bin') | Out-Null
            $shared = [DsvNative]::Measure([string[]]@($tree), 30)
            Assert-That 'a name outside the tree: shared, not freeable' ($shared.Freeable -lt $shared.Bytes -and $shared.SharedLinkBytes -gt 0)
            New-Item -ItemType Junction -Path (Join-Path $tree 'junction') -Target $decoy | Out-Null
            $withJunction = [DsvNative]::Measure([string[]]@($tree), 30)
            Assert-That 'a junction is not followed' ($withJunction.Bytes -eq $linked.Bytes -and $withJunction.Reparse -eq 1)
            $compressible = Join-Path $tree 'text.txt'
            [IO.File]::WriteAllText($compressible, ('compressible ' * 2000 + "`r`n") * 800)
            $beforeCompress = [DsvNative]::Measure([string[]]@($tree), 30)
            $c = Invoke-Process -FilePath (Join-Path $env:SystemRoot 'System32\compact.exe') -ArgumentList @('/c', '/q', $compressible) -TimeoutSeconds 60
            $isCompressed = (([DsvNative]::Attributes($compressible) -band 0x800) -ne 0)
            $afterCompress = [DsvNative]::Measure([string[]]@($tree), 30)
            if ($isCompressed) { Assert-That 'a compressed file takes less than its length' ($afterCompress.Bytes -lt $beforeCompress.Bytes) } else { Write-Host '  skip compressed-file check: this file system has no compression' -ForegroundColor DarkYellow }
            $long = Join-Path $tmp (('d' * 100) + '\' + ('e' * 100) + '\' + ('f' * 100))
            [DsvNative]::EnsureDir($long)
            [DsvNative]::WriteBytes((Join-Path $long 'x.bin'), (New-Object byte[] 5000))
            Assert-That 'a path over 260 characters can be created and measured' ($long.Length -gt 260 -and ([DsvNative]::Measure([string[]]@($long), 30)).Files -eq 1)
            $m1 = [DsvNative]::Manifest($tmp, 64MB)
            Assert-That 'manifest lists files, folders and the junction' ((@($m1 | Where-Object { $_ -match "^tree\\junction`tL`t" }).Count -eq 1) -and (@($m1 | Where-Object { $_ -match "`tF`t" }).Count -ge 4))
            Assert-That 'sha256 of a known file' (([DsvNative]::Sha256($compressible)).Length -eq 64)
            $sparsePath = Join-Path $tmp 'sparse.bin'
            $sparseWorked = $true
            try { [DsvNative]::MakeSparse($sparsePath, 64MB) } catch { $sparseWorked = $false }
            if ($sparseWorked) {
                $sp = [DsvNative]::Measure([string[]]@($sparsePath), 30)
                Assert-That 'a sparse file of 64 MB takes (almost) no space' ($sp.LogicalBytes -eq 64MB -and $sp.Bytes -lt 1MB)
            }
            else { Write-Host '  skip sparse-file check: this file system has no sparse files' -ForegroundColor DarkYellow }
            $decoyAfter = [DsvNative]::Measure([string[]]@($decoy), 30)
            [DsvNative]::DeleteTree($tmp)
            Assert-That 'the fixture is removed, junction removed as a link' (-not (Test-Path -LiteralPath $tmp))
            Assert-That 'DeleteTree refuses a folder that is not a verification fixture' (& { try { [DsvNative]::DeleteTree((Join-Path $env:TEMP 'not-a-fixture-folder')); $false } catch { $true } })
        }
        finally { if (Test-Path -LiteralPath $tmp) { try { [DsvNative]::DeleteTree($tmp) } catch { } } }
    }
    Write-Host ''
    if ($script:SelfFailures -eq 0) { Write-Host ("Self-test passed ({0} checks)." -f $script:SelfCount) -ForegroundColor Green; return 0 }
    Write-Host ("Self-test FAILED: {0} of {1} checks." -f $script:SelfFailures, $script:SelfCount) -ForegroundColor Red
    return 1
}

# ============================================================================================
# Report
# ============================================================================================
function ConvertTo-MdCell {
    param($Value)
    $t = [string]$Value
    return ($t -replace '\|', '\|' -replace "`r?`n", ' ')
}

function New-Report {
    param($Environment, $Sizes, $Leftovers, $RuntimesInfo, $PortsInfo, $CleanInfo, $Overall, [string] $Folder, [hashtable] $Raw, [string[]] $RedactNames)
    $pass = @($script:Results | Where-Object { $_.Status -eq 'PASS' }).Count
    $warn = @($script:Results | Where-Object { $_.Status -eq 'WARN' }).Count
    $fail = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
    $info = @($script:Results | Where-Object { $_.Status -eq 'INFO' }).Count
    $headline = switch ($Overall) {
        'PASS' { 'PASS: everything the kit could check agrees with Windows.' }
        'WARN' { "WARN: nothing is wrong that the kit could prove, but $warn check(s) need a human look." }
        default { "FAIL: $fail check(s) found DevSweep saying something Windows disagrees with. Do not release until these are explained." }
    }
    $md = New-Object 'System.Collections.Generic.List[string]'
    $md.Add('# DevSweep accuracy check')
    $md.Add('')
    $md.Add("**Result: $headline**")
    $md.Add('')
    $md.Add(('| Passed | Warnings | Failed | Information |'))
    $md.Add('|---:|---:|---:|---:|')
    $md.Add("| $pass | $warn | $fail | $info |")
    $md.Add('')
    $md.Add(('Checked {0} with kit {1}{2}. Mode: {3}.' -f $Environment.kit.started, $script:KitVersion, $(if ($Environment.devsweep) { ', DevSweep ' + $Environment.devsweep.productVersion } else { '' }), $(if ($RunCleanTest) { 'read-only checks plus the clean test on a fixture the kit created' } else { 'read-only (nothing was changed)' })))
    $md.Add('')
    $problems = @($script:Results | Where-Object { $_.Status -in 'FAIL', 'WARN' } | Sort-Object { if ($_.Status -eq 'FAIL') { 0 } else { 1 } })
    if ($problems.Count -gt 0) {
        $md.Add('## What needs attention')
        $md.Add('')
        foreach ($p in $problems) {
            $md.Add(('### {0}: {1}' -f $p.Status, (ConvertTo-MdCell $p.Title)))
            $md.Add('')
            if ($p.Detail) { $md.Add('```'); $md.Add($p.Detail); $md.Add('```'); $md.Add('') }
            if ($p.Meaning) { $md.Add('**What it likely means.** ' + $p.Meaning); $md.Add('') }
            if ($p.LookAt) { $md.Add('**Where to look.** ' + $p.LookAt); $md.Add('') }
        }
    }
    else { $md.Add('## What needs attention'); $md.Add(''); $md.Add('Nothing.'); $md.Add('') }
    # Sizes
    $md.Add('## Sizes'); $md.Add('')
    if (@($Sizes).Count -gt 0) {
        $md.Add('Allowed difference: 1% or 10 MB, whichever is larger (PLAN section 1).'); $md.Add('')
        $md.Add('| Finding | DevSweep | Independent | Difference | Allowed | Files | Links counted once | Verdict |')
        $md.Add('|---|---:|---:|---:|---:|---:|---:|---|')
        foreach ($s in $Sizes) { $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5:N0} | {6:N0} | {7} |' -f (ConvertTo-MdCell $s.Finding), (Format-Bytes $s.DevSweepBytes), (Format-Bytes $s.IndependentBytes), (Format-Bytes $s.DifferenceBytes), (Format-Bytes $s.ToleranceBytes), $s.Files, $s.HardLinkDuplicates, $s.Status)) }
    }
    else { $md.Add('No finding with a size to re-measure.') }
    $md.Add('')
    # Leftovers
    $md.Add('## Leftovers and the inventory'); $md.Add('')
    if ($Leftovers -and @($Leftovers.Leftovers).Count -gt 0) {
        $md.Add('| App DevSweep calls a leftover | Verdict | Independent evidence |'); $md.Add('|---|---|---|')
        foreach ($l in $Leftovers.Leftovers) { $md.Add(('| {0} | {1} | {2} |' -f (ConvertTo-MdCell $l.App), $l.Status, (ConvertTo-MdCell $(if ($l.Evidence) { $l.Evidence } else { 'none: not installed' })))) }
        $md.Add('')
    }
    foreach ($r in @($script:Results | Where-Object { $_.Area -eq 'Leftovers' -and $_.Id -notlike 'leftover.*' })) { $md.Add(('- **{0}** {1}' -f $r.Status, (ConvertTo-MdCell $r.Title))) }
    $md.Add('')
    # Runtimes
    $md.Add('## Runtimes and PATH'); $md.Add('')
    foreach ($r in @($script:Results | Where-Object { $_.Area -eq 'Runtimes' })) { $md.Add(('- **{0}** {1}' -f $r.Status, (ConvertTo-MdCell $r.Title))) }
    $md.Add('')
    # Ports
    $md.Add('## Ports'); $md.Add('')
    foreach ($r in @($script:Results | Where-Object { $_.Area -eq 'Ports' })) { $md.Add(('- **{0}** {1}' -f $r.Status, (ConvertTo-MdCell $r.Title))) }
    $md.Add('')
    # Clean
    $md.Add('## Clean test'); $md.Add('')
    if ($RunCleanTest) { foreach ($r in @($script:Results | Where-Object { $_.Area -eq 'Clean' })) { $md.Add(('- **{0}** {1}' -f $r.Status, (ConvertTo-MdCell $r.Title))) } }
    else { $md.Add('Not run (it is opt in: add -RunCleanTest). Nothing on this PC was moved or deleted.') }
    $md.Add('')
    # Environment
    $md.Add('## This PC'); $md.Add('')
    $w = $Environment.windows
    $md.Add(('- Windows: {0}, build {1}.{2}, {3}, {4}' -f $w.product, $w.build, $w.ubr, $w.architecture, $w.displayVersion))
    foreach ($v in @($Environment.volumes)) { $md.Add(('- Drive {0}: {1}, {2}, {3} free of {4}{5}' -f $v.drive, $v.type, $v.fileSystem, (Format-Bytes $v.freeBytes), (Format-Bytes $v.sizeBytes), $(if ($v.devDrive) { ', Dev Drive' } else { '' }))) }
    foreach ($k in $Environment.knownFolders.Keys) { $kf = $Environment.knownFolders[$k]; $md.Add(('- {0}: {1}{2}' -f $k, $kf.path, $(if ($kf.redirected) { " (redirected: $($kf.why))" } else { '' }))) }
    $md.Add(('- Tools: ' + ((@($Environment.tools.Keys) | ForEach-Object { '{0} = {1}' -f $_, $Environment.tools[$_] }) -join ', ')))
    if (@($Environment.ideAndTools).Count -gt 0) { $md.Add('- IDEs and big tools: ' + (@($Environment.ideAndTools) -join ', ')) }
    $md.Add('- Looks like reference profile(s): ' + $(if (@($Environment.looksLikeReferenceProfiles).Count -gt 0) { @($Environment.looksLikeReferenceProfiles) -join '; ' } else { 'none of the three on its own' }))
    $md.Add(('- ReFS present: {0}; Dev Drive present: {1}; OneDrive-redirected known folders: {2}' -f $Environment.refsPresent, $Environment.devDrivePresent, $Environment.oneDriveRedirectedKnownFolders))
    $md.Add('')
    $md.Add('## About this report'); $md.Add('')
    $md.Add('- Everything above was computed on this PC; nothing was uploaded. The folder `raw` holds the exact outputs of DevSweep that were compared.')
    $md.Add($(if ($NoRedact) { '- User names were NOT hidden (-NoRedact).' } else { '- Your user name and OneDrive organisation name are replaced by <user> and <organisation>; folder names inside your profile are still shown. Read the files before you share them.' }))
    $md.Add('- Sharing is optional: copy this folder, or just report.md, to whoever asked for it.')
    $text = ($md -join "`r`n")
    return [pscustomobject]@{ Markdown = (Protect-Text $text $RedactNames -Off:$NoRedact); Pass = $pass; Warn = $warn; Fail = $fail; Info = $info; Headline = $headline }
}

# ============================================================================================
# Main
# ============================================================================================
if ($SelfTest) {
    $code = @(Invoke-SelfTest)[-1]
    exit $code
}

if (-not $script:IsWindowsHost) {
    Write-Host 'This kit checks DevSweep for Windows and only runs on Windows.' -ForegroundColor Red
    exit 2
}

Initialize-Native
$started = Get-Date
Write-Host ''
Write-Host 'DevSweep accuracy check' -ForegroundColor White
Write-Host ('Read-only by default: this will not change, move or delete anything{0}. Nothing leaves this PC.' -f $(if ($RunCleanTest) { ' except a throwaway fixture the kit creates under %TEMP% (-RunCleanTest)' } else { '' }))

$exe = Find-DevSweepCli $DevSweepPath
if (-not $exe) {
    Write-Host ''
    Write-Host 'Could not find devsweep.exe (the command-line program).' -ForegroundColor Red
    Write-Host 'Looked next to this script, in %LOCALAPPDATA%\Programs\DevSweep, on PATH and in the current folder.'
    Write-Host 'Download the devsweep-cli zip from the same release, unzip it, and run again with -DevSweepPath <folder or devsweep.exe>.'
    exit 2
}
Write-Host "Using $exe"

$stamp = $started.ToString('yyyyMMdd-HHmmss')
if (-not $OutputPath) {
    $OutputPath = [Environment]::GetFolderPath('Desktop')
    if (-not $OutputPath -or -not (Test-Path -LiteralPath $OutputPath)) { $OutputPath = [IO.Path]::GetTempPath() }
}
$folder = Join-Path $OutputPath "Verify-DevSweep-$stamp"
try { New-Item -ItemType Directory -Path (Join-Path $folder 'raw') -Force | Out-Null }
catch {
    $folder = Join-Path ([IO.Path]::GetTempPath()) "Verify-DevSweep-$stamp"
    New-Item -ItemType Directory -Path (Join-Path $folder 'raw') -Force | Out-Null
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('devsweep-verify-work-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

$redactNames = @(@($env:USERNAME, $(if ($env:USERPROFILE) { Split-Path $env:USERPROFILE -Leaf })) | Where-Object { $_ } | Select-Object -Unique)
$rawFiles = @{}
function Save-Raw([string] $Name, [string] $Text) {
    $script:rawFiles[$Name] = $Text
    [IO.File]::WriteAllText((Join-Path $script:folder ('raw\' + $Name)), (Protect-Text $Text $script:redactNames -Off:$NoRedact), (New-Object Text.UTF8Encoding($false)))
}

$environmentFacts = $null; $sizes = @(); $leftoverInfo = $null; $runtimeInfo = $null; $portsInfo = $null; $cleanInfo = $null
try {
    Write-Step 'This PC, and independent evidence of what is installed'
    $independent = $null
    try { $independent = Get-IndependentApps } catch { Add-Result 'Leftovers' 'leftovers.evidence' 'WARN' ('Could not collect the independent list of installed programs: ' + $_.Exception.Message) '' 'kit' }
    $environmentFacts = Get-EnvironmentFacts $exe $independent
    Add-Result 'Environment' 'env.windows' 'INFO' ('Windows {0} build {1}.{2}, PowerShell {3}.' -f $environmentFacts.windows.product, $environmentFacts.windows.build, $environmentFacts.windows.ubr, $environmentFacts.windows.powershell)

    function Invoke-Cli([string] $Name, [string[]] $CliArgs) {
        Write-Host ("  running: devsweep " + ($CliArgs -join ' ')) -ForegroundColor DarkGray
        $p = Invoke-Process -FilePath $script:exe -ArgumentList $CliArgs -TimeoutSeconds $CliTimeoutSeconds
        Save-Raw ($Name + '.json') $p.StdOut
        if ($p.StdErr) { Save-Raw ($Name + '.stderr.txt') $p.StdErr }
        $json = $null
        if (-not $p.TimedOut -and $p.ExitCode -eq 0) { try { $json = $p.StdOut | ConvertFrom-Json } catch { $json = $null } }
        if (-not $json) {
            $why = if ($p.TimedOut) { "did not finish in $CliTimeoutSeconds s" } elseif ($p.ExitCode -ne 0) { "exit code $($p.ExitCode)" } else { 'its output is not valid JSON' }
            Add-Result 'DevSweep' "cli.$Name" 'FAIL' ("devsweep {0}: {1}." -f ($CliArgs -join ' '), $why) ("Command: $($p.Command)`n" + (($p.StdErr -split "`r?`n" | Select-Object -First 8) -join "`n")) 'cli'
        }
        else { Add-Result 'DevSweep' "cli.$Name" 'PASS' ("devsweep {0} ran in {1} s and printed valid JSON." -f ($CliArgs -join ' '), $p.Seconds) }
        return $json
    }

    Write-Step 'Running DevSweep (read-only)'
    $scan = Invoke-Cli 'scan' @('scan', '--json', '--exact')
    $inventory = Invoke-Cli 'inventory' @('inventory', '--json')
    $versions = Invoke-Cli 'runtimes' @('runtimes', '--json', '--offline')
    $portsJson = Invoke-Cli 'ports' @('ports', '--json', '--all')
    if ($scan) {
        $bad = @(@($scan.report.checks) | Where-Object { [string]$_.outcome -in 'Failed', 'TimedOut' })
        if ($bad.Count -gt 0) { Add-Result 'DevSweep' 'scan.checks' 'WARN' ("{0} of {1} scan check(s) failed or timed out: {2}" -f $bad.Count, @($scan.report.checks).Count, (($bad | ForEach-Object { $_.id }) -join ', ')) '' 'kit' }
        else { Add-Result 'DevSweep' 'scan.checks' 'PASS' ("The scan ran {0} checks (none failed or timed out) and found {1} item(s), {2}." -f @($scan.report.checks).Count, @($scan.findings).Count, (Format-Bytes ([double]$scan.report.totalBytes))) }
    }

    if ($scan) {
        try { $sizes = Test-Sizes -Scan $scan -Top $TopFindings -TimeLimit $SizeTimeLimitSeconds }
        catch { Add-Result 'Sizes' 'sizes.error' 'WARN' ('The size check stopped: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit' }
    }
    if ($scan -and $inventory -and $independent) {
        try {
            Add-Result 'Leftovers' 'leftovers.evidence' 'INFO' ('Independent evidence: {0} names from {1} Uninstall-registry entries (four views), Store packages, Scoop, Chocolatey, the Start menu and Program Files.' -f @($independent.Apps).Count, $independent.RegistryEntries)
            $leftoverInfo = Test-Leftovers -Scan $scan -InventoryJson $inventory -Independent $independent
        }
        catch { Add-Result 'Leftovers' 'leftovers.error' 'WARN' ('The leftover check stopped: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit' }
    }
    if ($versions) {
        try { $runtimeInfo = Test-Runtimes -Versions $versions -WorkDirectory $work }
        catch { Add-Result 'Runtimes' 'runtimes.error' 'WARN' ('The runtime check stopped: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit' }
    }
    if ($portsJson) {
        try { $portsInfo = Test-Ports -PortsJson $portsJson }
        catch { Add-Result 'Ports' 'ports.error' 'WARN' ('The ports check stopped: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit' }
    }
    if ($RunCleanTest) {
        try { $cleanInfo = Invoke-CleanTest -Cli $exe -WorkDirectory $work }
        catch { Add-Result 'Clean' 'clean.error' 'WARN' ('The clean test stopped: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit' }
    }
}
catch {
    Add-Result 'Kit' 'kit.error' 'WARN' ('The kit stopped early: ' + $_.Exception.Message) ($_.ScriptStackTrace) 'kit'
}
finally {
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

$overall = Get-Overall $script:Results
if (-not $environmentFacts) { $environmentFacts = [ordered]@{ windows = [ordered]@{}; volumes = @(); knownFolders = [ordered]@{}; tools = [ordered]@{}; ideAndTools = @(); looksLikeReferenceProfiles = @(); kit = [ordered]@{ started = $started.ToString('s') } } }
$report = New-Report -Environment $environmentFacts -Sizes $sizes -Leftovers $leftoverInfo -RuntimesInfo $runtimeInfo -PortsInfo $portsInfo -CleanInfo $cleanInfo -Overall $overall -Folder $folder -Raw $rawFiles -RedactNames $redactNames

$json = [ordered]@{
    kit        = [ordered]@{ name = 'Verify-DevSweep'; version = $script:KitVersion; generated = (Get-Date).ToString('s'); runCleanTest = [bool]$RunCleanTest; redacted = (-not $NoRedact) }
    summary    = [ordered]@{ overall = $overall; pass = $report.Pass; warn = $report.Warn; fail = $report.Fail; info = $report.Info; headline = $report.Headline }
    environment = $environmentFacts
    results    = @($script:Results | ForEach-Object { [ordered]@{ area = $_.Area; id = $_.Id; status = $_.Status; title = $_.Title; detail = $_.Detail; meaning = $_.Meaning; lookAt = $_.LookAt } })
    sizes      = @($sizes)
    leftovers  = $leftoverInfo
    runtimes   = $runtimeInfo
    ports      = $portsInfo
    clean      = $cleanInfo
}
$jsonText = Protect-Text ($json | ConvertTo-Json -Depth 8) $redactNames -Off:$NoRedact
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $folder 'report.md'), $report.Markdown, $utf8)
[IO.File]::WriteAllText((Join-Path $folder 'report.json'), $jsonText, $utf8)

Write-Host ''
$color = switch ($overall) { 'PASS' { 'Green' } 'WARN' { 'Yellow' } default { 'Red' } }
Write-Host $report.Headline -ForegroundColor $color
Write-Host ("{0} passed, {1} warnings, {2} failed, {3} information. Took {4:N0} s." -f $report.Pass, $report.Warn, $report.Fail, $report.Info, ((Get-Date) - $started).TotalSeconds)
Write-Host "Report: $folder\report.md"

if (-not $NoPause -and $env:GITHUB_ACTIONS -ne 'true' -and $env:CI -ne 'true' -and [Environment]::UserInteractive) {
    $parentName = ''
    try { $pp = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId; $parentName = (Get-Process -Id $pp -ErrorAction SilentlyContinue).ProcessName } catch { }
    if ($parentName -eq 'explorer') { [void](Read-Host 'Press Enter to close this window') }
}
if ($overall -eq 'FAIL') { exit 1 }
exit 0
