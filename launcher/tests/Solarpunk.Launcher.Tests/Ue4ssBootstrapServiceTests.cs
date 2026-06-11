using FluentAssertions;
using Solarpunk.Launcher.Services;
using Xunit;

namespace Solarpunk.Launcher.Tests;

// The +ModsFolderPaths INI rewrite is carried over verbatim from Beacon; keep its
// round-trip + idempotency guarantees under test (renamed markers).
public class Ue4ssBootstrapServiceTests
{
    [Fact]
    public void Rewrite_AddsManagedBlock_WhenNoOverridesSection()
    {
        var result = Ue4ssBootstrapService.Rewrite("[General]\nbUseUObjectArrayCache = true\n", @"C:\Solarpunk\mods\srv");
        result.Should().Contain("[Overrides]");
        result.Should().Contain("+ModsFolderPaths = C:/Solarpunk/mods/srv");
        result.Should().Contain("SOLARPUNK-MANAGED");
    }

    [Fact]
    public void Rewrite_IsIdempotent()
    {
        var once = Ue4ssBootstrapService.Rewrite("[General]\n", @"C:\a\b");
        var twice = Ue4ssBootstrapService.Rewrite(once, @"C:\a\b");
        twice.Should().Be(once);
    }

    [Fact]
    public void Rewrite_NullPath_StripsManagedBlock()
    {
        var withBlock = Ue4ssBootstrapService.Rewrite("[General]\n", @"C:\a\b");
        var cleared = Ue4ssBootstrapService.Rewrite(withBlock, null);
        cleared.Should().NotContain("SOLARPUNK-MANAGED");
        cleared.Should().NotContain("+ModsFolderPaths");
    }

    [Fact]
    public void Rewrite_SwitchingPaths_LeavesSinglePath()
    {
        var first = Ue4ssBootstrapService.Rewrite("[General]\n", @"C:\srv\one");
        var second = Ue4ssBootstrapService.Rewrite(first, @"C:\srv\two");
        second.Split('\n').Count(l => l.Contains("+ModsFolderPaths")).Should().Be(1);
        second.Should().Contain("+ModsFolderPaths = C:/srv/two");
    }
}
