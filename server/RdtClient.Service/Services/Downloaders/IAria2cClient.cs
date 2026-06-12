using Aria2NET;

namespace RdtClient.Service.Services.Downloaders;

/// <summary>
/// Thin seam over <see cref="Aria2NetClient"/> (which exposes no interface and no
/// virtual methods) so <see cref="Aria2cDownloader"/> can be unit tested without a
/// live aria2 RPC endpoint. Mirrors the ISynologyClient injection used by
/// DownloadStationDownloader. Only the calls the downloader actually makes are
/// surfaced; signatures match Aria2NetClient exactly so the adapter stays trivial.
/// </summary>
public interface IAria2cClient
{
    Task<String> AddUriAsync(IList<String> uriList, IDictionary<String, Object>? options = null, Int32? position = null, CancellationToken cancellationToken = default);
    Task<DownloadStatusResult> TellStatusAsync(String gid, CancellationToken cancellationToken = default);
    Task<IList<DownloadStatusResult>> TellAllAsync(CancellationToken cancellationToken = default);
    Task<String> PauseAsync(String gid, CancellationToken cancellationToken = default);
    Task<String> UnpauseAsync(String gid, CancellationToken cancellationToken = default);
    Task<String> ForceRemoveAsync(String gid, CancellationToken cancellationToken = default);
    Task RemoveDownloadResultAsync(String gid, CancellationToken cancellationToken = default);
}

public class Aria2NetClientAdapter(Aria2NetClient client) : IAria2cClient
{
    public Task<String> AddUriAsync(IList<String> uriList, IDictionary<String, Object>? options = null, Int32? position = null, CancellationToken cancellationToken = default)
    {
        return client.AddUriAsync(uriList, options, position, cancellationToken);
    }

    public Task<DownloadStatusResult> TellStatusAsync(String gid, CancellationToken cancellationToken = default)
    {
        return client.TellStatusAsync(gid, cancellationToken);
    }

    public Task<IList<DownloadStatusResult>> TellAllAsync(CancellationToken cancellationToken = default)
    {
        return client.TellAllAsync(cancellationToken);
    }

    public Task<String> PauseAsync(String gid, CancellationToken cancellationToken = default)
    {
        return client.PauseAsync(gid, cancellationToken);
    }

    public Task<String> UnpauseAsync(String gid, CancellationToken cancellationToken = default)
    {
        return client.UnpauseAsync(gid, cancellationToken);
    }

    public Task<String> ForceRemoveAsync(String gid, CancellationToken cancellationToken = default)
    {
        return client.ForceRemoveAsync(gid, cancellationToken);
    }

    public Task RemoveDownloadResultAsync(String gid, CancellationToken cancellationToken = default)
    {
        return client.RemoveDownloadResultAsync(gid, cancellationToken);
    }
}
