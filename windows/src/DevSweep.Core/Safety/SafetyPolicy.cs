namespace DevSweep.Core.Safety;

/// <summary>
/// The lexical half of the guard: given a canonical, long-name path and a
/// <see cref="SafetyContext"/>, may DevSweep remove it? No disk access, so it is
/// unit-tested and fuzzed directly.
/// </summary>
public static class SafetyPolicy
{
    public static SafetyVerdict Evaluate(SafetyContext context, string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(canonicalPath);

        foreach (ProtectedPath item in context.Protected)
        {
            if (SafePath.AreSame(canonicalPath, item.Path))
            {
                return new SafetyVerdict(canonicalPath, SafetyRefusal.Protected, $"{canonicalPath} is {item.Why}, so DevSweep never removes it.");
            }
        }

        foreach (ProtectedPath item in context.Protected)
        {
            if (SafePath.IsStrictlyUnder(item.Path, canonicalPath))
            {
                return new SafetyVerdict(canonicalPath, SafetyRefusal.ContainsProtected,
                    $"{canonicalPath} contains {item.Path} ({item.Why}), so DevSweep won't remove it.");
            }
        }

        if (context.QuarantineRoots.Any(root => SafePath.IsStrictlyUnder(canonicalPath, root))
            || context.DevRoots.Any(root => SafePath.IsStrictlyUnder(canonicalPath, root)))
        {
            return SafetyVerdict.Allow(canonicalPath);
        }

        if (SafePath.IsStrictlyUnder(canonicalPath, context.ProfileRoot))
        {
            IReadOnlyList<string> relative = SafePath.RelativeSegments(canonicalPath, context.ProfileRoot);
            if (relative.Count == 1 && !relative[0].StartsWith('.'))
            {
                // Only hidden tool folders (~\.konan) may go from the top of the profile;
                // a visible folder there (~\Projects) is the user's own.
                return new SafetyVerdict(canonicalPath, SafetyRefusal.TooShallow,
                    $"{canonicalPath} is a folder directly in your profile, too close to your own files to remove safely.");
            }

            return SafetyVerdict.Allow(canonicalPath);
        }

        return new SafetyVerdict(canonicalPath, SafetyRefusal.OutsideAllowedRoots,
            $"{canonicalPath} is outside your profile and your development folders, so DevSweep won't touch it; it can show you the command instead.");
    }
}
