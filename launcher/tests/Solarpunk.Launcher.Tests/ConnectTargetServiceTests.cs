using FluentAssertions;
using Solarpunk.Launcher.Services;
using Xunit;

namespace Solarpunk.Launcher.Tests;

// The connect-target parse is the contract between what the player types and the
// "<ip>:<port>" line the SolarpunkConnect mod reads — exercise its edges.
public class ConnectTargetServiceTests
{
    [Theory]
    [InlineData("123.45.67.89:7777", "123.45.67.89", 7777)]
    [InlineData(" 10.0.0.1:28015 ", "10.0.0.1", 28015)]
    [InlineData("play.example.com:7780", "play.example.com", 7780)]
    [InlineData("solarpunk://10.0.0.1:9000", "10.0.0.1", 9000)]
    public void Parse_HostPort(string input, string host, int port)
    {
        var t = ConnectTargetService.Parse(input);
        t.Should().NotBeNull();
        t!.Host.Should().Be(host);
        t.Port.Should().Be(port);
        t.Address.Should().Be($"{host}:{port}");
    }

    [Fact]
    public void Parse_BareHost_UsesDefaultPort()
    {
        var t = ConnectTargetService.Parse("198.51.100.7");
        t.Should().NotBeNull();
        t!.Port.Should().Be(7777);
    }

    [Fact]
    public void Parse_BracketedIpv6()
    {
        var t = ConnectTargetService.Parse("[::1]:7777");
        t.Should().NotBeNull();
        t!.Host.Should().Be("::1");
        t.Port.Should().Be(7777);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:notaport")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    public void Parse_Rejects_Garbage(string input)
    {
        ConnectTargetService.Parse(input).Should().BeNull();
    }
}

// The character-identity contract: the launcher's synthetic SteamID64 must be
// deterministic + match the server-side hook's formula (base + CRC32(lowercase
// name)), and the connect line must carry the ?Name= option the host's
// InitNewPlayer hook reads. Vectors computed with zlib.crc32 (IEEE).
public class CharacterIdentityTests
{
    [Theory]
    [InlineData("hello", 0x3610a686u, 76561190907060870UL)]
    [InlineData("PlayerOne", 0x85cbb61eu, 76561192244720158UL)]    // lowercased before hashing
    [InlineData("HumanGenome56", 0x164e627eu, 76561190374235774UL)]
    public void SyntheticId_Matches_KnownCrc32Vectors(string name, uint crc, ulong id64)
    {
        ConnectTargetService.Crc32(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            .Should().Be(crc);
        ConnectTargetService.SyntheticSteamId64(name).Should().Be(id64);
        ConnectTargetService.CharacterHash8(name).Should().Be(crc.ToString("x8"));
    }

    [Fact]
    public void SyntheticId_IsSteamShaped()
    {
        // 7656119xxxxxxxxxx — 17 digits, valid SteamID64 space, even for the
        // max CRC32 value.
        (ConnectTargetService.SyntheticSteamIdBase + uint.MaxValue)
            .ToString().Length.Should().Be(17);
        ConnectTargetService.SyntheticSteamId64("x").ToString().Should().StartWith("7656119");
    }

    [Theory]
    [InlineData("  Player One ", "PlayerOne")]                  // whitespace stripped
    [InlineData("a?b=c&d/e\\f:g", "abcdefg")]                 // URL-option breakers stripped
    [InlineData("Práxis_42", "Práxis_42")]                    // unicode + underscore kept
    public void SanitizeCharacterName_StripsUrlBreakers(string raw, string expected)
    {
        ConnectTargetService.SanitizeCharacterName(raw).Should().Be(expected);
    }

    [Fact]
    public void SanitizeCharacterName_CapsAt32()
    {
        ConnectTargetService.SanitizeCharacterName(new string('a', 64)).Should().HaveLength(32);
    }

    [Fact]
    public void ConnectLine_CarriesNameOption()
    {
        new ConnectTargetService.Target("203.0.113.10", 7777, "PlayerOne")
            .ConnectLine.Should().Be("203.0.113.10:7777?Name=PlayerOne");
        new ConnectTargetService.Target("203.0.113.10", 7777)
            .ConnectLine.Should().Be("203.0.113.10:7777");
    }

    [Fact]
    public void ConnectLine_RidesJoinPasswordInNameChannel()
    {
        // Join password rides "<char>__SPPW__<pw>" (?Password= is unreadable
        // server-side; SolarpunkAuth strips the token). Case-sensitive, no trim.
        new ConnectTargetService.Target("203.0.113.10", 7777, "PlayerOne", "sptest123")
            .ConnectLine.Should().Be("203.0.113.10:7777?Name=PlayerOne__SPPW__sptest123");
        // empty/whitespace password => no token appended
        new ConnectTargetService.Target("203.0.113.10", 7777, "PlayerOne", "")
            .ConnectLine.Should().Be("203.0.113.10:7777?Name=PlayerOne");
        new ConnectTargetService.Target("203.0.113.10", 7777, "PlayerOne", null)
            .ConnectLine.Should().Be("203.0.113.10:7777?Name=PlayerOne");
        // URL-structural chars stripped from the password so option parsing survives
        new ConnectTargetService.Target("203.0.113.10", 7777, "PlayerOne", "a=b&c?d")
            .ConnectLine.Should().Be("203.0.113.10:7777?Name=PlayerOne__SPPW__abcd");
    }
}
