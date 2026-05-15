using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Test.Helpers;

public class CategoryParserTest
{
    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(CategoryParser.Parse(null));
        Assert.Empty(CategoryParser.Parse(""));
        Assert.Empty(CategoryParser.Parse("   "));
    }

    [Fact]
    public void Parse_LegacyCommaList_ReturnsCategoriesWithAllFlagsFalse()
    {
        var result = CategoryParser.Parse("Movies, TV Shows ,Other videos");

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Movies", "TV Shows", "Other videos" }, result.Select(c => c.Name));
        Assert.All(result, c =>
        {
            Assert.False(c.RemoveFromDashboard);
            Assert.False(c.RemoveFromProvider);
            Assert.False(c.RemoveLocalFiles);
        });
    }

    [Fact]
    public void Parse_LegacyCommaList_DedupesCaseInsensitively()
    {
        var result = CategoryParser.Parse("Movies,movies,MOVIES");

        Assert.Single(result);
        Assert.Equal("Movies", result[0].Name);
    }

    [Fact]
    public void Parse_NewJsonFormat_PreservesAllThreeFlags()
    {
        var json = """
                   [
                     {"name":"Movies","removeFromDashboard":true,"removeFromProvider":false,"removeLocalFiles":true},
                     {"name":"TV Shows","removeFromDashboard":false,"removeFromProvider":true,"removeLocalFiles":false}
                   ]
                   """;

        var result = CategoryParser.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("Movies", result[0].Name);
        Assert.True(result[0].RemoveFromDashboard);
        Assert.False(result[0].RemoveFromProvider);
        Assert.True(result[0].RemoveLocalFiles);

        Assert.Equal("TV Shows", result[1].Name);
        Assert.False(result[1].RemoveFromDashboard);
        Assert.True(result[1].RemoveFromProvider);
        Assert.False(result[1].RemoveLocalFiles);
    }

    [Fact]
    public void Parse_LegacyAutoRemoveOnFinish_MigratesToDashboardPlusProvider()
    {
        // Old Phase-1 cut had a single boolean meaning "dashboard + provider, keep local files".
        // When we see that on read, with no granular flags set, we migrate.
        var json = """[{"name":"Other videos","autoRemoveOnFinish":true}]""";

        var result = CategoryParser.Parse(json);

        Assert.Single(result);
        Assert.True(result[0].RemoveFromDashboard);
        Assert.True(result[0].RemoveFromProvider);
        Assert.False(result[0].RemoveLocalFiles);
    }

    [Fact]
    public void Parse_LegacyAutoRemoveOnFinish_DoesNotOverrideExplicitGranularFlag()
    {
        // If the user has set any granular flag, their explicit choice wins — the legacy
        // bit is treated as stale and ignored.
        var json = """[{"name":"Movies","autoRemoveOnFinish":true,"removeLocalFiles":true}]""";

        var result = CategoryParser.Parse(json);

        Assert.Single(result);
        Assert.False(result[0].RemoveFromDashboard);
        Assert.False(result[0].RemoveFromProvider);
        Assert.True(result[0].RemoveLocalFiles);
    }

    [Fact]
    public void Parse_MalformedJson_FallsBackToCommaList()
    {
        var result = CategoryParser.Parse("[broken json");

        Assert.Single(result);
        Assert.Equal("[broken json", result[0].Name);
    }

    [Fact]
    public void Parse_JsonWithMissingName_SkipsThatEntry()
    {
        var json = """[{"name":""},{"removeFromDashboard":true},{"name":"  "},{"name":"Movies"}]""";

        var result = CategoryParser.Parse(json);

        Assert.Single(result);
        Assert.Equal("Movies", result[0].Name);
    }

    [Fact]
    public void Parse_AcceptsPascalCaseJson()
    {
        // Hand-edited DB values or older Phase-1 deploys may write PascalCase.
        var json = """[{"Name":"Movies","RemoveFromDashboard":true}]""";

        var result = CategoryParser.Parse(json);

        Assert.Single(result);
        Assert.Equal("Movies", result[0].Name);
        Assert.True(result[0].RemoveFromDashboard);
    }

    [Fact]
    public void Serialize_NullInput_ReturnsEmptyArray()
    {
        Assert.Equal("[]", CategoryParser.Serialize(null));
    }

    [Fact]
    public void Serialize_OutputsCamelCaseJsonWithoutLegacyField()
    {
        var input = new[]
        {
            new DbCategory { Name = "Movies", RemoveFromDashboard = true, AutoRemoveOnFinish = true }
        };

        var json = CategoryParser.Serialize(input);

        Assert.Contains("\"name\":\"Movies\"", json);
        Assert.Contains("\"removeFromDashboard\":true", json);
        // Legacy field must not be persisted on save — it's read-only compat surface.
        Assert.DoesNotContain("autoRemoveOnFinish", json);
        Assert.DoesNotContain("AutoRemoveOnFinish", json);
    }

    [Fact]
    public void Names_ReturnsBareNamesInOrder()
    {
        var names = CategoryParser.Names("Movies,TV Shows,Other videos");

        Assert.Equal(new[] { "Movies", "TV Shows", "Other videos" }, names);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var json = """[{"name":"Other videos","removeFromProvider":true}]""";

        var match = CategoryParser.Find(json, "OTHER VIDEOS");

        Assert.NotNull(match);
        Assert.True(match!.RemoveFromProvider);
    }

    [Fact]
    public void Find_NullOrEmptyName_ReturnsNull()
    {
        var json = """[{"name":"Movies"}]""";

        Assert.Null(CategoryParser.Find(json, null));
        Assert.Null(CategoryParser.Find(json, ""));
        Assert.Null(CategoryParser.Find(json, "   "));
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        var json = """[{"name":"Movies"}]""";

        Assert.Null(CategoryParser.Find(json, "Music"));
    }

    [Fact]
    public void Roundtrip_LegacyCommaThenSerialize_ProducesJsonThatParsesBackEquivalently()
    {
        var first = CategoryParser.Parse("Movies,TV Shows");
        var json = CategoryParser.Serialize(first);
        var second = CategoryParser.Parse(json);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Name, second[i].Name);
            Assert.Equal(first[i].RemoveFromDashboard, second[i].RemoveFromDashboard);
            Assert.Equal(first[i].RemoveFromProvider, second[i].RemoveFromProvider);
            Assert.Equal(first[i].RemoveLocalFiles, second[i].RemoveLocalFiles);
        }
    }
}
