using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevSweep.App.ViewModels.Places;
using DevSweep.App.ViewModels.Text;
using DevSweep.Core.Health.Ports;
using DevSweep.Core.Health.Runtimes;
using DevSweep.Core.Health.Security;
using DevSweep.Core.Projects;
using DevSweep.Core.Projects.Git;

namespace DevSweep.App.ViewModels;

/// <summary>Where a module-backed place is.</summary>
public enum PlaceState
{
    /// <summary>The module is registered but this build of the app has no adapter for it yet.</summary>
    Unavailable,
    NotChecked,
    Loading,
    Ready,
    Failed,
}

public enum HealthTab
{
    Tools,
    Security,
    Ports,
}

public enum ProjectsTab
{
    Projects,
    Cleanup,
    Accounts,
    Activity,
}
