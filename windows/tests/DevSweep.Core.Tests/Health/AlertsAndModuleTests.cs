using DevSweep.Core.Health;
using DevSweep.Core.Health.Alerts;
using DevSweep.Core.Modules;
using DevSweep.Core.Tests.Rules;
using Xunit;

namespace DevSweep.Core.Tests.Health;

public sealed class AlertsAndModuleTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_problem_is_announced_once_then_again_after_its_cool_down()
    {
        var ledger = new AlertLedger();
        var settings = new AlertSettings();
        var input = new AlertInputs
        {
            DiskUsedPercent = 91.4,
            Grew = [new AlertGrowth("npm cache", 3_500_000_000), new AlertGrowth("pip cache", 2_100_000_000), new AlertGrowth("tiny", 10)],
            EndOfLife = [new AlertItem("eol:node:16", "Node.js 16.20.2 (nvm-windows) no longer gets security fixes.")],
            Secrets = [new AlertItem("security:a", "AWS access keys")],
        };

        IReadOnlyList<AlertEvent> first = AlertEvaluator.Evaluate(input, settings, ledger, Monday);
        Assert.Equal(["Your disk is 91% full", "npm cache grew by 3.3 GB", "A tool you use is past end of life"], first.Select(e => e.Title));
        Assert.Equal("1 other item grew a lot too.", first[1].Body);
        Assert.DoesNotContain(first, e => e.Target == AlertTarget.HealthSecurity); // first run: secrets are the baseline

        Assert.Empty(AlertEvaluator.Evaluate(input, settings, ledger, Monday.AddDays(1)));
        Assert.Equal(["disk"], AlertEvaluator.Evaluate(input, settings, ledger, Monday.AddDays(3)).Select(e => e.Key));
        Assert.Equal(3, AlertEvaluator.Evaluate(input, settings, ledger, Monday.AddDays(7)).Count); // disk again (4 days), growth and end of life (7 days)

        // Back under the limit, then over again: said at once.
        Assert.Empty(AlertEvaluator.Evaluate(input with { DiskUsedPercent = 50, Grew = [], EndOfLife = [] }, settings, ledger, Monday.AddDays(8)));
        Assert.Equal(["disk"], AlertEvaluator.Evaluate(input with { Grew = [], EndOfLife = [] }, settings, ledger, Monday.AddDays(8).AddHours(1)).Select(e => e.Key));

        // Secrets: only ones not in the baseline are new.
        var withBaseline = input with { DiskUsedPercent = null, Grew = [], EndOfLife = [], KnownSecretIds = new HashSet<string> { "security:a" }, Secrets = [new("security:a", "AWS access keys"), new("security:b", "A private key outside .ssh: C:\\x")] };
        AlertEvent secret = Assert.Single(AlertEvaluator.Evaluate(withBaseline, settings, ledger, Monday.AddDays(9)));
        Assert.Equal(("A new file looks like a secret", AlertTarget.HealthSecurity), (secret.Title, secret.Target));
        Assert.Empty(AlertEvaluator.Evaluate(withBaseline, settings, ledger, Monday.AddDays(10)));

        Assert.Empty(AlertEvaluator.Evaluate(input, new AlertSettings { DiskEnabled = false, GrowthEnabled = false, EndOfLifeEnabled = false }, new AlertLedger(), Monday));
    }

    [Fact]
    public void Snapshots_compare_with_last_week_and_survive_a_restart()
    {
        string folder = Path.Join(Path.GetTempPath(), "devsweep-snapshots-unit-" + Guid.NewGuid().ToString("N")[..6]);
        string file = Path.Join(folder, "scans.json");
        try
        {
            var store = new ScanSnapshotStore(file);
            Assert.Null(store.Changes([("npm", "npm cache", 1L)], Monday));
            store.Record([("npm", "npm cache", 1_000_000_000), ("pip", "pip cache", 500_000_000)], 10_000, Monday.AddDays(-8));
            store.Record([("npm", "npm cache", 1_100_000_000)], 9_000, Monday.AddDays(-2));

            var reopened = new ScanSnapshotStore(file);
            Assert.Equal(2, reopened.Snapshots.Count);
            ScanChanges changes = reopened.Changes([("npm", "npm cache", 3_000_000_000L), ("pip", "pip cache", 550_000_000L), ("gradle", "Gradle caches", 900_000_000L)], Monday)!;
            Assert.Equal(Monday.AddDays(-8), changes.Since);
            Assert.Equal(("npm", 2_000_000_000L), (Assert.Single(changes.Grew).Id, changes.Grew[0].Bytes));
            Assert.Equal("gradle", Assert.Single(changes.New).Id);

            for (int i = 0; i < 45; i++)
            {
                reopened.Record([], null, Monday.AddMinutes(i));
            }

            Assert.Equal(ScanSnapshotStore.Keep, new ScanSnapshotStore(file).Snapshots.Count);

            File.WriteAllText(file, "{ not json");
            Assert.Empty(new ScanSnapshotStore(file).Snapshots);
            Assert.Single(Directory.GetFiles(folder, "scans.json.unreadable-*"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_alert_store_keeps_the_ledger_and_the_secret_baseline()
    {
        string folder = Path.Join(Path.GetTempPath(), "devsweep-alerts-unit-" + Guid.NewGuid().ToString("N")[..6]);
        string file = Path.Join(folder, "alerts.json");
        try
        {
            var store = new AlertStore(file);
            Assert.Null(store.Data.KnownSecretIds);
            store.Data.Ledger.Notified["disk"] = Monday;
            store.Update(d => d with { LastScheduledRun = Monday, KnownSecretIds = ["security:a"] });

            var reopened = new AlertStore(file);
            Assert.Equal(Monday, reopened.Data.LastScheduledRun);
            Assert.Equal(["security:a"], reopened.Data.KnownSecretIds);
            Assert.Equal(Monday, reopened.Data.Ledger.Notified["disk"]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Health_is_a_module_and_each_tab_has_its_own_flag()
    {
        DevSweepEngine off = TestSchema.Engine();
        Assert.Empty(off.Services<HealthChecks>());
        Assert.Contains("module:health", off.Disabled);

        DevSweepEngine on = TestSchema.Engine(FeatureFlags.Default.With("+module:health"));
        Assert.Contains(on.Modules, m => m.Id == "health");
        HealthFeatures all = Assert.Single(on.Services<HealthFeatures>());
        Assert.Equal((true, true, true), (all.Runtimes, all.Security, all.Ports));

        DevSweepEngine noPorts = TestSchema.Engine(FeatureFlags.Default.With("+module:health -module:health.ports"));
        HealthChecks checks = Assert.Single(noPorts.Services<HealthChecks>());
        Assert.Equal((true, true, false), (checks.Features.Runtimes, checks.Features.Security, checks.Features.Ports));
        Assert.Equal(["module:health.ports"], checks.Features.Disabled);
        Assert.Throws<InvalidOperationException>(() => checks.Ports(noPorts));
    }
}
