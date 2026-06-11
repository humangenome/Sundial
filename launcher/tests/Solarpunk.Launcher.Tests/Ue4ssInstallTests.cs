using FluentAssertions;
using Solarpunk.Launcher.Services;
using Xunit;

namespace Solarpunk.Launcher.Tests;

// The UE4SS runtime install must land the proxy DLL next to the exe, UE4SS.dll +
// the Mods load-list (mods.txt) under <Win64>\ue4ss, and be idempotent across a
// re-connect at the same bundled version.
public class Ue4ssInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solarpunk-ue4sstests-" + Guid.NewGuid().ToString("N"));

    private string MakePack()
    {
        var pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(pack, "Mods", "SolarpunkConnect", "Scripts"));
        Directory.CreateDirectory(Path.Combine(pack, "UE4SS_Signatures"));
        File.WriteAllText(Path.Combine(pack, "UE4SS.dll"), "fake-ue4ss");
        File.WriteAllText(Path.Combine(pack, Ue4ssBootstrapService.ProxyDllName), "fake-proxy");
        File.WriteAllText(Path.Combine(pack, "Mods", "mods.txt"), "SolarpunkConnect : 1\r\n");
        File.WriteAllText(Path.Combine(pack, "Mods", "SolarpunkConnect", "enabled.txt"), "");
        File.WriteAllText(Path.Combine(pack, "Mods", "SolarpunkConnect", "Scripts", "main.lua"), "-- mod");
        File.WriteAllText(Path.Combine(pack, "UE4SS_Signatures", "GUObjectArray.lua"), "-- sig");
        File.WriteAllText(Path.Combine(pack, "UE4SS_Signatures", "FName_Constructor.lua"), "-- sig");
        return pack;
    }

    [Fact]
    public void Install_LandsRuntime_ProxyNextToExe_ModsTxtUnderUe4ss()
    {
        var pack = MakePack();
        var win64 = Path.Combine(_root, "game", "Solarpunk", "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        var result = Ue4ssBootstrapService.Install(pack, win64, "1.0.0");

        result.Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        File.Exists(Path.Combine(win64, Ue4ssBootstrapService.ProxyDllName)).Should().BeTrue("proxy must sit next to the exe");
        File.Exists(Path.Combine(win64, "ue4ss", "UE4SS.dll")).Should().BeTrue();
        File.Exists(Path.Combine(win64, "ue4ss", "Mods", "mods.txt")).Should().BeTrue("UE4SS load-list must be installed");
        result.ModsRoot.Should().Be(Path.Combine(win64, "ue4ss", "Mods"));
    }

    [Fact]
    public void Install_MirrorsEntirePackTree_IncludingSignaturesSubdir()
    {
        // Regression: the original install copied a hand-picked file list and
        // DROPPED the UE4SS_Signatures\ subfolder — without the custom AOB sigs
        // UE4SS's built-in scan times out on UE5.7 ("PS scan timed out") and the
        // auto-join never fires. The stager must mirror the whole pack tree.
        var pack = MakePack();
        var win64 = Path.Combine(_root, "game-sigs", "Solarpunk", "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        var result = Ue4ssBootstrapService.Install(pack, win64, "1.0.0");

        result.Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        File.Exists(Path.Combine(win64, "ue4ss", "UE4SS_Signatures", "GUObjectArray.lua"))
            .Should().BeTrue("every pack subdir must be mirrored, especially the UE5.7 AOB signatures");
        File.Exists(Path.Combine(win64, "ue4ss", "UE4SS_Signatures", "FName_Constructor.lua")).Should().BeTrue();
        File.Exists(Path.Combine(win64, "ue4ss", "Mods", "SolarpunkConnect", "Scripts", "main.lua"))
            .Should().BeTrue("the whole Mods subtree rides the same mirror");
    }

    [Fact]
    public void Install_SelfHeals_WhenPackFileMissing_EvenAtSameVersion()
    {
        // A same-version marker must NOT mask a hole in the installed tree (the
        // exact shape of the dropped-signatures bug on machines already stamped
        // with the old marker).
        var pack = MakePack();
        var win64 = Path.Combine(_root, "game-heal", "Solarpunk", "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        Ue4ssBootstrapService.Install(pack, win64, "1.0.0").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        var sig = Path.Combine(win64, "ue4ss", "UE4SS_Signatures", "GUObjectArray.lua");
        File.Delete(sig);

        Ue4ssBootstrapService.Install(pack, win64, "1.0.0").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        File.Exists(sig).Should().BeTrue("a missing pack file must force a re-stage even when the version marker matches");
    }

    [Fact]
    public void Install_PreservesPatchedSettingsIni_OnReinstall()
    {
        var pack = MakePack();
        File.WriteAllText(Path.Combine(pack, "UE4SS-settings.ini"), "[Overrides]\r\n");
        var win64 = Path.Combine(_root, "game-ini", "Solarpunk", "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        Ue4ssBootstrapService.Install(pack, win64, "1.0.0").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        var ini = Path.Combine(win64, "ue4ss", "UE4SS-settings.ini");
        File.WriteAllText(ini, "[Overrides]\r\n; patched-by-launcher\r\n");

        Ue4ssBootstrapService.Install(pack, win64, "1.0.1").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        File.ReadAllText(ini).Should().Contain("patched-by-launcher", "an in-place patched settings.ini must survive a version-bump re-stage");
    }

    [Fact]
    public void Install_IsIdempotent_SkipsSameVersion()
    {
        var pack = MakePack();
        var win64 = Path.Combine(_root, "game2", "Solarpunk", "Binaries", "Win64");
        Directory.CreateDirectory(win64);

        Ue4ssBootstrapService.Install(pack, win64, "1.0.0").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
        Ue4ssBootstrapService.Install(pack, win64, "1.0.0").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Skipped);
        Ue4ssBootstrapService.Install(pack, win64, "1.0.1").Status.Should().Be(Ue4ssBootstrapService.InstallStatus.Installed);
    }

    [Fact]
    public void Install_MissingPack_ReportsSourceMissing()
    {
        var win64 = Path.Combine(_root, "game3", "Win64");
        Directory.CreateDirectory(win64);
        Ue4ssBootstrapService.Install(Path.Combine(_root, "nope"), win64, "1.0.0")
            .Status.Should().Be(Ue4ssBootstrapService.InstallStatus.SourceMissing);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
