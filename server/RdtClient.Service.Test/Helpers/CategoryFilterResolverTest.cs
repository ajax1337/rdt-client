using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Test.Helpers;

public class CategoryFilterResolverTest
{
    [Fact]
    public void NoCategoryName_ReturnsDefaults()
    {
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video", ExcludeRegex = "sample" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, null, "default-include", "default-exclude");

        Assert.Equal("default-include", include);
        Assert.Equal("default-exclude", exclude);
    }

    [Fact]
    public void EmptyCategoryName_ReturnsDefaults()
    {
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, "   ", "d-i", "d-e");

        Assert.Equal("d-i", include);
        Assert.Equal("d-e", exclude);
    }

    [Fact]
    public void UnknownCategory_ReturnsDefaults()
    {
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, "Software", "d-i", "d-e");

        Assert.Equal("d-i", include);
        Assert.Equal("d-e", exclude);
    }

    [Fact]
    public void CategoryWithBothRegex_OverridesBothDefaults()
    {
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video-only", ExcludeRegex = "skip-this" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, "Movies", "default-include", "default-exclude");

        Assert.Equal("video-only", include);
        Assert.Equal("skip-this", exclude);
    }

    [Fact]
    public void CategoryWithOnlyIncludeRegex_FallsBackForExclude()
    {
        // The user might set just an include regex and leave exclude empty. Each field
        // falls through independently — there's no "all or nothing" tie between them.
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video-only" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, "Movies", "default-include", "default-exclude");

        Assert.Equal("video-only", include);
        Assert.Equal("default-exclude", exclude);
    }

    [Fact]
    public void CategoryNameMatchIsCaseInsensitive()
    {
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "video" }
        };

        var (include, _) = CategoryFilterResolver.Resolve(categories, "movies", null, null);

        Assert.Equal("video", include);
    }

    [Fact]
    public void NullCategoriesList_ReturnsDefaults()
    {
        var (include, exclude) = CategoryFilterResolver.Resolve(null, "Movies", "d-i", "d-e");

        Assert.Equal("d-i", include);
        Assert.Equal("d-e", exclude);
    }

    [Fact]
    public void CategoryWithWhitespaceRegex_TreatedAsUnset()
    {
        // Defence-in-depth: even if the parser somehow lets a whitespace-only regex
        // through, the resolver treats it as "not set" and falls back to the default.
        var categories = new[]
        {
            new DbCategory { Name = "Movies", IncludeRegex = "   ", ExcludeRegex = "" }
        };

        var (include, exclude) = CategoryFilterResolver.Resolve(categories, "Movies", "default-include", "default-exclude");

        Assert.Equal("default-include", include);
        Assert.Equal("default-exclude", exclude);
    }
}
