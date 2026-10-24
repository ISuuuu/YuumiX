using System.Text.Json;
using YuumiX.Core.Automation;
using YuumiX.Core.Common;
using Xunit;

namespace YuumiX.Core.Tests;

/// <summary>
/// 多杀截图的事件匹配验收（requirements-settings.md §5：killer 名 #tag/大小写容错、
/// 等级白名单、Legendary 容错字段、文件名规则、保存目录解析）。
/// </summary>
public sealed class LiveEventMatcherTests
{
    private const string SampleEvents = """
        {"Events":[
            {"EventID":1,"EventName":"GameStart","EventTime":0.0},
            {"EventID":2,"EventName":"Multikill","EventTime":63.5,"KillerName":"Hide#KR1","KillStreak":3},
            {"EventID":3,"EventName":"Multikill","EventTime":120.1,"KillerName":"someoneelse#EUW","KillStreak":4},
            {"EventID":4,"EventName":"Legendary","EventTime":900.0,"LegendaryTo":"Hide on bush#KR1"}
        ]}
        """;

    [Fact]
    public void ParseEvents_ReadsAllFields()
    {
        var events = LiveEventMatcher.ParseEvents(SampleEvents);

        Assert.Equal(4, events.Count);
        Assert.Equal("GameStart", events[0].EventName);
        Assert.Equal(2, events[1].EventId);
        Assert.Equal("Hide#KR1", events[1].KillerName);
        Assert.Equal(3, events[1].KillStreak);
        Assert.Null(events[0].KillStreak);
        Assert.Equal("Hide on bush#KR1", events[3].LegendaryTo);
    }

    [Fact]
    public void ParseEvents_InvalidPayload_ReturnsEmpty()
    {
        Assert.Empty(LiveEventMatcher.ParseEvents("not json"));
        Assert.Empty(LiveEventMatcher.ParseEvents("{}"));
        Assert.Empty(LiveEventMatcher.ParseEvents("""{"Events":null}"""));
    }

    // ── 玩家名比较（#tag 与大小写忽略）─────────────────────────

