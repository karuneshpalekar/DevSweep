using DevSweep.Core.Inventory;
using DevSweep.Core.Inventory.Collectors;
using DevSweep.Core.Inventory.Registry;
using Microsoft.Win32;
using Xunit;

namespace DevSweep.Core.Tests.Inventory;

public sealed class InventoryTests
{
    [Fact]
    public void Every_registry_view_and_the_package_repository_are_read()
    {
        AppInventory inventory = FakeMachine.Inventory();

        Assert.All(inventory.Sources, s => Assert.True(s.Complete, $"{s.Title}: {string.Join("; ", s.Problems)}"));
        Assert.Equal(1, Count(inventory, InventorySource.UninstallMachine64));
        Assert.Equal(1, Count(inventory, InventorySource.UninstallMachine32));
        Assert.Equal(2, Count(inventory, InventorySource.UninstallUser64));
        Assert.Equal(1, Count(inventory, InventorySource.StorePackages));
        Assert.Equal(1, Count(inventory, InventorySource.AppPaths));

        AppRecord studio = Assert.Single(inventory.Records, r => r.Source == InventorySource.UninstallMachine64);
        Assert.Equal("11111111-2222-3333-4444-555555555555", studio.ProductCode);
        Assert.Equal("Contoso Ltd", studio.Publisher);

        AppRecord terminal = Assert.Single(inventory.Records, r => r.Source == InventorySource.StorePackages);
        Assert.Equal("Microsoft.WindowsTerminal", terminal.Name);
        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe", terminal.PackageFamilyName);

        Assert.Equal("BurntSushi.ripgrep.MSVC", Assert.Single(inventory.Records, r => r.Name == "ripgrep").PackageId);
    }

