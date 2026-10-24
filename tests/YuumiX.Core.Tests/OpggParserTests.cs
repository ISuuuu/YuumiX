using Xunit;
using YuumiX.Core.Features.Opgg.Models;
using YuumiX.Core.Parsers;

namespace YuumiX.Core.Tests;

public sealed class OpggParserTests
{
    [Fact]
    public void ParseTierList_RankedMode_FiltersByPositionAndSortsByRank()
    {
        string json = """
        {
            "meta": { "version": "14.20.1" },
            "data": [
                {
                    "id": 103,
                    "name": "Ahri",
                    "positions": [
                        {
                            "name": "MID",
                            "stats": {
                                "win_rate": 0.521,
                                "pick_rate": 0.125,
                                "ban_rate": 0.045,
                                "kda": 3.15,
                                "tier_data": { "tier": "1", "rank": 2 },
                                "rank": 2
                            },
                            "counters": [
                                { "champion_id": 84 },
                                { "champion_id": 238 }
                            ]
                        },
                        {
                            "name": "TOP",
                            "stats": {
                                "win_rate": 0.48,
                                "pick_rate": 0.01,
                                "ban_rate": 0.01,
                                "kda": 2.1,
                                "rank": 40
                            }
                        }
                    ]
                },
                {
                    "id": 157,
                    "name": "Yasuo",
                    "positions": [
                        {
                            "name": "MID",
                            "stats": {
                                "win_rate": 0.505,
                                "pick_rate": 0.150,
                                "ban_rate": 0.180,
                                "kda": 2.25,
                                "tier": "2",
                                "rank": 1
                            },
                            "counters": [
                                { "champion_id": 103 }
                            ]
                        }
                    ]
                },
                {
                    "id": 1,
                    "name": "Annie",
                    "positions": [
                        {
                            "name": "SUPPORT",
                            "stats": { "win_rate": 0.49, "rank": 10 }
                        }
                    ]
                }
            ]
        }
        """;

        var list = OpggParser.ParseTierList(json, OpggMode.Ranked, OpggPosition.Mid);

        Assert.Equal(2, list.Count);
        // Sorted by Rank: Yasuo (1), then Ahri (2)
        Assert.Equal(157, list[0].Id);
        Assert.Equal("Yasuo", list[0].Name);
        Assert.Equal("2", list[0].Tier);
        Assert.Equal(1, list[0].Rank);
        Assert.Single(list[0].Counters);
        Assert.Equal(103, list[0].Counters[0]);

        Assert.Equal(103, list[1].Id);
        Assert.Equal("Ahri", list[1].Name);
        Assert.Equal("1", list[1].Tier);
        Assert.Equal(2, list[1].Rank);
        Assert.Equal(2, list[1].Counters.Count);
        Assert.Equal(84, list[1].Counters[0]);
    }

    [Fact]
    public void ParseTierList_NonRankedMode_UsesAverageStatsAndSortsByRank()
    {
        string json = """
        {
            "meta": { "version": "14.20.1" },
            "data": [
                {
                    "id": 103,
                    "name": "Ahri",
                    "average_stats": {
                        "win_rate": 0.53,
                        "pick_rate": 0.08,
                        "kda": 3.4,
                        "tier": "1",
                        "rank": 5
                    }
                },
                {
                    "id": 11,
                    "name": "MasterYi",
                    "average_stats": {
                        "win_rate": 0.55,
                        "pick_rate": 0.12,
                        "kda": 2.8,
                        "tier_data": { "tier": "0", "rank": 1 }
                    }
                }
            ]
        }
        """;

        var list = OpggParser.ParseTierList(json, OpggMode.Aram);

        Assert.Equal(2, list.Count);
        // MasterYi rank 1, Ahri rank 5
        Assert.Equal(11, list[0].Id);
        Assert.Equal("MasterYi", list[0].Name);
        Assert.Equal("0", list[0].Tier);
        Assert.Equal(1, list[0].Rank);

        Assert.Equal(103, list[1].Id);
        Assert.Equal("Ahri", list[1].Name);
        Assert.Equal("1", list[1].Tier);
        Assert.Equal(5, list[1].Rank);
    }

