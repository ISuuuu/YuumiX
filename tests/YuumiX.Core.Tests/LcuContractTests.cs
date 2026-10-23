using Xunit;
using YuumiX.Core.Lcu;

namespace YuumiX.Core.Tests;

/// <summary>LCU 契约层：凭据编码、路径白名单、请求约束。</summary>
public sealed class LcuContractTests
{
    [Fact]
    public void Credentials_BuildBasicAuthHeaderWithRiotUser()
    {
        var credentials = new LcuCredentials(port: 50000, token: "abc123");

        // "riot:abc123" 的 Base64
        Assert.Equal("Basic cmlvdDphYmMxMjM=", credentials.AuthorizationHeader);
        Assert.Equal("https://127.0.0.1:50000/", credentials.BaseAddress.ToString());
    }

    [Fact]
    public void Credentials_RejectInvalidPortOrToken()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuCredentials(0, "t"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LcuCredentials(65536, "t"));
        Assert.Throws<ArgumentException>(() => new LcuCredentials(50000, "  "));
    }

    /// <summary>诊断输出不得泄露 token——日志会被用户上传排障。</summary>
    [Fact]
    public void Credentials_ToStringNeverLeaksToken()
    {
        var credentials = new LcuCredentials(50000, "super-secret-token", processId: 1234, server: "CN1");

        var text = credentials.ToString();

        Assert.DoesNotContain("super-secret-token", text);
        Assert.Contains("pid=1234", text);
        Assert.Contains("port=50000", text);
        Assert.Contains("CN1", text);
    }

    [Theory]
    [InlineData("/lol-summoner/v1/current-summoner", true)]
    [InlineData("/system/v1/builds", true)]
    [InlineData("/riotclient/region-locale", true)]
    [InlineData("/lol-champ-select/v1/session", true)]
    [InlineData("/lol-summoner", false)] // 少了结尾斜杠，前缀不匹配
    [InlineData("/LOL-SUMMONER/v1/x", false)] // 大小写敏感
    [InlineData("/lol-login/v1/x", true)]
    [InlineData("/arbitrary/v1/x", false)]
    [InlineData("lol-summoner/v1/x", false)] // 非绝对路径
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ApiCatalog_AllowsOnlyWhitelistedPrefixes(string? path, bool expected)
    {
        Assert.Equal(expected, LcuApiCatalog.IsAllowed(path));
    }

    [Fact]
    public void ApiCatalog_ReadinessPathIsWhitelisted()
    {
        Assert.True(LcuApiCatalog.IsAllowed(LcuApiCatalog.ReadinessPath));
    }

    [Fact]
    public void Request_RequiresAbsolutePath()
    {
        Assert.Throws<ArgumentException>(() => new LcuRequest(HttpMethod.Get, "lol-summoner/v1/x"));
        Assert.Throws<ArgumentException>(() => new LcuRequest(HttpMethod.Get, "  "));
    }

    [Fact]
    public void Request_FactoriesProduceExpectedShape()
    {
        Assert.Equal(HttpMethod.Post, LcuRequest.Post("/lol-lobby/v2/lobby", "{}").Method);
        Assert.Equal("{}", LcuRequest.Put("/lol-login/v1/x", "{}").JsonBody);
        Assert.Null(LcuRequest.Delete("/lol-lobby/v2/lobby").JsonBody);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    public void Response_ClassifiesStatus(int status, bool isSuccess)
    {
        var response = new LcuResponse(status, string.Empty);

        Assert.Equal(isSuccess, response.IsSuccess);
        Assert.Equal(status == 404, response.IsNotFound);
    }
}