    [Theory]
    [InlineData("Hide#KR1", "hide", true)]
    [InlineData("Hide on bush", "HIDE ON BUSH", true)]
    [InlineData("Hide#KR1", "Hide#EUW", true)]  // #tag 段被忽略
    [InlineData("Hide", "Ghide", false)]
    [InlineData("Hide", null, false)]
    [InlineData(null, "Hide", false)]
    public void CompareNames_IgnoresTagAndCase(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, LiveEventMatcher.CompareNames(a, b));
    }

    // ── 截图触发判定 ────────────────────────────────────────────

    private static readonly IReadOnlyCollection<int> DefaultLevels = [3, 4, 5, 8];

    [Fact]
    public void ShouldCapture_Multikill_InLevels_MatchesKiller()
    {
        var evt = new LiveEventMatcher.LiveEvent(10, "Multikill", "Hide#KR1", null, null, 3);

        Assert.True(LiveEventMatcher.ShouldCapture(evt, "hide", DefaultLevels, out var reason));
        Assert.Equal("TripleKill", reason);
    }

    [Fact]
    public void ShouldCapture_Multikill_LevelNotConfigured_DoesNotCapture()
    {
        // 6 连杀不在配置等级里
        var evt = new LiveEventMatcher.LiveEvent(11, "Multikill", "Hide", null, null, 6);

        Assert.False(LiveEventMatcher.ShouldCapture(evt, "Hide", DefaultLevels, out _));
    }

    [Fact]
    public void ShouldCapture_Multikill_KillerIsOther_DoesNotCapture()
    {
        var evt = new LiveEventMatcher.LiveEvent(12, "Multikill", "someoneelse#EUW", null, null, 4);

        Assert.False(LiveEventMatcher.ShouldCapture(evt, "Hide", DefaultLevels, out _));
    }

    [Fact]
    public void ShouldCapture_Legendary_RequiresLevel8_AndFallsBackAcrossFields()
    {
        // LegendaryTo 优先
        var legendaryTo = new LiveEventMatcher.LiveEvent(13, "Legendary", null, "Hide#KR1", null, null);
        Assert.True(LiveEventMatcher.ShouldCapture(legendaryTo, "hide", DefaultLevels, out var reason1));
        Assert.Equal("Legendary", reason1);

        // LegendaryTo 缺失时回退 KillerName
        var killerName = new LiveEventMatcher.LiveEvent(14, "Legendary", "Hide", null, "someoneelse", null);
        Assert.True(LiveEventMatcher.ShouldCapture(killerName, "hide", DefaultLevels, out _));

        // levels 不含 8 → 不触发
        var levels = new List<int> { 3 };
        Assert.False(LiveEventMatcher.ShouldCapture(legendaryTo, "hide", levels, out _));
    }

    [Fact]
    public void ShouldCapture_UnrelatedEvent_DoesNotCapture()
    {
        var evt = new LiveEventMatcher.LiveEvent(15, "ChampionKill", "Hide", null, null, null);

        Assert.False(LiveEventMatcher.ShouldCapture(evt, "Hide", DefaultLevels, out _));
    }

    // ── 文件名与目录 ────────────────────────────────────────────

    [Fact]
    public void BuildFileName_FollowsOldFormat_AndKeepsTagSegment()
    {
        // 旧实现原样保留 #tag 段（Windows 文件名允许 #）
        string name = LiveEventMatcher.BuildFileName("TripleKill", "Hide#KR1", new DateTime(2026, 10, 24, 9, 30, 5));

        Assert.Equal("TripleKill_Hide#KR1_20261024_093005.png", name);
    }

    [Fact]
    public void ResolveSaveDirectory_CustomPathWins()
    {
        Assert.Equal(@"D:\shots", LiveEventMatcher.ResolveSaveDirectory(@"D:\shots"));
    }

    [Fact]
    public void ResolveSaveDirectory_Empty_FallsBackToPictures()
    {
        string result = LiveEventMatcher.ResolveSaveDirectory(string.Empty);

        if (string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))
        {
            Assert.EndsWith("Yuumi_Screenshots", result, StringComparison.Ordinal);
        }
        else
        {
            Assert.EndsWith(
                Path.Combine("Pictures", "Yuumi_Screenshots").TrimStart('\\'),
                result,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>纯托管 PNG 编码器的结构验收（CRC32 已知向量 + 输出字节结构）。</summary>
public sealed class PngEncoderTests
{
    [Fact]
    public void Crc32_MatchesKnownVectors()
    {
        // CRC-32("123456789") = 0xCBF43926（IEEE 802.3 标准测试向量）
        Assert.Equal(0xCBF43926u, PngEncoder.Crc32("123456789"u8));
        Assert.Equal(0x00000000u, PngEncoder.Crc32([]));
    }

    [Fact]
    public void Adler32_MatchesKnownVector()
    {
        // Adler-32("Wikipedia") = 0x11E60398
        Assert.Equal(0x11E60398u, PngEncoder.Adler32("Wikipedia"u8));
    }

    [Fact]
    public void Encode_ProducesValidPngStructure()
    {
        // 2x1 红蓝像素
        byte[] bgra = [0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF];

        byte[] png = PngEncoder.Encode(bgra, 2, 1, 8);

        // 签名
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        // IHDR 长度固定 13，紧跟类型
        Assert.Equal(13u, ReadU32(png, 8));
        Assert.Equal("IHDR"u8.ToArray(), png[12..16]);

        // IHDR 内容：宽 2、高 1、位深 8、RGBA、无隔行
        Assert.Equal(2u, ReadU32(png, 16));
        Assert.Equal(1u, ReadU32(png, 20));
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);
        Assert.Equal(0, png[27]);

        // 尾部是 IEND 块（长度 0）
        int iendTypeOffset = png.Length - 8;
        Assert.Equal("IEND"u8.ToArray(), png[iendTypeOffset..(iendTypeOffset + 4)]);
        Assert.Equal(0u, ReadU32(png, png.Length - 12));
    }

    [Fact]
    public void Encode_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode([], 0, 1, 0));
        var pixels = new byte[8];
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(pixels, 2, 1, 4)); // stride < 8
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(pixels, 4, 4, 16)); // 缓冲区太小
    }

    private static uint ReadU32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
