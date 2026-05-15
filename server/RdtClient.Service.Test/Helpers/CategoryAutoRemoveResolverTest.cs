using RdtClient.Data.Enums;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;

namespace RdtClient.Service.Test.Helpers;

public class CategoryAutoRemoveResolverTest
{
    [Fact]
    public void Resolve_NullCategory_ReturnsNull()
    {
        Assert.Null(CategoryAutoRemoveResolver.Resolve(null, DownloadClient.Aria2c));
    }

    [Fact]
    public void Resolve_CategoryWithNoFlags_ReturnsNull()
    {
        // No opt-in means the caller should fall through to the FinishedAction enum path.
        var cat = new DbCategory { Name = "Movies" };

        Assert.Null(CategoryAutoRemoveResolver.Resolve(cat, DownloadClient.Aria2c));
    }

    [Fact]
    public void Resolve_Aria2c_AllThreeFlags_PassesThroughUnchanged()
    {
        var cat = new DbCategory
        {
            Name = "Movies",
            RemoveFromDashboard = true,
            RemoveFromProvider = true,
            RemoveLocalFiles = true
        };

        var decision = CategoryAutoRemoveResolver.Resolve(cat, DownloadClient.Aria2c);

        Assert.NotNull(decision);
        Assert.True(decision!.Value.RemoveDashboard);
        Assert.True(decision.Value.RemoveProvider);
        Assert.True(decision.Value.RemoveLocalFiles);
        Assert.False(decision.Value.SymlinkSuppressedProvider);
    }

    [Fact]
    public void Resolve_Aria2c_ProviderOnly_RemovesFromProvider()
    {
        // The user's existing setup: TorBox + Aria2c, wants the provider slot freed but everything else kept.
        var cat = new DbCategory
        {
            Name = "Other videos",
            RemoveFromProvider = true
        };

        var decision = CategoryAutoRemoveResolver.Resolve(cat, DownloadClient.Aria2c);

        Assert.NotNull(decision);
        Assert.False(decision!.Value.RemoveDashboard);
        Assert.True(decision.Value.RemoveProvider);
        Assert.False(decision.Value.RemoveLocalFiles);
        Assert.False(decision.Value.SymlinkSuppressedProvider);
    }

    [Fact]
    public void Resolve_Symlink_WithProviderFlag_SuppressesProviderRemoval()
    {
        // Critical safety: deleting the provider entry under Symlink Downloader breaks the symlink target.
        var cat = new DbCategory
        {
            Name = "Movies",
            RemoveFromDashboard = true,
            RemoveFromProvider = true,
            RemoveLocalFiles = false
        };

        var decision = CategoryAutoRemoveResolver.Resolve(cat, DownloadClient.Symlink);

        Assert.NotNull(decision);
        Assert.True(decision!.Value.RemoveDashboard);
        Assert.False(decision.Value.RemoveProvider); // forcibly downgraded
        Assert.False(decision.Value.RemoveLocalFiles);
        Assert.True(decision.Value.SymlinkSuppressedProvider); // caller should log/warn
    }

    [Fact]
    public void Resolve_Symlink_WithoutProviderFlag_DoesNotFlagSuppression()
    {
        // If the user never asked for provider removal, the symlink downgrade is a non-event.
        var cat = new DbCategory
        {
            Name = "Movies",
            RemoveFromDashboard = true,
            RemoveFromProvider = false,
            RemoveLocalFiles = true
        };

        var decision = CategoryAutoRemoveResolver.Resolve(cat, DownloadClient.Symlink);

        Assert.NotNull(decision);
        Assert.True(decision!.Value.RemoveDashboard);
        Assert.False(decision.Value.RemoveProvider);
        Assert.True(decision.Value.RemoveLocalFiles);
        Assert.False(decision.Value.SymlinkSuppressedProvider);
    }

    [Theory]
    [InlineData(DownloadClient.Bezzad)]
    [InlineData(DownloadClient.Aria2c)]
    [InlineData(DownloadClient.DownloadStation)]
    public void Resolve_NonSymlinkClients_NeverSuppressProvider(DownloadClient client)
    {
        var cat = new DbCategory
        {
            Name = "Movies",
            RemoveFromProvider = true
        };

        var decision = CategoryAutoRemoveResolver.Resolve(cat, client);

        Assert.NotNull(decision);
        Assert.True(decision!.Value.RemoveProvider);
        Assert.False(decision.Value.SymlinkSuppressedProvider);
    }
}
