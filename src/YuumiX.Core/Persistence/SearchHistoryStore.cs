using System.Text.Json;
using Microsoft.Extensions.Logging;
using YuumiX.Core.Runtime;

namespace YuumiX.Core.Persistence;

/// <summary>
/// 召唤师搜索历史持久化存储（对应原版的 <c>localStorage.getItem("yuumi_search_history")</c>）。
/// 存储于 &lt;DataDirectory&gt;/search_history.json，最多保留 20 条，去重后新搜索居前。
/// </summary>
public sealed class SearchHistoryStore
{
    public const int MaxHistoryCount = 20;

    private readonly string _filePath;
    private readonly ILogger<SearchHistoryStore>? _logger;
    private readonly Lock _gate = new();
    private List<string>? _cached;

    public SearchHistoryStore(RuntimePaths paths, ILogger<SearchHistoryStore>? logger = null)
        : this(Path.Combine(paths.DataDirectory, "search_history.json"), logger)
    {
    }

    public SearchHistoryStore(string filePath, ILogger<SearchHistoryStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached.ToArray();
            }
        }

        if (!File.Exists(_filePath))
        {
            lock (_gate)
            {
                _cached = [];
                return [];
            }
        }

        try
        {
            await using FileStream stream = File.OpenRead(_filePath);
            List<string>? list = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _cached = list ?? [];
                return _cached.ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取搜索历史失败，重置为空");
            lock (_gate)
            {
                _cached = [];
                return [];
            }
        }
    }

    public async Task AddAsync(string query, CancellationToken cancellationToken = default)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        List<string> list;
        lock (_gate)
        {
            _cached ??= LoadSync();
            _cached.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
            _cached.Insert(0, trimmed);
            if (_cached.Count > MaxHistoryCount)
            {
                _cached.RemoveRange(MaxHistoryCount, _cached.Count - MaxHistoryCount);
            }
            list = [.. _cached];
        }

        await SaveAsync(list, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string query, CancellationToken cancellationToken = default)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        List<string> list;
        lock (_gate)
        {
            _cached ??= LoadSync();
            _cached.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
            list = [.. _cached];
        }

        await SaveAsync(list, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _cached = [];
        }

        await SaveAsync([], cancellationToken).ConfigureAwait(false);
    }

    private List<string> LoadSync()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task SaveAsync(List<string> items, CancellationToken cancellationToken)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = _filePath + ".tmp";
            await using (FileStream stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, items, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempFile, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "保存搜索历史失败");
        }
    }
}
