using System.Text.Json;
using RdtClient.Data.Models.Internal;

namespace RdtClient.Service.Helpers;

/// One-line WHY: General:Categories is stored as a single string for back-compat, but Phase 1
/// (per-category auto-remove on finish) needs structured data; this parser hides the legacy
/// comma-list vs. JSON dual format from every call site and migrates legacy single-flag
/// AutoRemoveOnFinish into the three granular flags.
public static class CategoryParser
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // Case-insensitive read tolerates legacy PascalCase payloads or hand-edited DB values;
        // CamelCase output keeps the wire format consistent with the Angular client's JSON.parse.
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<DbCategory> Parse(String? raw)
    {
        if (String.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<DbCategory>();
        }

        var trimmed = raw.TrimStart();

        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<DbCategory>>(trimmed, JsonOpts);

                if (parsed != null)
                {
                    return parsed
                           .Where(c => !String.IsNullOrWhiteSpace(c.Name))
                           .Select(MigrateLegacy)
                           .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
                }
            }
            catch (JsonException)
            {
                // fall through to legacy parse
            }
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(n => !String.IsNullOrWhiteSpace(n))
                  .DistinctBy(n => n, StringComparer.OrdinalIgnoreCase)
                  .Select(n => new DbCategory { Name = n })
                  .ToList();
    }

    public static String Serialize(IEnumerable<DbCategory>? categories)
    {
        if (categories == null)
        {
            return "[]";
        }

        // Don't write the legacy field on disk going forward. Normalize blank regex
        // strings to null so the round-trip is stable (a freshly-cleared input box
        // shouldn't change the JSON shape between saves).
        var clean = categories
                    .Where(c => c != null)
                    .Select(c => new DbCategory
                    {
                        Name = (c.Name ?? "").Trim(),
                        RemoveFromDashboard = c.RemoveFromDashboard,
                        RemoveFromProvider = c.RemoveFromProvider,
                        RemoveLocalFiles = c.RemoveLocalFiles,
                        IncludeRegex = String.IsNullOrWhiteSpace(c.IncludeRegex) ? null : c.IncludeRegex.Trim(),
                        ExcludeRegex = String.IsNullOrWhiteSpace(c.ExcludeRegex) ? null : c.ExcludeRegex.Trim()
                    })
                    .ToList();

        return JsonSerializer.Serialize(clean, JsonOpts);
    }

    public static IReadOnlyList<String> Names(String? raw)
    {
        return Parse(raw).Select(c => c.Name).ToList();
    }

    public static DbCategory? Find(String? raw, String? name)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Parse(raw).FirstOrDefault(c => String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static DbCategory MigrateLegacy(DbCategory c)
    {
        var name = c.Name.Trim();
        var includeRegex = String.IsNullOrWhiteSpace(c.IncludeRegex) ? null : c.IncludeRegex.Trim();
        var excludeRegex = String.IsNullOrWhiteSpace(c.ExcludeRegex) ? null : c.ExcludeRegex.Trim();

        // Only migrate when the user hasn't explicitly set any granular flag — otherwise
        // their newer choices win.
        if (c.AutoRemoveOnFinish && !c.RemoveFromDashboard && !c.RemoveFromProvider && !c.RemoveLocalFiles)
        {
            return new DbCategory
            {
                Name = name,
                RemoveFromDashboard = true,
                RemoveFromProvider = true,
                RemoveLocalFiles = false,
                IncludeRegex = includeRegex,
                ExcludeRegex = excludeRegex
            };
        }

        return new DbCategory
        {
            Name = name,
            RemoveFromDashboard = c.RemoveFromDashboard,
            RemoveFromProvider = c.RemoveFromProvider,
            RemoveLocalFiles = c.RemoveLocalFiles,
            IncludeRegex = includeRegex,
            ExcludeRegex = excludeRegex
        };
    }
}