    [Theory]
    [InlineData("names", "Contoso Studio", InventorySource.UninstallMachine64)]
    [InlineData("names", "Postman", InventorySource.UninstallUser64)]
    [InlineData("publishers", "Fabrikam", InventorySource.UninstallMachine32)]
    [InlineData("productCodes", "{11111111-2222-3333-4444-555555555555}", InventorySource.UninstallMachine64)]
    [InlineData("packageFamilies", "Microsoft.WindowsTerminal", InventorySource.StorePackages)]
    [InlineData("packageFamilies", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", InventorySource.StorePackages)]
    [InlineData("packageIds", "burntsushi.ripgrep.msvc", InventorySource.UninstallUser64)]
    [InlineData("exes", "LITWARE.exe", InventorySource.AppPaths)]
    [InlineData("exes", "devenv.exe", InventorySource.RunningProcesses)]
    public void Is_installed_names_the_source_and_the_hint_that_matched(string kind, string value, InventorySource source)
    {
        AppInventory inventory = FakeMachine.Inventory(processes: "devenv");
        AppHint hint = kind switch
        {
            "names" => new AppHint { Name = value, Names = [value] },
            "publishers" => new AppHint { Name = value, Publishers = [value] },
            "productCodes" => new AppHint { Name = value, ProductCodes = [value] },
            "packageFamilies" => new AppHint { Name = value, PackageFamilies = [value] },
            "packageIds" => new AppHint { Name = value, PackageIds = [value] },
            _ => new AppHint { Name = value, Exes = [value] },
        };

        InstallMatch match = inventory.IsInstalled(hint);

        Assert.Equal(InstallState.Installed, match.State);
        Assert.Contains(match.Evidence, e => e.Source == source);
        Assert.Equal(inventory.Sources.Count, match.Checked.Count);
    }

    [Fact]
    public void Not_installed_only_when_every_source_was_read_and_lists_every_source()
    {
        AppInventory inventory = FakeMachine.Inventory();
        var hint = new AppHint { Name = "Slack", Names = ["Slack"], Exes = ["slack.exe"], PackageIds = ["slack"] };

        InstallMatch match = inventory.IsInstalled(hint);

        Assert.Equal(InstallState.NotInstalled, match.State);
        Assert.Empty(match.Evidence);
        Assert.Equal(
            [InventorySource.UninstallMachine64, InventorySource.UninstallMachine32, InventorySource.UninstallUser64, InventorySource.UninstallUser32,
             InventorySource.StorePackages, InventorySource.AppPaths, InventorySource.RunningProcesses],
            match.Checked.Select(s => s.Source));
    }

    [Fact]
    public void A_source_that_cannot_be_read_makes_gone_unknown()
    {
        InMemoryRegistry registry = FakeMachine.Registry().Deny(RegistryHive.LocalMachine, RegistryView.Registry32, FakeMachine.Uninstall);
        AppInventory inventory = FakeMachine.Inventory(registry);

        InstallMatch match = inventory.IsInstalled(new AppHint { Name = "Slack", Names = ["Slack"] });

        Assert.Equal(InstallState.Unknown, match.State);
        Assert.Contains("32-bit", match.UnknownBecause, StringComparison.Ordinal);
        Assert.False(inventory.Sources.Single(s => s.Source == InventorySource.UninstallMachine32).Complete);

        // Still installed when another source lists it.
        Assert.True(inventory.IsInstalled(new AppHint { Name = "Postman", Names = ["Postman"] }).IsInstalled);
    }

    [Fact]
    public void A_missing_package_repository_or_machine_uninstall_key_is_a_problem_not_an_empty_source()
    {
        var registry = new InMemoryRegistry();
        AppInventory inventory = FakeMachine.Inventory(registry);

        Assert.False(inventory.Sources.Single(s => s.Source == InventorySource.StorePackages).Complete);
        Assert.False(inventory.Sources.Single(s => s.Source == InventorySource.UninstallMachine64).Complete);
        Assert.True(inventory.Sources.Single(s => s.Source == InventorySource.UninstallUser64).Complete);
        Assert.Equal(InstallState.Unknown, inventory.IsInstalled(new AppHint { Name = "X", Names = ["X"] }).State);
    }

    [Fact]
    public void A_turned_off_source_makes_gone_unknown()
    {
        AppInventory inventory = FakeMachine.Inventory(disabled: [InventorySource.Winget]);

        InstallMatch match = inventory.IsInstalled(new AppHint { Name = "Slack", Names = ["Slack"] });

        Assert.Equal(InstallState.Unknown, match.State);
        Assert.Contains("turned off", match.UnknownBecause, StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_app_paths_do_not_count_and_an_empty_hint_is_never_gone()
    {
        AppInventory inventory = FakeMachine.Inventory();

        Assert.DoesNotContain(inventory.Records, r => r.Name == "gone");
        Assert.Equal(InstallState.Unknown, inventory.IsInstalled(new AppHint { Name = "Nothing" }).State);
    }

    [Theory]
    [InlineData("Slack", "Slack (Machine - MSI)", true)]
    [InlineData("Slack", "Slackware Tools", false)]
    [InlineData("Postman", "Postman x64 11.2.0", true)]
    [InlineData("GitHub Desktop", "GitHubDesktop", true)]
    [InlineData("GitHub Desktop", "GitHub CLI", false)]
    [InlineData("Visual Studio Code", "Microsoft Visual Studio Code (User)", false)]
    [InlineData("Microsoft Visual Studio Code", "Microsoft Visual Studio Code (User)", true)]
    [InlineData("JetBrains", "JetBrains s.r.o.", true)]
    public void Names_match_by_leading_words(string hint, string name, bool expected)
        => Assert.Equal(expected, AppMatcher.WordsMatch(hint, name));

    [Fact]
    public void Records_of_one_app_merge_by_ids_and_names()
    {
        AppRecord msi = new() { Source = InventorySource.UninstallMachine64, Name = "Contoso Studio 2024", ProductCode = "A" };
        AppRecord shortcut = new() { Source = InventorySource.StartMenu, Name = "Contoso Studio", ProductCode = "A" };
        AppRecord scoop = new() { Source = InventorySource.Scoop, Name = "contoso-studio", PackageId = "contoso" };
        AppRecord choco = new() { Source = InventorySource.Chocolatey, Name = "Contoso Studio", PackageId = "contoso" };
        var inventory = new AppInventory([msi, shortcut, scoop, choco], []);

        InstalledApp app = Assert.Single(inventory.Apps);
        Assert.Equal(4, app.Sources.Count);
        Assert.Equal(["A"], app.ProductCodes);
    }

    [Theory]
    [InlineData("\"C:\\App\\app.exe\",0", "C:\\App\\app.exe")]
    [InlineData("C:\\Program Files\\App\\uninstall.exe /S", "C:\\Program Files\\App\\uninstall.exe")]
    [InlineData("C:\\App\\icon.ico", null)]
    [InlineData("app.exe", null)]
    public void Program_paths_come_out_of_command_lines(string command, string? expected)
    {
        if (!OperatingSystem.IsWindows() && expected is not null)
        {
            return; // Drive-letter paths are only full paths on Windows.
        }

        Assert.Equal(expected, ExePath.FromCommand(command));
    }

    [Theory]
    [InlineData("Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("91750D7E.Slack_4.41.105.0_x64__8she8kybcnzg4", "91750D7E.Slack_8she8kybcnzg4")]
    [InlineData("Not_A_Package", null)]
    public void Package_family_names_come_from_full_names(string fullName, string? family)
    {
        bool parsed = PackageName.TryParse(fullName, out _, out _, out string? familyName);
        Assert.Equal(family is not null, parsed);
        Assert.Equal(family, familyName);
    }

    private static int Count(AppInventory inventory, InventorySource source) => inventory.Sources.Single(s => s.Source == source).Count;
}
