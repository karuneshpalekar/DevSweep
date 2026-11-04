using System.Globalization;
using System.Text;
using DevSweep.Core.Safety;
using Xunit;

namespace DevSweep.Core.Tests.Safety;

/// <summary>
/// Property tests: random paths built from tricky pieces (protected names in any
/// case, <c>..</c>, short names, trailing dots and spaces, stream syntax,
/// <c>\\?\</c> prefixes, forward slashes, doubled and trailing separators),
/// checked against a separate, deliberately simple model of the rules.
/// </summary>
public sealed class SafetyFuzzTests(ITestOutputHelper output)
{
    private const int Iterations = 50_000;
    private static readonly SafetyContext Context = TestContexts.Create();

    private static readonly string[] Roots =
    [
        @"C:\", "c:/", @"D:\", @"E:\", @"\\?\C:\", @"\\?\e:\", @"\\server\share\", "C:", "relative\\", @"\\.\C:\", @"\\?\UNC\server\share\",
        @"C:\Users\Dev\", @"c:/users/dev/", @"\\?\C:\Users\Dev\", @"C:\Users\Dev\AppData\Local\", @"C:\Users\Dev\AppData\Local\DevSweep\Quarantine\",
        @"E:\src\", @"D:\OneDrive - Contoso\",
    ];

    private static readonly string[] Pieces =
    [
        "Users", "Dev", "dev", "AppData", "Local", "Roaming", "LocalLow", "Microsoft", "Packages", "Temp", "DevSweep", "Quarantine",
        "OneDrive - Contoso", "Documents", "Desktop", "Downloads", "src", "src-old", "app", "node_modules", ".ssh", ".nuget", ".konan",
        "scoop", "Projects", "x", "a b", "Program Files", "Windows", "DOCUME~1", ".cache", "cache",
    ];

    /// <summary>Pieces Windows would reinterpret; drawn one time in eight.</summary>
    private static readonly string[] TrickyPieces = ["..", ".", "name.", "name ", "a:b", "*", "a?b", "a\"b", "a|b", "", "\t"];

