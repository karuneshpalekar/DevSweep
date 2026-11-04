using DevSweep.Core.Platform;
using DevSweep.Core.Safety;
using DevSweep.Fixtures;
using Xunit;

namespace DevSweep.Accuracy;

/// <summary>The guard on this machine's real profile: Known Folders, short names, junction tricks, final-path checks.</summary>
public sealed class SafetyGuardTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempFixture _fixture = new("guard");
    private readonly SafetyGuard _guard = SafetyGuard.ForCurrentUser();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Known_folders_and_profile_roots_are_protected()
    {
        string profile = KnownFolders.Profile;
        string local = KnownFolders.LocalAppData;
        output.WriteLine($"profile={profile} localAppData={local} ({Elevation.Describe()}); {_guard.Context.Protected.Count} protected paths");
        foreach (KnownFolder folder in KnownFolders.ResolveAll())
        {
            output.WriteLine($"  {folder.Name}: {folder.Path}");
        }

        string[] protectedPaths =
        [
            profile, profile + "\\", KnownFolders.Desktop, Path.Combine(profile, "AppData"), local,
            Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "LocalLow"),
            Path.Combine(local, "Microsoft"), Path.Combine(local, "Packages"), Path.Combine(local, "Temp"),
            Path.Combine(profile, ".ssh"), Path.Combine(profile, ".nuget"), Path.Combine(profile, "scoop"),
            Path.GetTempPath(), Path.GetDirectoryName(profile)!, Path.GetPathRoot(profile)!,
        ];
        foreach (string path in protectedPaths)
        {
            SafetyVerdict verdict = _guard.CheckLexically(path);
            Assert.False(verdict.Allowed, $"{path} was allowed");
            Assert.Contains(verdict.Refusal, new[] { SafetyRefusal.Protected, SafetyRefusal.ContainsProtected });
        }

        Assert.Equal(SafetyRefusal.TooShallow, _guard.CheckLexically(Path.Combine(profile, "Projects")).Refusal);
        Assert.Equal(SafetyRefusal.OutsideAllowedRoots, _guard.CheckLexically(@"C:\Windows\Temp\x").Refusal);
        Assert.Equal(SafetyRefusal.InvalidPath, _guard.CheckLexically(Path.Combine(local, "x", "..", "Microsoft")).Refusal);
    }

    [Fact]
    public void A_fixture_item_is_allowed_and_opened_as_itself()
    {
        string file = _fixture.CreateFile(@"item\cache.bin", 4096);

        SafetyVerdict verdict = _guard.TryOpen(file, out VerifiedItem? item);
        using (item)
        {
            output.WriteLine($"{file} -> {verdict.Path}: {verdict.Refusal}");
            Assert.True(verdict.Allowed, verdict.Reason);
            Assert.NotNull(item);
            Assert.False(item.IsDirectory);
        }
    }

    [Fact]
    public void Short_names_are_resolved_before_comparing()
    {
        string folder = _fixture.CreateDirectory("A Folder With A Long Name");
        string shortForm = Ntfs.ShortPath(folder);
        if (shortForm == folder && Elevation.IsAdministrator && Ntfs.TrySetShortName(folder, "AFOLDE~9"))
        {
            shortForm = Ntfs.ShortPath(folder);
        }

        string temp = Path.GetTempPath();
        output.WriteLine($"TEMP={temp} long={LongNames.Resolve(temp.TrimEnd('\\'))}; folder short form={shortForm}");
        if (string.Equals(shortForm, folder, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("8.3 short names are disabled on this volume and could not be set (fsutil 8dot3name), so there is no short spelling to resolve.");
        }

        string file = Path.Combine(shortForm, "x.bin");
        File.WriteAllBytes(file, [1]);
        SafetyVerdict viaShort = _guard.Check(file);
        Assert.True(viaShort.Allowed, viaShort.Reason);
        Assert.Equal(Path.Combine(LongNames.Resolve(folder), "x.bin"), viaShort.Path, ignoreCase: true);
        Assert.DoesNotContain('~', viaShort.Path);

        // The short spelling of a protected folder is still that folder.
        string localShort = Ntfs.ShortPath(Path.Combine(KnownFolders.LocalAppData, "Microsoft"));
        Assert.Equal(SafetyRefusal.Protected, _guard.CheckLexically(localShort).Refusal);
    }

    [Fact]
    public void A_path_through_a_junction_is_refused_and_the_target_untouched()
    {
        string target = _fixture.CreateFile(@"real\keep.bin", 4096);
        string link = _fixture.PathOf("link");
        Ntfs.CreateJunction(link, _fixture.PathOf("real"));

        SafetyVerdict through = _guard.Check(Path.Combine(link, "keep.bin"));
        SafetyVerdict linkItself = _guard.TryOpen(link, out VerifiedItem? item);
        using (item)
        {
            output.WriteLine($"through: {through.Refusal}; link itself: {linkItself.Refusal} isLink={item?.IsLink}");
            Assert.Equal(SafetyRefusal.ThroughLink, through.Refusal);
            Assert.True(linkItself.Allowed, linkItself.Reason);
            Assert.True(item!.IsLink);
        }

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Removal_acts_on_the_checked_object_even_if_the_path_is_swapped()
    {
        string file = _fixture.CreateFile(@"swap\victim.bin", 4096);
        string movedAway = _fixture.PathOf(@"swap\renamed-meanwhile.bin");
        Assert.True(_guard.TryOpen(file, out VerifiedItem? item).Allowed);
        using (item)
        {
            // Between the check and the delete, someone renames the item and puts something else at its path.
            File.Move(file, movedAway);
            File.WriteAllText(file, "decoy");

            DeleteReport report = item!.DeleteForGood();

            Assert.True(report.Succeeded);
        }

        Assert.Equal("decoy", File.ReadAllText(file));
        Assert.False(File.Exists(movedAway));
    }

    [Fact]
    public void A_junction_to_a_protected_folder_does_not_unlock_it()
    {
        string link = _fixture.PathOf("to-local-microsoft");
        Ntfs.CreateJunction(link, Path.Combine(KnownFolders.LocalAppData, "Microsoft"));

        SafetyVerdict child = _guard.Check(Path.Combine(link, "Windows"));

        Assert.Equal(SafetyRefusal.ThroughLink, child.Refusal);
    }

    [Fact]
    public void The_final_path_of_a_handle_exposes_a_junction_on_the_way()
    {
        string target = _fixture.CreateFile(@"real2\file.bin", 4096);
        string link = _fixture.PathOf("link2");
        Ntfs.CreateJunction(link, _fixture.PathOf("real2"));
        string viaLink = Path.Combine(link, "file.bin");

        using var direct = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var through = File.OpenHandle(viaLink, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool directMatches = FinalPath.Matches(direct, LongNames.Resolve(target), out string? directActual);
        bool throughMatches = FinalPath.Matches(through, LongNames.Resolve(viaLink), out string? throughActual);

        output.WriteLine($"direct: {directMatches} {directActual}; through junction: {throughMatches} {throughActual}");
        Assert.True(directMatches);
        Assert.False(throughMatches);
    }

    [Fact]
    public void A_redirected_desktop_is_protected_where_it_now_is()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Redirects the real Desktop for a moment, so it only runs on CI (CI=true).");
        }

        string redirected = _fixture.CreateDirectory("Redirected Desktop");
        string original = KnownFolders.Desktop;
        using (KnownFolderRedirect.Desktop(redirected))
        {
            SafetyGuard guard = SafetyGuard.ForCurrentUser();
            output.WriteLine($"Desktop {original} -> {KnownFolders.Desktop}");
            Assert.Equal(redirected, KnownFolders.Desktop, ignoreCase: true);
            Assert.Equal(SafetyRefusal.Protected, guard.CheckLexically(redirected).Refusal);
            Assert.True(guard.CheckLexically(Path.Combine(redirected, "sub", "x")).Allowed);
        }

        Assert.Equal(original, KnownFolders.Desktop, ignoreCase: true);
    }
}
