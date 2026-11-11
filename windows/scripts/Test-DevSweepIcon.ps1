<#
.SYNOPSIS
  Helpers (dot-source this file) that prove an exe really carries DevSweep's logo, without looking at it.

.DESCRIPTION
  Get-ExeIcons       the icon images embedded in an exe's resources (the first RT_GROUP_ICON and the RT_ICON
                     entries it lists), as objects with Width, Height and Data.
  Get-IcoFrames      the same for an .ico file.
  Assert-ExeHasLogo  throws unless the exe has an embedded icon with exactly the sizes of the .ico, each image the
                     same length as the .ico's frame (and says whether the bytes are identical).
  Assert-ShortcutIcon  throws unless a .lnk shows the exe's own embedded icon (IconLocation "<exe>,0").
#>

if (-not ('DevSweepPe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public sealed class DevSweepIconFrame {
    public int Width;
    public int Height;
    public byte[] Data;
}

public static class DevSweepPe {
    delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string file, IntPtr zero, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNames callback, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);

    const uint LoadLibraryAsDataFile = 0x2;
    static readonly IntPtr RtIcon = (IntPtr)3;
    static readonly IntPtr RtGroupIcon = (IntPtr)14;

    static byte[] Read(IntPtr module, IntPtr name, IntPtr type) {
        IntPtr found = FindResource(module, name, type);
        if (found == IntPtr.Zero) return null;
        IntPtr loaded = LoadResource(module, found);
        IntPtr pointer = LockResource(loaded);
        uint size = SizeofResource(module, found);
        if (pointer == IntPtr.Zero || size == 0) return null;
        byte[] bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, (int)size);
        return bytes;
    }

    public static List<DevSweepIconFrame> Icons(string path) {
        var frames = new List<DevSweepIconFrame>();
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, LoadLibraryAsDataFile);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot load " + path);
        try {
            IntPtr groupName = IntPtr.Zero;
            EnumResourceNames(module, RtGroupIcon, delegate (IntPtr m, IntPtr t, IntPtr n, IntPtr p) { groupName = n; return false; }, IntPtr.Zero);
            if (groupName == IntPtr.Zero) return frames;
            byte[] group = Read(module, groupName, RtGroupIcon);
            if (group == null || group.Length < 6) return frames;
            int count = BitConverter.ToUInt16(group, 4);
            for (int i = 0; i < count; i++) {
                int at = 6 + (14 * i);
                int id = BitConverter.ToUInt16(group, at + 12);
                byte[] data = Read(module, (IntPtr)id, RtIcon);
                if (data == null) continue;
                frames.Add(new DevSweepIconFrame { Width = group[at] == 0 ? 256 : group[at], Height = group[at + 1] == 0 ? 256 : group[at + 1], Data = data });
            }
        }
        finally { FreeLibrary(module); }
        return frames;
    }
}
'@
}

function Get-ExeIcons([string] $Exe) {
    , @([DevSweepPe]::Icons((Resolve-Path -LiteralPath $Exe).Path))
}

function Get-IcoFrames([string] $Ico) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Ico).Path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $frames = @()
    for ($i = 0; $i -lt $count; $i++) {
        $at = 6 + 16 * $i
        $size = [BitConverter]::ToUInt32($bytes, $at + 8)
        $offset = [BitConverter]::ToUInt32($bytes, $at + 12)
        $width = if ($bytes[$at] -eq 0) { 256 } else { [int] $bytes[$at] }
        $height = if ($bytes[$at + 1] -eq 0) { 256 } else { [int] $bytes[$at + 1] }
        $frames += [pscustomobject]@{ Width = $width; Height = $height; Data = [byte[]] $bytes[$offset..($offset + $size - 1)] }
    }
    , $frames
}

function Get-BytesHash([byte[]] $Bytes) {
    [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($Bytes)).Replace('-', '').Substring(0, 12)
}

function Assert-ExeHasLogo([string] $Exe, [string] $Ico) {
    $expected = Get-IcoFrames $Ico
    $actual = Get-ExeIcons $Exe
    $name = Split-Path $Exe -Leaf
    if ($actual.Count -eq 0) { throw "$name has no embedded icon (it would show the generic exe icon)" }
    $want = ($expected | ForEach-Object { "$($_.Width)x$($_.Height)" } | Sort-Object) -join ','
    $have = ($actual | ForEach-Object { "$($_.Width)x$($_.Height)" } | Sort-Object) -join ','
    "  ${name}: embedded icon sizes $have"
    if ($want -ne $have) { throw "$name embeds icon sizes [$have], the logo .ico has [$want]" }
    $identical = 0
    foreach ($frame in $expected) {
        $match = $actual | Where-Object { $_.Width -eq $frame.Width -and $_.Height -eq $frame.Height } | Select-Object -First 1
        if ($match.Data.Length -ne $frame.Data.Length) { throw "$name's $($frame.Width)x$($frame.Height) icon is $($match.Data.Length) bytes, the logo's is $($frame.Data.Length)" }
        if ((Get-BytesHash $match.Data) -eq (Get-BytesHash $frame.Data)) { $identical++ }
    }
    "  ${name}: $($expected.Count) icon sizes match the logo .ico; $identical of them byte-for-byte identical"
}

function Assert-ShortcutIcon([string] $Lnk, [string] $Exe) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Lnk)
    "  $(Split-Path $Lnk -Leaf): target $($link.TargetPath), icon $($link.IconLocation)"
    if ($link.TargetPath -ine $Exe) { throw "$Lnk does not point at $Exe" }
    if ($link.IconLocation -notmatch ('^' + [regex]::Escape($Exe) + ',\s*0$')) { throw "$Lnk does not use the icon embedded in $Exe (IconLocation is '$($link.IconLocation)')" }
}
