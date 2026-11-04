using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace DevSweep.Core.Platform.Recycling;

/// <summary>
/// Sends items to the Recycle Bin only when they will really land there.
/// Before acting it checks for junctions on the path, the drive type, the
/// bin's on/off switch and its size limit; the Recycle Bin deletes oversized
/// items permanently and silently when no UI is shown.
/// </summary>
public sealed class RecycleBin(IRecycleBinQuotaSource quotaSource)
{
    // FOF_NO_UI (silent, no confirmation, no error UI, no mkdir confirmation),
    // keep undo (recycle), and treat reparse points as objects, never containers.
    private const FILEOPERATION_FLAGS DeleteFlags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOF_NO_UI
        | FILEOPERATION_FLAGS.FOF_NORECURSEREPARSE;

    public RecycleBin()
        : this(new RegistryRecycleBinQuotaSource())
    {
    }

    /// <summary>Works out what recycling <paramref name="path"/> would do, without changing anything.</summary>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    public RecycleAssessment Assess(string path)
    {
        string full = LongPath.Normalize(path);
        string? ancestor = ReparseGuard.FindReparseAncestor(full);
        VolumeInfo volume = Volumes.ForPath(full);
        RecycleQuota quota = quotaSource.GetQuota(volume);

        // Measure the item itself; a junction or link measures as the link, not its target.
        WalkResult size = DirectoryWalker.Measure(full);
        long itemBytes = Math.Max(size.Bytes, size.LogicalBytes);

        var inputs = new RecycleInputs(volume.Kind, full.Length, ancestor, size.Complete && size.Unreadable == 0, itemBytes, quota);
        (RecycleVerdict verdict, string reason) = RecyclePolicy.Evaluate(inputs);
        return new RecycleAssessment(full, verdict, reason, itemBytes, quota.MaxBytes, volume.Kind);
    }

    /// <summary>
    /// Recycles <paramref name="path"/> when the assessment says it will be restorable.
    /// An <see cref="RecycleVerdict.Unknown"/> verdict is acted on only when
    /// <paramref name="allowUnverified"/> is true; permanent and not-allowed verdicts never are.
    /// </summary>
    public RecycleOutcome Recycle(string path, bool allowUnverified = false)
        => Recycle(Assess(path), allowUnverified);

    /// <summary>Recycles the item of an assessment made just before, on the same terms as <see cref="Recycle(string, bool)"/>.</summary>
    public static RecycleOutcome Recycle(RecycleAssessment assessment, bool allowUnverified = false)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        bool allowed = assessment.Verdict == RecycleVerdict.Recyclable
            || (assessment.Verdict == RecycleVerdict.Unknown && allowUnverified);
        if (!allowed)
        {
            return new RecycleOutcome(false, assessment, assessment.Reason);
        }

        string? error = ShellDelete(assessment.Path);
        if (error is null && Exists(assessment.Path))
        {
            error = "The shell reported success but the item is still there.";
        }

        return new RecycleOutcome(error is null, assessment, error);
    }

    /// <summary>What is in the Recycle Bin of the drive holding <paramref name="path"/>.</summary>
    /// <exception cref="Win32Exception">The bin cannot be queried.</exception>
    public static unsafe RecycleBinUsage QueryUsage(string path)
    {
        string root = Volumes.VolumeRoot(LongPath.Normalize(path));
        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        fixed (char* p = root)
        {
            var result = PInvoke.SHQueryRecycleBin(p, &info);
            if (result.Failed)
            {
                throw new Win32Exception(result.Value, $"Cannot query the Recycle Bin for {root}");
            }
        }

        return new RecycleBinUsage(info.i64Size, info.i64NumItems);
    }

    private static unsafe string? ShellDelete(string fullPath)
    {
        // pFrom is a list of paths, each null-terminated, ending with an extra null.
        string from = fullPath + "\0\0";
        fixed (char* p = from)
        {
            var operation = new SHFILEOPSTRUCTW
            {
                wFunc = PInvoke.FO_DELETE,
                pFrom = p,
                fFlags = (ushort)DeleteFlags,
            };
            int result = PInvoke.SHFileOperation(&operation);
            if (result != 0)
            {
                return $"The shell could not recycle the item (error 0x{result:X}).";
            }

            return operation.fAnyOperationsAborted ? "The shell cancelled the operation." : null;
        }
    }

    private static bool Exists(string fullPath)
        => FindFileEnumerator.TryReadEntry(LongPath.ToExtended(fullPath), out _, out int error)
            || !Win32Errors.IsNotFound(error);
}
