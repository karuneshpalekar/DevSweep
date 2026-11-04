using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace DevSweep.Core.Platform;

/// <summary>
/// Opens metadata handles and reads information classes from them. Every open
/// shares read, write and delete (so DevSweep never blocks another program),
/// opens a reparse point as itself and never recalls cloud data.
/// </summary>
internal static unsafe class NativeFile
{
    public const FILE_SHARE_MODE ShareAll =
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    /// <summary>Open the entry itself: folders too, links as links, cloud data never recalled.</summary>
    public const FILE_FLAGS_AND_ATTRIBUTES NoFollow =
        FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS
        | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT
        | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_NO_RECALL;

    public const uint ReadAttributes = (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES;
    public const uint ListDirectory = (uint)(FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | FILE_ACCESS_RIGHTS.SYNCHRONIZE);
    public const uint DeleteAccess = (uint)(FILE_ACCESS_RIGHTS.DELETE | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | FILE_ACCESS_RIGHTS.SYNCHRONIZE);

    /// <summary>Opens <paramref name="extendedPath"/>; null and the Win32 error on failure.</summary>
    public static SafeFileHandle? Open(string extendedPath, uint access, FILE_FLAGS_AND_ATTRIBUTES flags, out int error)
    {
        HANDLE handle;
        fixed (char* p = extendedPath)
        {
            handle = PInvoke.CreateFile(p, access, ShareAll, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, flags, HANDLE.Null);
        }

        if (handle == HANDLE.INVALID_HANDLE_VALUE)
        {
            error = Marshal.GetLastPInvokeError();
            return null;
        }

        error = 0;
        return new SafeFileHandle((nint)handle.Value, ownsHandle: true);
    }

    /// <summary>GetFileInformationByHandleEx on a safe handle; false with the Win32 error.</summary>
    public static bool Query(SafeFileHandle handle, FILE_INFO_BY_HANDLE_CLASS infoClass, void* buffer, uint size, out int error)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), infoClass, buffer, size))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>SetFileInformationByHandle on a safe handle; false with the Win32 error.</summary>
    public static bool Set(SafeFileHandle handle, FILE_INFO_BY_HANDLE_CLASS infoClass, void* buffer, uint size, out int error)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), infoClass, buffer, size))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Renames the object behind <paramref name="handle"/> (opened with DELETE access)
    /// to <paramref name="extendedTarget"/>, never replacing anything. A rename
    /// never copies: across volumes it fails with ERROR_NOT_SAME_DEVICE, which is
    /// what MoveFileExW without MOVEFILE_COPY_ALLOWED does, but on the handle that was checked.
    /// </summary>
    public static bool RenameByHandle(SafeFileHandle handle, string extendedTarget, out int error)
    {
        int nameBytes = extendedTarget.Length * sizeof(char);
        int size = sizeof(FILE_RENAME_INFO) + nameBytes;
        byte* buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            var info = (FILE_RENAME_INFO*)buffer;
            info->Flags = 0; // ReplaceIfExists = FALSE
            info->RootDirectory = HANDLE.Null;
            info->FileNameLength = (uint)nameBytes;
            extendedTarget.AsSpan().CopyTo(new Span<char>(&info->FileName, extendedTarget.Length));
            return Set(handle, FILE_INFO_BY_HANDLE_CLASS.FileRenameInfo, buffer, (uint)size, out error);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>The attributes and reparse tag of the object the handle refers to, read from the handle itself.</summary>
    public static bool TryGetAttributeTag(SafeFileHandle handle, out Win32FileAttributes attributes, out uint tag)
    {
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO), out _))
        {
            attributes = Win32FileAttributes.None;
            tag = 0;
            return false;
        }

        attributes = (Win32FileAttributes)info.FileAttributes;
        tag = (attributes & Win32FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0;
        return true;
    }

    /// <summary>Volume serial and 128-bit file ID (FILE_ID_INFO).</summary>
    public static bool TryGetIdentity(SafeFileHandle handle, out FileIdentity identity, out int error)
    {
        FILE_ID_INFO info;
        if (!Query(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, &info, (uint)sizeof(FILE_ID_INFO), out error))
        {
            identity = default;
            return false;
        }

        identity = new FileIdentity(info.VolumeSerialNumber, ToUInt128(&info.FileId));
        return true;
    }

    public static UInt128 ToUInt128(FILE_ID_128* id)
    {
        ulong* words = (ulong*)id;
        return new UInt128(words[1], words[0]);
    }
}
