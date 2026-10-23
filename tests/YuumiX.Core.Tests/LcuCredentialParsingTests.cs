using Xunit;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>
/// lockfile 与命令行两条凭据来源的解析。
/// 这两段逻辑在原项目里与进程扫描、文件 IO 混在一起，是最难定位故障的地方，故单独成测。
/// </summary>
public sealed class LcuCredentialParsingTests
{
    [Fact]
    public void Lockfile_ValidContent_IsParsed()
    {
        var parsed = LockfileParser.TryParse("LeagueClient:1234:54321:secretToken:https", out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal("LeagueClient", entry.Name);
        Assert.Equal(1234u, entry.ProcessId);
        Assert.Equal(54321u, entry.Port);
        Assert.Equal("secretToken", entry.Password);
        Assert.Equal("https", entry.Protocol);
    }

    [Fact]
    public void Lockfile_WithoutProtocol_DefaultsToHttps()
    {
        Assert.True(LockfileParser.TryParse("LeagueClient:1234:54321:secretToken", out var entry));
        Assert.Equal("https", entry!.Protocol);
    }

    /// <summary>客户端启动瞬间文件可能只写了一半，此时必须安静地等下一轮。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LeagueClient:1234:54321")]
    [InlineData("LeagueClient:abc:54321:token:https")]
    [InlineData("LeagueClient:0:54321:token:https")]
    [InlineData("LeagueClient:1234:0:token:https")]
    [InlineData("LeagueClient:1234:70000:token:https")]
    [InlineData("LeagueClient:1234:54321::https")]
    public void Lockfile_IncompleteOrInvalid_IsRejected(string? content)
    {
        Assert.False(LockfileParser.TryParse(content, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void CommandLine_ExtractsPortTokenAndPlatform()
    {
        const string commandLine =
            "\"D:\\Game\\LeagueClientUx.exe\" " +
            "\"--riotclient-auth-token=rso-token\" " +
            "\"--riotclient-app-port=60000\" " +
            "\"--remoting-auth-token=ABCdef123\" " +
            "\"--app-port=54321\" " +
            "\"--rso_platform_id=CN1\" " +
            "\"--locale=zh_CN\"";

        var parsed = CommandLineCredentials.TryExtract(commandLine, out var port, out var token, out var server);

        Assert.True(parsed);
        Assert.Equal(54321u, port);
        Assert.Equal("ABCdef123", token);
        Assert.Equal("CN1", server);
    }

    /// <summary>
    /// <c>--riotclient-app-port=</c> 里含有 <c>-app-port=</c>，不能误把 riotclient 端口当 LCU 端口。
    /// </summary>
    [Fact]
    public void CommandLine_DoesNotConfuseRiotClientPortWithLcuPort()
    {
        const string commandLine = "\"x.exe\" --riotclient-app-port=60000 --app-port=54321 --remoting-auth-token=t";

        Assert.True(CommandLineCredentials.TryExtract(commandLine, out var port, out _, out _));
        Assert.Equal(54321u, port);
    }

    [Fact]
    public void CommandLine_MissingToken_Fails()
    {
        Assert.False(CommandLineCredentials.TryExtract("x.exe --app-port=54321", out _, out _, out _));
    }

    [Fact]
    public void CommandLine_MissingPort_Fails()
    {
        Assert.False(CommandLineCredentials.TryExtract("x.exe --remoting-auth-token=t", out _, out _, out _));
    }

    [Fact]
    public void CommandLine_WithoutPlatform_StillSucceeds()
    {
        Assert.True(CommandLineCredentials.TryExtract(
            "x.exe --app-port=54321 --remoting-auth-token=t", out _, out _, out var server));
        Assert.Null(server);
    }

    [Fact]
    public void CommandLine_EmptyInput_Fails()
    {
        Assert.False(CommandLineCredentials.TryExtract(null, out _, out _, out _));
        Assert.False(CommandLineCredentials.TryExtract("   ", out _, out _, out _));
    }
}