    [Fact]
    public void ParseBuildData_ExtractsFullBuildStructure()
    {
        string json = """
        {
            "meta": { "version": "14.20.1" },
            "data": {
                "summary": {
                    "id": 103,
                    "name": "Ahri",
                    "positions": [
                        {
                            "name": "MID",
                            "stats": { "win_rate": 0.51, "pick_rate": 0.1, "ban_rate": 0.05, "tier": "1" }
                        }
                    ]
                },
                "counters": [
                    { "champion_id": 84, "name": "Akali", "play": 100, "win": 60 },
                    { "champion_id": 238, "name": "Zed", "play": 100, "win": 40 }
                ],
                "runes": [
                    {
                        "primary_page_id": 8100,
                        "secondary_page_id": 8200,
                        "primary_rune_ids": [8112, 8126, 8138, 8135],
                        "secondary_rune_ids": [8226, 8210],
                        "stat_mod_ids": [5008, 5008, 5002],
                        "win": 520,
                        "play": 1000,
                        "pick_rate": 0.65
                    }
                ],
                "skills": [
                    {
                        "order": ["Q", "W", "E", "Q", "Q", "R"],
                        "win": 510,
                        "play": 1000,
                        "pick_rate": 0.8
                    }
                ],
                "skill_masteries": [
                    { "ids": ["Q", "W", "E"] }
                ],
                "summoner_spells": [
                    { "ids": [4, 14], "win": 530, "play": 900, "pick_rate": 0.9 }
                ],
                "starter_items": [
                    { "ids": [1056, 2003, 2003], "win": 520, "play": 800 }
                ],
                "core_items": [
                    { "ids": [6655, 4645, 3089], "win": 600, "play": 500, "pick_rate": 0.4 }
                ],
                "boots": [
                    { "ids": [3020], "win": 525, "play": 850 }
                ],
                "last_items": [
                    { "ids": [3157], "win": 620, "play": 200 },
                    { "ids": [3135], "win": 590, "play": 150 }
                ]
            }
        }
        """;

        var payload = OpggParser.ParseBuildPayload(json);
        Assert.NotNull(payload);
        Assert.Equal("14.20.1", payload.Meta?.Version);

        var data = payload.Data;
        Assert.NotNull(data);
        Assert.Equal(103, data.Summary?.Id);

        // Counters
        Assert.NotNull(data.Counters);
        Assert.Equal(2, data.Counters.Count);
        Assert.Equal(0.6, data.Counters[0].Rate);
        Assert.Equal(0.4, data.Counters[1].Rate);

        // Runes
        Assert.Single(data.Runes);
        Assert.Equal(8100, data.Runes[0].PrimaryPageId);
        Assert.Equal(4, data.Runes[0].PrimaryRuneIds.Count);
        Assert.Equal(2, data.Runes[0].SecondaryRuneIds.Count);
        Assert.Equal(3, data.Runes[0].StatModIds.Count);

        // Skills
        Assert.NotNull(data.Skills);
        Assert.Single(data.Skills);
        Assert.Equal(6, data.Skills[0].Order.Count);
        Assert.NotNull(data.SkillMasteries);
        Assert.Equal(["Q", "W", "E"], data.SkillMasteries[0].Ids);

        // Items
        Assert.Single(data.SummonerSpells);
        Assert.Equal([4, 14], data.SummonerSpells[0].Ids);
        Assert.NotNull(data.StarterItems);
        Assert.Single(data.StarterItems);
        Assert.NotNull(data.CoreItems);
        Assert.Single(data.CoreItems);
        Assert.NotNull(data.Boots);
        Assert.Single(data.Boots);
        Assert.Equal(2, data.LastItems.Count);
    }

    [Fact]
    public void ParsePerkStyles_SupportsBothArrayAndStylesObject()
    {
        string arrayJson = """
        [
            {
                "id": 8000,
                "name": "Precision",
                "iconPath": "/lol-game-data/assets/v1/perk-images/Styles/7201_Precision.png",
                "slots": [
                    { "perks": [8005, 8008, 8021, 8010] },
                    { "perks": [9101, 9111, 8009] }
                ]
            }
        ]
        """;

        string objectJson = """
        {
            "styles": [
                {
                    "id": 8100,
                    "name": "Domination",
                    "iconPath": "/lol-game-data/assets/v1/perk-images/Styles/7200_Domination.png",
                    "slots": [
                        { "perks": [8112, 8124, 8128] }
                    ]
                }
            ]
        }
        """;

        var list1 = OpggParser.ParsePerkStyles(arrayJson);
        Assert.Single(list1);
        Assert.Equal(8000, list1[0].Id);
        Assert.Equal(2, list1[0].Slots.Count);

        var list2 = OpggParser.ParsePerkStyles(objectJson);
        Assert.Single(list2);
        Assert.Equal(8100, list2[0].Id);
        Assert.Single(list2[0].Slots);
    }

    [Fact]
    public void ParsePerkDetails_CleansHtmlAndUnescapesEntities()
    {
        string json = """
        [
            {
                "id": 8112,
                "name": "Electrocute",
                "iconPath": "/lol-game-data/assets/v1/perk-images/Styles/Domination/Electrocute/Electrocute.png",
                "shortDesc": "Hitting a champion with 3 <b>separate</b> attacks<br>deals &lt;bonus&gt; damage &amp; more."
            }
        ]
        """;

        var dict = OpggParser.ParsePerkDetails(json);
        Assert.Single(dict);
        Assert.True(dict.ContainsKey(8112));
        var perk = dict[8112];
        Assert.Equal("Electrocute", perk.Name);
        Assert.Contains("Hitting a champion with 3 separate attacks", perk.CleanedDesc);
        Assert.Contains("deals <bonus> damage & more.", perk.CleanedDesc);
        Assert.DoesNotContain("<b>", perk.CleanedDesc);
        Assert.DoesNotContain("&amp;", perk.CleanedDesc);
    }
}
