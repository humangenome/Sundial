using FluentAssertions;
using Xunit;

namespace Solarpunk.Launcher.Tests;

// The Mods tab must never surface the internal connect transport
// (solarpunk-connect): it's launcher plumbing — bundled with the installer and
// auto-staged by ConnectFlow on every connect — not a mod the player chooses.
// Beacon gives its beacon-connect transport the same treatment (never shown in
// the user-facing list). These tests pin the display filter; the install
// pipeline is untouched by it.
public class ModsViewFilterTests
{
    private static SolarpunkHttpClient.ManifestModEntry Entry(string id, string name = "") =>
        new(id, name, "1.0.0", "", "", "Mods/" + id, "");

    [Theory]
    [InlineData("solarpunk-connect")]
    [InlineData("Solarpunk-Connect")]
    [InlineData("SOLARPUNK-CONNECT")]
    [InlineData("SolarpunkConnect")]
    public void IsInternalMod_MatchesConnectTransport_AnyCasing(string id)
    {
        MainWindow.IsInternalMod(Entry(id, "Solarpunk Connect")).Should().BeTrue();
    }

    [Theory]
    [InlineData("solarpunk-hud")]
    [InlineData("better-farming")]
    [InlineData("")]
    public void IsInternalMod_LeavesRealModsAlone(string id)
    {
        MainWindow.IsInternalMod(Entry(id)).Should().BeFalse();
    }

    [Fact]
    public void FilterInternalMods_DropsConnect_KeepsGameplayMods()
    {
        var list = new List<SolarpunkHttpClient.ManifestModEntry>
        {
            Entry("solarpunk-connect", "Solarpunk Connect"),
            Entry("better-farming", "Better Farming"),
        };

        var visible = MainWindow.FilterInternalMods(list);

        visible.Should().ContainSingle(e => e.Id == "better-farming");
    }

    [Fact]
    public void FilterInternalMods_TypicalServer_OnlyConnect_IsEmpty()
    {
        // The shipping manifest for a stock server lists ONLY the connect
        // transport — the customer-facing list must come back empty so the
        // Required section hides itself.
        var list = new List<SolarpunkHttpClient.ManifestModEntry>
        {
            Entry("solarpunk-connect", "Solarpunk Connect"),
        };

        MainWindow.FilterInternalMods(list).Should().BeEmpty();
    }

    [Fact]
    public void FilterInternalMods_NullList_IsEmptyNotNull()
    {
        MainWindow.FilterInternalMods(null).Should().NotBeNull().And.BeEmpty();
    }
}
