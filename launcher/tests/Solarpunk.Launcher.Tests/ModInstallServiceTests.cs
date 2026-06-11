using FluentAssertions;
using Solarpunk.Launcher.Services;
using Xunit;

namespace Solarpunk.Launcher.Tests;

// The bundled-mod install must be idempotent (a re-join is a no-op) and must
// actually land the mod's files in the UE4SS Mods dir.
public class ModInstallServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solarpunk-modtests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstallBundled_CopiesMod_ThenSkipsOnReinstall()
    {
        var modsRoot = Path.Combine(_root, "ue4ss", "Mods");
        var source = Path.Combine(_root, "pack", "Mods", "SolarpunkConnect");
        Directory.CreateDirectory(Path.Combine(source, "Scripts"));
        File.WriteAllText(Path.Combine(source, "enabled.txt"), "");
        File.WriteAllText(Path.Combine(source, "Scripts", "main.lua"), "-- mod");

        var svc = new ModInstallService(modsRoot);
        var mod = new ModInstallService.BundledMod("SolarpunkConnect", source, "1.0.0");

        var first = svc.InstallBundled(new[] { mod });
        first.AllOk.Should().BeTrue();
        first.Results[0].Status.Should().Be(ModInstallService.InstallStatus.Installed);
        File.Exists(Path.Combine(modsRoot, "SolarpunkConnect", "Scripts", "main.lua")).Should().BeTrue();

        var second = svc.InstallBundled(new[] { mod });
        second.Results[0].Status.Should().Be(ModInstallService.InstallStatus.Skipped);
    }

    [Fact]
    public void InstallBundled_VersionBump_Reinstalls()
    {
        var modsRoot = Path.Combine(_root, "ue4ss2", "Mods");
        var source = Path.Combine(_root, "pack2", "Mods", "SolarpunkConnect");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "main.lua"), "v1");

        var svc = new ModInstallService(modsRoot);
        svc.InstallBundled(new[] { new ModInstallService.BundledMod("SolarpunkConnect", source, "1.0.0") });

        var bumped = svc.InstallBundled(new[] { new ModInstallService.BundledMod("SolarpunkConnect", source, "1.0.1") });
        bumped.Results[0].Status.Should().Be(ModInstallService.InstallStatus.Installed);
    }

    [Fact]
    public void InstallBundled_MissingSource_ReportsSourceMissing()
    {
        var svc = new ModInstallService(Path.Combine(_root, "ue4ss3", "Mods"));
        var result = svc.InstallBundled(new[]
        {
            new ModInstallService.BundledMod("SolarpunkConnect", Path.Combine(_root, "nope"), "1.0.0"),
        });
        result.AllOk.Should().BeFalse();
        result.Results[0].Status.Should().Be(ModInstallService.InstallStatus.SourceMissing);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
