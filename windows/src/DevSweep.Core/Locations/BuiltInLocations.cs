using DevSweep.Core.Locations.Resolvers;

namespace DevSweep.Core.Locations;

/// <summary>The resolvers every engine has: the Known Folder roots, <c>{Temp}</c> and the tool locations.</summary>
public static class BuiltInLocations
{
    public static IReadOnlyList<ILocationResolver> All()
    {
        var resolvers = new List<ILocationResolver>();
        resolvers.AddRange(KnownFolderResolver.Roots.Select(root => new KnownFolderResolver(root.Name, root.Description)));
        resolvers.Add(new TempResolver());
        resolvers.AddRange(
        [
            new NpmCacheResolver(),
            new PnpmStoreResolver(),
            new YarnCacheResolver(),
            new PipCacheResolver(),
            new UvCacheResolver(),
            new NuGetPackagesResolver(),
            new NuGetHttpCacheResolver(),
            new GradleHomeResolver(),
            new MavenRepositoryResolver(),
            new CargoHomeResolver(),
            new GoModCacheResolver(),
            new AndroidSdkResolver(),
            new HuggingFaceHomeResolver(),
            new HuggingFaceHubCacheResolver(),
            new ScoopRootResolver(),
            new ScoopGlobalResolver(),
            new ChocolateyResolver(),
            new WingetPackagesResolver(),
            new WingetMachinePackagesResolver(),
        ]);
        return resolvers;
    }
}
