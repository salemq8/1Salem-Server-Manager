using ServerManager.Contracts;

namespace ServerManager.Core.Tests;

public sealed class Version120PolicyTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.1", 1)]
    [InlineData("1.2.0", "1.2.0-beta.9", 1)]
    [InlineData("v1.2.0+local", "1.2.0", 0)]
    public void SemanticVersion_UsesSemverOrdering(
        string left,
        string right,
        int expectedSign)
    {
        var comparison = SemanticVersion.Parse(left).CompareTo(
            SemanticVersion.Parse(right));

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public void PlayitOutput_DetectsOfficialClaimAndVerifiedState()
    {
        const string line =
            "Open https://playit.gg/claim/AbC_123 to finish setting up playit";

        Assert.Equal(
            "https://playit.gg/claim/AbC_123",
            PlayitOutputParser.FindClaimUrl(line));
        Assert.True(PlayitOutputParser.IndicatesVerified("agent registered; tunnel running"));
    }

    [Fact]
    public void PlayitOutput_RedactsSecretsAndClaimLinks()
    {
        var secret = new string('A', 48);
        var redacted = PlayitOutputParser.Redact(
            $"secret={secret} https://playit.gg/claim/ABC123");

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC123", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void MinecraftMapping_AcceptsTcpConfiguredPort()
    {
        var error = PlayitTunnelPolicy.Validate(
            GameType.Minecraft,
            PlayitTunnelProtocol.Tcp,
            "127.0.0.1",
            25565,
            25565);

        Assert.Null(error);
    }

    [Fact]
    public void PalworldMapping_AcceptsUdp8211()
    {
        var error = PlayitTunnelPolicy.Validate(
            GameType.Palworld,
            PlayitTunnelProtocol.Udp,
            "127.0.0.1",
            8211,
            8211);

        Assert.Null(error);
    }

    [Theory]
    [InlineData(GameType.Palworld, PlayitTunnelProtocol.Udp, 25565, 8211)]
    [InlineData(GameType.Palworld, PlayitTunnelProtocol.Tcp, 8211, 8211)]
    [InlineData(GameType.Minecraft, PlayitTunnelProtocol.Tcp, 8211, 25565)]
    public void TunnelMapping_RejectsWrongGamePortOrProtocol(
        GameType game,
        PlayitTunnelProtocol protocol,
        int localPort,
        int configuredPort)
    {
        Assert.NotNull(PlayitTunnelPolicy.Validate(
            game,
            protocol,
            "127.0.0.1",
            localPort,
            configuredPort));
    }

    [Fact]
    public void TunnelMapping_RejectsDuplicateGameTarget()
    {
        var existing = new[]
        {
            new PlayitTunnelStatus(
                GameType.Minecraft,
                PlayitTunnelProtocol.Tcp,
                "127.0.0.1",
                25565,
                "example.joinmc.link",
                true,
                true,
                null)
        };

        var error = PlayitTunnelPolicy.Validate(
            GameType.Palworld,
            PlayitTunnelProtocol.Udp,
            "127.0.0.1",
            25565,
            25565,
            existing);

        Assert.NotNull(error);
    }

    [Fact]
    public void RunningGamePolicy_RequiresApprovalOnlyForServiceRestart()
    {
        Assert.False(ApplicationUpdatePolicy.CanInstall(true, false, true));
        Assert.True(ApplicationUpdatePolicy.CanInstall(true, true, true));
        Assert.True(ApplicationUpdatePolicy.CanInstall(true, false, false));
    }
}
