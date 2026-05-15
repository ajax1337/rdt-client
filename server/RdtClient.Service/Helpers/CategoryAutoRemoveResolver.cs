using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;

namespace RdtClient.Service.Helpers;

/// Pure decision function for "after a torrent finishes downloading, what should we remove
/// from where?" Split out of TorrentRunner so the Aria2c-vs-Symlink semantics can be unit-tested
/// without standing up the full torrent loop.
public static class CategoryAutoRemoveResolver
{
    public readonly record struct Decision(
        Boolean RemoveDashboard,
        Boolean RemoveProvider,
        Boolean RemoveLocalFiles,
        Boolean SymlinkSuppressedProvider);

    /// Returns null if the category doesn't opt into any auto-remove; the caller should fall
    /// through to the FinishedAction enum path. Otherwise returns the exact flags to pass to
    /// Torrents.Delete, with the symlink safety downgrade already applied.
    public static Decision? Resolve(DbCategory? category, DownloadClient downloadClient)
    {
        if (category == null)
        {
            return null;
        }

        if (!category.RemoveFromDashboard && !category.RemoveFromProvider && !category.RemoveLocalFiles)
        {
            return null;
        }

        var rmDashboard = category.RemoveFromDashboard;
        var rmProvider = category.RemoveFromProvider;
        var rmFiles = category.RemoveLocalFiles;
        var symlinkSuppressed = false;

        // Symlink Downloader writes a symlink pointing at the cached file on the provider.
        // Removing the provider entry would break the symlink target, so we silently downgrade.
        // The UI surfaces a separate warning banner for symlink users so the suppression is visible.
        if (downloadClient == DownloadClient.Symlink && rmProvider)
        {
            rmProvider = false;
            symlinkSuppressed = true;
        }

        return new Decision(rmDashboard, rmProvider, rmFiles, symlinkSuppressed);
    }
}
