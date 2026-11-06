using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using YuumiX.Core.Automation.Models;
using YuumiX.Core.Features.GameInfo.Models;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Features.SavedPlayers.Models;
using YuumiX.Core.Features.Tools.Models;
using YuumiX.Core.Persistence;
using YuumiX.Core.Updates;
using YuumiX.Core.Upload;

namespace YuumiX.Core.Serialization;

/// <summary>
/// Native AOT / 裁剪环境下的 JSON 源生成元数据总表。
///
/// 裁剪后的应用默认关闭反射序列化（IsReflectionEnabledByDefault=false），
/// System.Text.Json 的反射路径在 AOT 成品里会直接不可用。因此所有参与
/// 序列化的类型必须在此登记，由各 <c>JsonSerializerOptions</c> 把
/// <see cref="JsonSerializerOptions.TypeInfoResolver"/> 指向
/// <see cref="CoreJsonContext.Default"/>，调用点经
/// <see cref="AotJson.GetTypeInfo{T}(JsonSerializerOptions)"/> 取强类型元数据。
///
/// 未登记的类型不会在编译期报错，而是在运行时抛「缺少元数据」异常——
/// 新增序列化类型时务必同步登记，并跑一遍相关回归（见 2026-11-01-native-aot-requirements.md）。
/// </summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ChampSelectSession))]
[JsonSerializable(typeof(List<LiveClientPlayer>))]
[JsonSerializable(typeof(List<LcuFriend>))]
[JsonSerializable(typeof(SavedPlayerRecord))]
[JsonSerializable(typeof(List<SavedPlayerRecord>))]
[JsonSerializable(typeof(UploadPayload))]
[JsonSerializable(typeof(List<UploadPayload>))]
[JsonSerializable(typeof(UpdateService.ReleaseEntry))]
[JsonSerializable(typeof(List<UpdateService.ReleaseEntry>))]
[JsonSerializable(typeof(DownloadCheckpoint))]
[JsonSerializable(typeof(Features.Tools.ChampionAliasProvider.CacheDocument))]
[JsonSerializable(typeof(OpggTierPayload))]
[JsonSerializable(typeof(OpggBuildPayload))]
[JsonSerializable(typeof(List<PerkStyle>))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>
/// AOT 安全的共享 <see cref="JsonSerializerOptions"/> 与元数据获取扩展。
/// options 的序列化设置（命名策略、缩进、编码器、自定义转换器）由使用方保持不变，
/// 这里只负责挂上源生成解析器并统一取元数据的入口。
/// </summary>
public static class AotJson
{
    /// <summary>默认设置的 AOT 安全选项（替代直接使用 JsonSerializer.Serialize 的无 options 重载）。</summary>
    public static JsonSerializerOptions Options { get; } = Bind(new JsonSerializerOptions());

    /// <summary>Web 默认（camelCase、大小写不敏感、数字容忍字符串）的 AOT 安全选项，供上传载荷等 HTTP 路径使用。</summary>
    public static JsonSerializerOptions WebOptions { get; } = Bind(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static JsonSerializerOptions Bind(JsonSerializerOptions options)
    {
        options.TypeInfoResolver = CoreJsonContext.Default;
        return options;
    }

    /// <summary>
    /// 取强类型序列化元数据。类型未在 <see cref="CoreJsonContext"/> 登记时
    /// 运行时直接抛异常（非 AOT 环境下同样如此，便于测试期暴露）。
    /// </summary>
    public static JsonTypeInfo<T> GetTypeInfo<T>(this JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
