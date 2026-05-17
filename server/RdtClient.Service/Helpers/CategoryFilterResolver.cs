using RdtClient.Data.Models.Internal;

namespace RdtClient.Service.Helpers;

/// <summary>
/// Resolves the effective IncludeRegex / ExcludeRegex for a torrent by layering:
///   1. The torrent's own value (typed at Add time or persisted on the row).
///   2. The category's IncludeRegex / ExcludeRegex from General:Categories.
///   3. The source-level Default (Provider.Default, Watch.Default, Integrations.Default).
///
/// Step 1 is the caller's responsibility — this helper assumes the caller has already
/// decided whether to honour an explicit torrent regex. Use Resolve() when you want
/// (category > default) semantics for a new torrent that doesn't carry its own regex.
/// </summary>
public static class CategoryFilterResolver
{
    /// <summary>
    /// Given the active categories list, returns the (IncludeRegex, ExcludeRegex) pair
    /// for <paramref name="categoryName"/> with fallback to the supplied defaults. A
    /// blank / whitespace value at the category level is treated as "not set" and
    /// falls through.
    /// </summary>
    public static (String? Include, String? Exclude) Resolve(
        IEnumerable<DbCategory>? categories,
        String? categoryName,
        String? defaultInclude,
        String? defaultExclude)
    {
        if (String.IsNullOrWhiteSpace(categoryName) || categories == null)
        {
            return (defaultInclude, defaultExclude);
        }

        var category = categories.FirstOrDefault(c => String.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));

        if (category == null)
        {
            return (defaultInclude, defaultExclude);
        }

        var include = !String.IsNullOrWhiteSpace(category.IncludeRegex) ? category.IncludeRegex : defaultInclude;
        var exclude = !String.IsNullOrWhiteSpace(category.ExcludeRegex) ? category.ExcludeRegex : defaultExclude;

        return (include, exclude);
    }

    /// <summary>
    /// Convenience overload that reads the categories list from
    /// <see cref="Services.Settings"/>.<c>Get.General.Categories</c> so callers don't
    /// have to thread the parsed list through.
    /// </summary>
    public static (String? Include, String? Exclude) Resolve(
        String? categoryName,
        String? defaultInclude,
        String? defaultExclude)
    {
        var categories = CategoryParser.Parse(Services.Settings.Get.General.Categories);
        return Resolve(categories, categoryName, defaultInclude, defaultExclude);
    }
}