    [Fact]
    public void Random_paths_agree_with_the_reference_model()
    {
        int seed = Environment.TickCount;
        var random = new Random(seed);
        int allowed = 0;
        int invalid = 0;
        for (int i = 0; i < Iterations; i++)
        {
            string path = RandomPath(random);
            bool expectedAllowed = Reference.IsAllowed(path, out bool expectedValid);
            SafetyVerdict verdict = Evaluate(path);

            Assert.True(expectedAllowed == verdict.Allowed,
                $"seed {seed}: '{path}' expected allowed={expectedAllowed}, got {verdict.Refusal}: {verdict.Reason}");
            Assert.True(expectedValid == (verdict.Refusal != SafetyRefusal.InvalidPath),
                $"seed {seed}: '{path}' expected valid={expectedValid}, got {verdict.Refusal}: {verdict.Reason}");
            allowed += verdict.Allowed ? 1 : 0;
            invalid += expectedValid ? 0 : 1;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seed {seed}: {Iterations:N0} paths, {allowed:N0} allowed, {invalid:N0} refused as invalid, the rest refused by policy"));
        Assert.InRange(allowed, 1, Iterations - 1);
    }

    [Fact]
    public void Spelling_variants_get_the_same_answer()
    {
        var random = new Random(1234);
        for (int i = 0; i < Iterations / 5; i++)
        {
            string path = RandomPath(random);
            if (!SafePath.TryCanonicalize(path, out string? canonical, out _))
            {
                continue;
            }

            SafetyVerdict baseline = SafetyPolicy.Evaluate(Context, canonical);
            foreach (string variant in Variants(canonical))
            {
                SafetyVerdict other = Evaluate(variant);
                Assert.True(baseline.Refusal == other.Refusal, $"'{canonical}' -> {baseline.Refusal} but '{variant}' -> {other.Refusal}");
            }
        }
    }

    [Fact]
    public void Nothing_protected_and_nothing_above_it_is_ever_allowed()
    {
        foreach (ProtectedPath item in Context.Protected)
        {
            string current = item.Path;
            while (true)
            {
                foreach (string variant in Variants(current).Append(current))
                {
                    Assert.False(Evaluate(variant).Allowed, $"{variant} was allowed");
                }

                string? parent = Path.GetDirectoryName(current);
                if (parent is null || parent.Length >= current.Length)
                {
                    break;
                }

                current = parent;
            }
        }
    }

    [Fact]
    public void Any_dot_dot_segment_is_refused_wherever_it_points()
    {
        var random = new Random(99);
        for (int i = 0; i < 5_000; i++)
        {
            string path = RandomPath(random);
            string withDots = path.TrimEnd('\\', '/') + @"\..\" + Pieces[random.Next(Pieces.Length)];
            Assert.Equal(SafetyRefusal.InvalidPath, Evaluate(withDots).Refusal);
        }
    }

    private static SafetyVerdict Evaluate(string path)
        => SafePath.TryCanonicalize(path, out string? canonical, out string rejection)
            ? SafetyPolicy.Evaluate(Context, canonical)
            : new SafetyVerdict(path, SafetyRefusal.InvalidPath, rejection);

    private static IEnumerable<string> Variants(string canonical)
    {
        yield return canonical.ToUpperInvariant();
        yield return canonical.ToLowerInvariant();
        yield return canonical + "\\";
        yield return canonical.Replace('\\', '/');
        if (canonical.Length > 2 && canonical[1] == ':')
        {
            yield return @"\\?\" + canonical;
        }
    }

    private static string RandomPath(Random random)
    {
        var path = new StringBuilder(Roots[random.Next(Roots.Length)]);
        int depth = random.Next(0, 7);
        for (int d = 0; d < depth; d++)
        {
            string piece = random.Next(8) == 0 ? TrickyPieces[random.Next(TrickyPieces.Length)] : Pieces[random.Next(Pieces.Length)];
            path.Append(random.Next(3) switch
            {
                0 => piece.ToUpperInvariant(),
                1 => piece.ToLowerInvariant(),
                _ => piece,
            });
            path.Append(random.Next(10) switch
            {
                0 => "/",
                1 => @"\\",
                _ => @"\",
            });
        }

        string result = path.ToString();
        return random.Next(2) == 0 ? result.TrimEnd('\\', '/') : result;
    }

    /// <summary>
    /// The rules restated as simply as possible, independent of the implementation:
    /// paths as lists of lower-case segments.
    /// </summary>
    private static class Reference
    {
        public static bool IsAllowed(string raw, out bool valid)
        {
            valid = TrySegments(raw, out List<string> segments);
            if (!valid)
            {
                return false;
            }

            List<List<string>> protectedPaths = [.. Context.Protected.Select(p => Split(p.Path))];
            foreach (List<string> item in protectedPaths)
            {
                if (StartsWith(item, segments))
                {
                    return false; // equal to, or an ancestor of, something protected
                }
            }

            List<string> profile = Split(TestContexts.Profile);
            List<string> dev = Split(TestContexts.DevRoot);
            List<string> quarantine = Split(TestContexts.Quarantine);
            if (StrictlyUnder(segments, dev) || StrictlyUnder(segments, quarantine))
            {
                return true;
            }

            if (!StrictlyUnder(segments, profile))
            {
                return false;
            }

            return segments.Count > profile.Count + 1 || segments[profile.Count].StartsWith('.');
        }

        private static bool TrySegments(string raw, out List<string> segments)
        {
            segments = [];
            string p = raw.Replace('/', '\\');
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                p = @"\\" + p[8..];
            }
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                p = p[4..];
                if (!(p.Length >= 3 && p[1] == ':' && p[2] == '\\'))
                {
                    return false;
                }
            }
            else if (p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts;
            if (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\')
            {
                segments.Add(p[..2].ToLowerInvariant());
                parts = p[3..].TrimEnd('\\').Split('\\');
            }
            else if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string[] unc = p[2..].TrimEnd('\\').Split('\\');
                if (unc.Length < 2 || unc[0].Length == 0 || unc[1].Length == 0)
                {
                    return false;
                }

                segments.Add(@"\\" + unc[0].ToLowerInvariant() + @"\" + unc[1].ToLowerInvariant());
                parts = unc[2..];
            }
            else
            {
                return false;
            }

            foreach (string part in parts)
            {
                if (part.Length == 0 && parts.Length == 1)
                {
                    continue; // the root itself
                }

                if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || part.Any(char.IsControl))
                {
                    return false;
                }

                segments.Add(part.ToLowerInvariant());
            }

            return true;
        }

        private static List<string> Split(string canonical)
        {
            Assert.True(TrySegments(canonical, out List<string> segments));
            return segments;
        }

        private static bool StartsWith(List<string> path, List<string> prefix)
            => prefix.Count <= path.Count && prefix.SequenceEqual(path.Take(prefix.Count));

        private static bool StrictlyUnder(List<string> path, List<string> ancestor)
            => path.Count > ancestor.Count && StartsWith(path, ancestor);
    }
}
