using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using YuumiX.Core.Localization;

namespace YuumiX.Core.Tests;

public sealed class ExperienceLocalizationTests
{
    [Fact]
    public void ExperienceStrings_ArePresentInEveryLocaleWithMatchingPlaceholders()
    {
        string[] keys =
        [
            "career.lootNoUnowned",
            "career.lootMissingChampions",
            "career.lootInsufficientBlue",
            "career.lootInsufficientOrange",
            "career.lootUpgradeMixedDescription",
            "career.lootUpgradeDescription",
            "career.lootUnlockDescription",
            "career.lootUpgradeTitle",
            "career.lootUnlockTitle",
            "career.lootUpgradeUnlockTitle",
            "career.lootItemsToProcess",
            "career.lootKinds",
            "career.lootBlueCost",
            "career.lootOrangeCost",
            "career.lootOperationDescription",
            "career.lootUpgradeConfirm",
            "career.lootDisenchantTitle",
            "career.lootDisenchantConfirm",
            "career.lootSelectedKinds",
            "career.lootBlueReward",
            "career.lootOrangeReward",
            "career.lootRerollTitle",
            "career.lootRerollConfirm",
            "career.lootRerollGroups",
            "career.lootGroups",
            "career.lootConsumed",
            "career.lootCount",
            "career.lootRemaining",
            "career.lootOpenAllTitle",
            "career.lootOpenAllConfirm",
            "career.lootRerollInsufficient",
            "career.lootMasterworkKey",
            "career.lootHextechKey",
            "career.lootForgeTitle",
            "career.lootOpenTitle",
            "career.lootStartForge",
            "career.lootStartOpen",
            "career.lootInsufficientFragments",
            "career.lootInsufficientKeys",
            "career.lootOwned",
            "career.lootNeedsKey",
            "career.lootKeyCount",
            "career.lootForgeQuantity",
            "career.lootOpenQuantity",
            "career.lootCancel",
            "career.lootConfirm",
            "search.resolveFailed",
            "search.retry",
            "search.historyFailed",
            "search.filteredEmpty",
            "search.historyEmpty",
            "search.detailFailed",
            "search.detailLoading",
            "search.enrichmentFailed",
            "search.clearHistoryTitle",
            "search.clearHistoryMessage",
            "search.clearHistoryConfirm",
            "search.cancel",
            "search.historyLoading",
            "tools.friendScanSuccess",
            "tools.friendScanFailed",
            "tools.friendDeleteSuccess",
            "tools.friendDeleteFailed",
            "tools.httpStatusError",
            "tools.restartClient.title",
            "tools.clearAllBadgesBtn",
            "tools.clearBorderBtn",
            "tools.friendFilter180",
            "tools.friendFilter360",
            "tools.friendFilter90",
            "tools.friendFilterNever",
            "tools.friendFilterHidden",
            "tools.friendFilterAll",
            "tools.friendSearchPlaceholder",
            "tools.friendScanning",
            "tools.friendRescan",
            "tools.friendSearchAutomation",
            "tools.friendFilterAutomation",
            "tools.friendSelectAll",
            "tools.friendSelectAllAutomation",
            "tools.friendDeselect",
            "tools.friendDeselectAutomation",
            "tools.friendDeleting",
            "tools.friendDeleteSelected",
            "tools.friendDeleteAutomation",
            "tools.friendFilteredCount",
            "tools.friendDeleteTitle",
            "tools.friendDeletePrompt",
            "tools.friendDeleteConfirm",
            "tools.restartAutomation",
            "tools.clearBadgesAutomation",
            "tools.clearBorderAutomation",
            "tools.clientDisconnected",
            "tools.restartSuccess",
            "tools.operationFailed",
            "tools.clearBadgesSuccess",
            "tools.clearBadgesFailed",
            "tools.clearBorderSuccess",
            "tools.clearBorderFailed",
            "tools.restartConfirmTitle",
            "tools.restartConfirmPrompt",
            "tools.restartConfirmAction",
            "tools.clearBadgesTitle",
            "tools.clearBadgesPrompt",
            "tools.clearConfirmAction",
            "tools.clearBorderTitle",
            "tools.clearBorderPrompt",
            "savedPlayersPage.deleteConfirmTitle",
            "savedPlayersPage.deleteConfirmMessage",
            "savedPlayersPage.deleteConfirmButton",
            "savedPlayersPage.cancel",
        ];
        using var zhCn = LoadLocale(SupportedLanguages.ZhCn);
        using var zhTw = LoadLocale(SupportedLanguages.ZhTw);
        using var enUs = LoadLocale(SupportedLanguages.EnUs);

        foreach (string key in keys)
        {
            string simplified = ReadValue(zhCn, key);
            string traditional = ReadValue(zhTw, key);
            string english = ReadValue(enUs, key);
            Assert.Equal(Placeholders(simplified), Placeholders(traditional));
            Assert.Equal(Placeholders(simplified), Placeholders(english));
            Assert.DoesNotMatch(@"[\u4e00-\u9fff]", english);
        }
    }

    private static JsonDocument LoadLocale(string language)
    {
        var assembly = typeof(I18nService).Assembly;
        string fileName = SupportedLanguages.ToResourceFileName(language);
        string resourceName = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream);
    }

    private static string ReadValue(JsonDocument document, string key)
    {
        JsonElement current = document.RootElement;
        foreach (string part in key.Split('.'))
        {
            Assert.True(current.TryGetProperty(part, out var next), $"Missing translation: {key}");
            current = next;
        }

        Assert.Equal(JsonValueKind.String, current.ValueKind);
        string value = Assert.IsType<string>(current.GetString());
        Assert.False(string.IsNullOrWhiteSpace(value), $"Empty translation: {key}");
        return value;
    }

    private static string[] Placeholders(string value) =>
        Regex.Matches(value, @"\{[a-zA-Z][a-zA-Z0-9_]*\}")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}
