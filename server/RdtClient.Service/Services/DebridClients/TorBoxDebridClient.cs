using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RdtClient.Data.Enums;
using RdtClient.Data.Models.Data;
using RdtClient.Data.Models.DebridClient;
using RdtClient.Data.Models.Internal;
using RdtClient.Service.Helpers;
using TorBoxNET;
using Torrent = RdtClient.Data.Models.Data.Torrent;

namespace RdtClient.Service.Services.DebridClients;

public sealed record TorBoxAddTorrentResult(String Hash, Int32? TorrentId);

public class TorBoxDebridClient(ILogger<TorBoxDebridClient> logger, IHttpClientFactory httpClientFactory, IDownloadableFileFilter fileFilter, IRateLimitCoordinator coordinator)
    : IDebridClient
{
    // TorBox file downloads are addressed by torrent_id + file_id (or "zip" for the
    // bundle). RDT stores these as a placeholder URL in Download.Path because the real
    // download URL has to be minted with the user's API key at request time. The
    // /fakedl/ prefix is parsed by TryGetFileId at the bottom of this file — keep the
    // builders and the parser in lock-step.
    private const String FakeDlUrlPrefix = "https://torbox.app/fakedl";
    private const String FakeDlZipSegment = "zip";

    private static String BuildFakeDlUrl(Int64 torrentId, String fileSegment)
    {
        return $"{FakeDlUrlPrefix}/{torrentId}/{fileSegment}";
    }

    private const String TorBoxApiHost = "api.torbox.app";
    private static readonly JsonSerializerSettings JsonSerializerSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };
    private static readonly TimeSpan MissingResourceGracePeriod = TimeSpan.FromMinutes(10);

    private TimeSpan? _offset;

    // TorBox's AddMagnet / AddFile flow needs the user's preferred seedTorrents setting
    // from /api/user/me. Calling User.GetAsync on every Add is an extra HTTP round-trip
    // that runs inside the RealDebridUpdateLock critical section, so it directly extends
    // the time a row sits in "Not Yet Added to Provider". Cache for 5 min.
    //
    // Reference type (not a struct tuple) because the fast path reads without holding the
    // lock: a class reference is read/written atomically, so consumers see either the old
    // entry or the new entry — never a torn half-state. The ApiKeyHash is part of the
    // entry so the cache invalidates implicitly when the user swaps their TorBox account.
    private sealed class UserSettingsCacheEntry
    {
        public required String ApiKeyHash { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required Int32 SeedTorrents { get; init; }
    }
    private static readonly TimeSpan UserSettingsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim UserSettingsLock = new(1, 1);
    private static UserSettingsCacheEntry? _userSettingsCache;

    public async Task<IList<DebridClientTorrent>> GetDownloads()
    {
        var results = new List<DebridClientTorrent>();

        var currentTorrentsTask = GetCurrentTorrents();
        var queuedTorrentsTask = GetQueuedTorrents();
        var currentNzbsTask = GetCurrentUsenet();
        var queuedNzbsTask = GetQueuedUsenet();

        await Task.WhenAll(currentTorrentsTask, queuedTorrentsTask, currentNzbsTask, queuedNzbsTask);

        var currentTorrents = await currentTorrentsTask;

        if (currentTorrents != null)
        {
            results.AddRange(currentTorrents.Select(Map));
        }

        var queuedTorrents = await queuedTorrentsTask;

        if (queuedTorrents != null)
        {
            results.AddRange(queuedTorrents.Select(Map));
        }

        var currentNzbs = await currentNzbsTask;

        if (currentNzbs != null)
        {
            results.AddRange(currentNzbs.Select(Map));
        }

        var queuedNzbs = await queuedNzbsTask;

        if (queuedNzbs != null)
        {
            results.AddRange(queuedNzbs.Select(Map));
        }

        return results;
    }

    public async Task<DebridClientUser> GetUser()
    {
        var user = await HandleErrors(() => GetClient().User.GetAsync(false));

        return new()
        {
            Username = user.Data!.Email,
            Expiration = user.Data!.Plan != 0 ? user.Data!.PremiumExpiresAt!.Value : null
        };
    }

    public async Task<String> AddTorrentMagnet(String magnetLink)
    {
        var result = await AddTorrentMagnetWithInfo(magnetLink);

        return result.Hash;
    }

    public async Task<TorBoxAddTorrentResult> AddTorrentMagnetWithInfo(String magnetLink)
    {
        magnetLink = magnetLink.Trim();

        return await HandleAddTorrentErrors(async asQueued =>
        {
            var seedTorrents = await GetSeedTorrentsSetting();
            var result = await GetClient(DiConfig.TORBOX_CLIENT_SLOW).Torrents.AddMagnetAsync(magnetLink, seedTorrents, as_queued: asQueued);

            return new TorBoxAddTorrentResult(result.Data!.Hash!, result.Data.TorrentId);
        });
    }

    public async Task<String> AddTorrentFile(Byte[] bytes)
    {
        return await HandleAddTorrentErrors(async asQueued =>
        {
            var seedTorrents = await GetSeedTorrentsSetting();
            var result = await GetClient(DiConfig.TORBOX_CLIENT_SLOW).Torrents.AddFileAsync(bytes, seedTorrents, as_queued: asQueued);

            return result.Data!.Hash!;
        });
    }

    private async Task<Int32> GetSeedTorrentsSetting()
    {
        var now = DateTimeOffset.UtcNow;
        var apiKeyHash = HashApiKey(Settings.Get.Provider.ApiKey);

        var snapshot = _userSettingsCache;
        if (snapshot != null && snapshot.ApiKeyHash == apiKeyHash && snapshot.ExpiresAt > now)
        {
            return snapshot.SeedTorrents;
        }

        await UserSettingsLock.WaitAsync();
        try
        {
            snapshot = _userSettingsCache;
            if (snapshot != null && snapshot.ApiKeyHash == apiKeyHash && snapshot.ExpiresAt > now)
            {
                return snapshot.SeedTorrents;
            }

            try
            {
                var user = await GetClient().User.GetAsync(true);
                var seed = user.Data?.Settings?.SeedTorrents ?? 3;
                _userSettingsCache = new UserSettingsCacheEntry
                {
                    ApiKeyHash = apiKeyHash,
                    ExpiresAt = now.Add(UserSettingsCacheTtl),
                    SeedTorrents = seed
                };
                return seed;
            }
            catch (Exception ex)
            {
                // Stale-while-error: if the refresh failed and we have a cached value for
                // THIS account, serve it rather than failing the Add. A transient TorBox
                // outage shouldn't error out every in-flight magnet.
                if (snapshot != null && snapshot.ApiKeyHash == apiKeyHash)
                {
                    logger.LogWarning(ex, "TorBox User.GetAsync failed during seedTorrents refresh; serving stale value ({Seed})", snapshot.SeedTorrents);
                    return snapshot.SeedTorrents;
                }
                throw;
            }
        }
        finally
        {
            UserSettingsLock.Release();
        }
    }

    private static String HashApiKey(String? apiKey)
    {
        if (String.IsNullOrEmpty(apiKey))
        {
            return "";
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public async Task<String> AddNzbLink(String nzbLink)
    {
        return await HandleAddUsenetErrors(async asQueued =>
        {
            var result = await GetClient(DiConfig.TORBOX_CLIENT_SLOW).Usenet.AddLinkAsync(nzbLink, as_queued: asQueued);

            return result.Data!.Hash!;
        });
    }

    public virtual async Task<String> AddNzbFile(Byte[] bytes, String? name)
    {
        return await HandleAddUsenetErrors(async asQueued =>
        {
            var result = await GetClient(DiConfig.TORBOX_CLIENT_SLOW).Usenet.AddFileAsync(bytes, name: name, as_queued: asQueued);

            return result.Data!.Hash!;
        });
    }

    public async Task<IList<DebridClientAvailableFile>> GetAvailableFiles(String hash)
    {
        var availability = await GetTorrentAvailability(hash);

        if (availability.Data != null && availability.Data.Count > 0)
        {
            return (availability.Data[0]?.Files ?? []).Select(file => new DebridClientAvailableFile
                                                      {
                                                          Filename = file.Name,
                                                          Filesize = file.Size
                                                      })
                                                      .ToList();
        }

        var usenetAvailability = await GetUsenetAvailability(hash);

        if (usenetAvailability.Data != null && usenetAvailability.Data.Count > 0)
        {
            return (usenetAvailability.Data[0]?.Files ?? []).Select(file => new DebridClientAvailableFile
                                                            {
                                                                Filename = file.Name,
                                                                Filesize = file.Size
                                                            })
                                                            .ToList();
        }

        return [];
    }

    public async Task<Boolean> IsTorrentAvailable(String hash)
    {
        var availability = await GetTorrentAvailability(hash);

        return availability.Data?.Count > 0;
    }

    public async Task<DebridClientTorrent?> GetTorrentByProviderId(Int32 torrentId)
    {
        var torrent = await HandleErrors(() => GetClient().Torrents.GetIdInfoAsync(torrentId, true));

        return torrent == null ? null : Map(torrent);
    }

    public DownloadInfo CreateZipDownloadInfo(Int32 torrentId, String? torrentName)
    {
        var fileName = String.IsNullOrWhiteSpace(torrentName) ? $"torbox-{torrentId}.zip" : $"{torrentName}.zip";

        return new()
        {
            RestrictedLink = BuildFakeDlUrl(torrentId, FakeDlZipSegment),
            FileName = fileName
        };
    }

    /// <inheritdoc />
    public Task<Int32?> SelectFiles(Torrent torrent)
    {
        return Task.FromResult<Int32?>(torrent.Files.Count);
    }

    public async Task Delete(Torrent torrent)
    {
        if (torrent.RdId == null)
        {
            return;
        }

        await HandleErrors(async () =>
        {
            if (torrent.Type == DownloadType.Nzb)
            {
                await GetClient().Usenet.ControlAsync(torrent.RdId, "delete");
            }
            else
            {
                await GetClient().Torrents.ControlAsync(torrent.RdId, "delete");
            }
        });
    }

    public async Task<String> Unrestrict(Torrent torrent, String link)
    {
        if (String.IsNullOrWhiteSpace(link))
        {
            throw new ArgumentException("Link cannot be null or empty", nameof(link));
        }

        var segments = link.Split('/');

        if (segments is not [_, _, _, _, var torrentIdStr, var fileIdStrOrZip])
        {
            throw new ArgumentException($"Invalid link format: {link}", nameof(link));
        }

        var zipped = fileIdStrOrZip == "zip";
        var fileIdStr = zipped ? "0" : fileIdStrOrZip;

        if (!Int32.TryParse(torrentIdStr, out var torrentId))
        {
            throw new ArgumentException($"Invalid torrent ID in link segment 4: {torrentIdStr}", nameof(link));
        }

        if (!Int32.TryParse(fileIdStr, out var fileId))
        {
            throw new ArgumentException($"Invalid file ID in link segment 5: {fileId}", nameof(link));
        }

        async Task<String> RequestDownloadLink(Int32 id)
        {
            Response<String> result;

            if (torrent.Type == DownloadType.Nzb)
            {
                result = await HandleErrors(() => GetClient().Usenet.RequestDownloadAsync(id, fileId, zipped));
            }
            else
            {
                result = await HandleErrors(() => GetClient().Torrents.RequestDownloadAsync(id, fileId, zipped));
            }

            if (result.Error != null)
            {
                throw new($"Unrestrict returned an invalid download: {result.Error}");
            }

            return result.Data!;
        }

        try
        {
            return await RequestDownloadLink(torrentId);
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception)
        {
            // The id embedded in the fakedl link is the TorBox id at the time the file
            // list was fetched. Retrying a torrent deletes + re-adds it on TorBox, which
            // assigns a new id, so the embedded id can point at a deleted instance and
            // requestdl fails with DATABASE_ERROR. Re-resolve the current id by hash and
            // try once more before giving up.
            var freshId = await TryResolveCurrentProviderId(torrent);

            if (freshId == null || freshId.Value == torrentId)
            {
                throw;
            }

            logger.LogWarning("TorBox id {StaleId} in link {Link} is stale, retrying with current id {FreshId} for hash {Hash}", torrentId, link, freshId.Value, torrent.Hash);

            return await RequestDownloadLink(freshId.Value);
        }
    }

    /// <summary>
    /// Looks up the torrent's current TorBox id by hash, bypassing any cached list data.
    /// Returns null when the hash cannot be found or the lookup fails.
    /// </summary>
    private async Task<Int32?> TryResolveCurrentProviderId(Torrent torrent)
    {
        try
        {
            if (torrent.Type == DownloadType.Nzb)
            {
                var usenets = await GetClient().Usenet.GetCurrentAsync(true);
                var usenetMatch = usenets?.FirstOrDefault(m => String.Equals(m.Hash, torrent.RdId, StringComparison.OrdinalIgnoreCase) ||
                                                               String.Equals(m.Hash, torrent.Hash, StringComparison.OrdinalIgnoreCase));

                return (Int32?)usenetMatch?.Id;
            }

            var currentTorrents = await GetClient().Torrents.GetCurrentAsync(true);
            var currentMatch = currentTorrents?.FirstOrDefault(t => String.Equals(t.Hash, torrent.Hash, StringComparison.OrdinalIgnoreCase));

            if (currentMatch != null)
            {
                return currentMatch.Id;
            }

            var queuedTorrents = await GetClient().Torrents.GetQueuedAsync(true);
            var queuedMatch = queuedTorrents?.FirstOrDefault(t => String.Equals(t.Hash, torrent.Hash, StringComparison.OrdinalIgnoreCase));

            return queuedMatch?.Id;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to re-resolve TorBox id for hash {Hash}", torrent.Hash);

            return null;
        }
    }

    public async Task<Torrent> UpdateData(Torrent torrent, DebridClientTorrent? torrentClientTorrent)
    {
        try
        {
            if (torrent.RdId == null)
            {
                return torrent;
            }

            var rdTorrent = torrentClientTorrent ?? await GetInfo(torrent.RdId, torrent.Type) ?? throw new($"Resource not found");

            if (!String.IsNullOrWhiteSpace(rdTorrent.Filename))
            {
                torrent.RdName = rdTorrent.Filename;
            }

            if (!String.IsNullOrWhiteSpace(rdTorrent.OriginalFilename))
            {
                torrent.RdName = rdTorrent.OriginalFilename;
            }

            if (rdTorrent.Bytes > 0)
            {
                torrent.RdSize = rdTorrent.Bytes;
            }
            else if (rdTorrent.OriginalBytes > 0)
            {
                torrent.RdSize = rdTorrent.OriginalBytes;
            }

            if (rdTorrent.Files != null)
            {
                torrent.RdFiles = JsonConvert.SerializeObject(rdTorrent.Files, JsonSerializerSettings);
            }

            torrent.ClientKind = Provider.TorBox;
            torrent.RdHost = rdTorrent.Host;
            torrent.RdSplit = rdTorrent.Split;
            torrent.RdProgress = rdTorrent.Progress;
            torrent.RdAdded = rdTorrent.Added;
            torrent.RdEnded = rdTorrent.Ended;
            torrent.RdSpeed = rdTorrent.Speed;
            torrent.RdSeeders = rdTorrent.Seeders;
            torrent.RdStatusRaw = rdTorrent.Status;

            if (rdTorrent.Host == "True")
            {
                torrent.RdStatus = TorrentStatus.Finished;
            }
            else
            {
                logger.LogTrace("Updating status for {TorrentName} from {OldStatus} to {NewStatus}", torrent.RdName, torrent.RdStatus, rdTorrent.Status);

                torrent.RdStatus = rdTorrent.Status switch
                {
                    "allocating" => TorrentStatus.Processing,
                    "metaDL" => TorrentStatus.Processing,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("queued", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Processing,
                    "completed" => TorrentStatus.Downloading,
                    "processing" => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("paused", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("stalled", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("downloading", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("checking", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("waiting", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("direct unpack", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("repair", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("verifying", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Downloading,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("uploading", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Uploading,
                    "cached" => TorrentStatus.Finished,
                    "missing" => TorrentStatus.Error, // NZB missing parts
                    "error" => TorrentStatus.Error,
                    _ when rdTorrent.Status != null && rdTorrent.Status.StartsWith("failed", StringComparison.OrdinalIgnoreCase) => TorrentStatus.Error,
                    _ => LogUnmappedStatus(rdTorrent.Status, torrent)
                };
            }
        }
        catch (Exception ex)
        {
            if (ex.Message == "Resource not found")
            {
                if (torrent.Added > DateTimeOffset.UtcNow.Subtract(MissingResourceGracePeriod))
                {
                    logger.LogWarning(
                        "TorBox resource {RdId} for torrent {TorrentId} was not found yet. Keeping it in processing during the grace period.",
                        torrent.RdId,
                        torrent.TorrentId);

                    torrent.ClientKind = Provider.TorBox;
                    torrent.RdStatusRaw = "waiting_for_torbox";
                    torrent.RdStatus = TorrentStatus.Processing;
                }
                else
                {
                    torrent.RdStatusRaw = "deleted";
                    torrent.RdStatus = TorrentStatus.Error;
                }
            }
            else
            {
                throw;
            }
        }

        return torrent;
    }

    public async Task<IList<DownloadInfo>?> GetDownloadInfos(Torrent torrent)
    {
        var id = TryGetProviderDownloadId(torrent);

        if (id == null && torrent.Type == DownloadType.Nzb)
        {
            if (torrent.RdId == null)
            {
                return null;
            }

            var usenets = await HandleErrors(() => GetClient().Usenet.GetCurrentAsync(true));
            var usenet = usenets?.FirstOrDefault(m => String.Equals(m.Hash, torrent.RdId, StringComparison.OrdinalIgnoreCase));
            id = (Int32?)usenet?.Id;
        }
        else if (id == null)
        {
            // Patched: bypass TorBox.NET's case-sensitive GetHashInfoAsync. Look up by hash directly
            // against /mylist AND /queued with case-insensitive comparison, since Torbox returns
            // hashes in lowercase but rdt-client may store them in any case.
            var currentTorrents = await HandleErrors(() => GetClient().Torrents.GetCurrentAsync(true));
            var match = currentTorrents?.FirstOrDefault(t => String.Equals(t.Hash, torrent.Hash, StringComparison.OrdinalIgnoreCase));
            id = match?.Id;

            if (id == null)
            {
                var queuedTorrents = await HandleErrors(() => GetClient().Torrents.GetQueuedAsync(true));
                var qmatch = queuedTorrents?.FirstOrDefault(t => String.Equals(t.Hash, torrent.Hash, StringComparison.OrdinalIgnoreCase));
                id = qmatch?.Id;

                if (id == null)
                {
                    logger.LogWarning(
                        "GetDownloadInfos: TorBox hash {Hash} (RdId={RdId}) not found in current ({CurrentCount}) or queued ({QueuedCount}) lists. "
                        + "First 3 current hashes: {Sample}",
                        torrent.Hash, torrent.RdId,
                        currentTorrents?.Count ?? -1,
                        queuedTorrents?.Count ?? -1,
                        currentTorrents == null ? "<null>" : String.Join(",", currentTorrents.Take(3).Select(t => t.Hash ?? "<null>")));
                }
            }
        }

        if (id == null)
        {
            return null;
        }

        var downloadableFiles = torrent.Files.Where(file => fileFilter.IsDownloadable(torrent, file.Path, file.Bytes)).ToList();

        if (downloadableFiles.Count == torrent.Files.Count && torrent.DownloadClient != Data.Enums.DownloadClient.Symlink && Settings.Get.Provider.PreferZippedDownloads)
        {
            logger.LogDebug("Downloading files from TorBox as a zip.");

            return
            [
                new()
                {
                    RestrictedLink = BuildFakeDlUrl(id.Value, FakeDlZipSegment),
                    FileName = $"{torrent.RdName}.zip"
                }
            ];
        }

        logger.LogDebug("Downloading files from TorBox individually.");

        return downloadableFiles.Select(file => new DownloadInfo
                                {
                                    RestrictedLink = BuildFakeDlUrl(id.Value, file.Id.ToString()),
                                    FileName = Path.GetFileName(file.Path)
                                })
                                .ToList();
    }

    /// <inheritdoc />
    public Task<String> GetFileName(Download download)
    {
        // FileName is set in GetDownlaadInfos
        Debug.Assert(download.FileName != null);

        return Task.FromResult(download.FileName);
    }

    protected virtual ITorBoxNetClient GetClient(String clientId = DiConfig.TORBOX_CLIENT)
    {
        try
        {
            var apiKey = Settings.Get.Provider.ApiKey;

            if (String.IsNullOrWhiteSpace(apiKey))
            {
                throw new("TorBox API Key not set in the settings");
            }

            var httpClient = httpClientFactory.CreateClient(clientId);
            var torBoxNetClient = new TorBoxNetClient(null, httpClient);
            torBoxNetClient.UseApiAuthentication(apiKey);

            // Get the server time to fix up the timezones on results
            if (_offset == null)
            {
                var serverTime = DateTimeOffset.UtcNow;
                _offset = serverTime.Offset;
            }

            return torBoxNetClient;
        }
        catch (AggregateException ae)
        {
            foreach (var inner in ae.InnerExceptions)
            {
                logger.LogError(inner, $"The connection to TorBox has failed: {inner.Message}");
            }

            throw;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, $"The connection to TorBox has timed out: {ex.Message}");

            throw;
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, $"The connection to TorBox has timed out: {ex.Message}");

            throw;
        }
    }

    protected virtual async Task<IEnumerable<TorrentInfoResult>?> GetCurrentTorrents()
    {
        return await HandleErrors(() => GetClient().Torrents.GetCurrentAsync(true));
    }

    protected virtual async Task<IEnumerable<TorrentInfoResult>?> GetQueuedTorrents()
    {
        return await HandleErrors(() => GetClient().Torrents.GetQueuedAsync(true));
    }

    protected virtual async Task<IEnumerable<UsenetInfoResult>?> GetCurrentUsenet()
    {
        return await HandleErrors(() => GetClient().Usenet.GetCurrentAsync(true));
    }

    protected virtual async Task<IEnumerable<UsenetInfoResult>?> GetQueuedUsenet()
    {
        return await HandleErrors(() => GetClient().Usenet.GetQueuedAsync(true));
    }

    protected virtual async Task<Response<List<AvailableTorrent?>>> GetTorrentAvailability(String hash)
    {
        return await HandleErrors(() => GetClient().Torrents.GetAvailabilityAsync(hash, true));
    }

    protected virtual async Task<Response<List<AvailableUsenet?>>> GetUsenetAvailability(String hash)
    {
        return await HandleErrors(() => GetClient().Usenet.GetAvailabilityAsync(hash, true));
    }

    private DebridClientTorrent Map(TorrentInfoResult torrent)
    {
        return new()
        {
            Id = torrent.Hash,
            Filename = torrent.Name,
            OriginalFilename = torrent.Name,
            Hash = torrent.Hash,
            Bytes = torrent.Size,
            OriginalBytes = torrent.Size,
            Host = IsTorBoxDownloadReady(torrent.DownloadPresent, torrent.Cached, torrent.DownloadFinished).ToString(),
            Split = 0,
            Progress = (Int64)(torrent.Progress * 100.0),
            Status = torrent.DownloadState,
            Type = DownloadType.Torrent,
            Added = ChangeTimeZone(torrent.CreatedAt)!.Value,
            Files = (torrent.Files ?? []).Select(m => new DebridClientFile
                                         {
                                             Path = NormalizeTorBoxFilePath(m.Name),
                                             Bytes = m.Size,
                                             Id = m.Id,
                                             Selected = true,
                                             ProviderDownloadId = torrent.Id,
                                             Md5 = m.Md5,
                                             Hash = m.Hash,
                                             MimeType = m.MimeType,
                                             ShortName = m.ShortName,
                                             AbsolutePath = m.AbsolutePath,
                                             S3Path = m.S3Path
                                         })
                                         .ToList(),
            Links = [],
            Ended = ChangeTimeZone(torrent.UpdatedAt),
            Speed = torrent.DownloadSpeed,
            Seeders = torrent.Seeds
        };
    }

    private DebridClientTorrent Map(UsenetInfoResult usenet)
    {
        return new()
        {
            Id = usenet.Hash,
            Filename = usenet.Name,
            OriginalFilename = usenet.Name,
            Hash = usenet.Hash,
            Bytes = usenet.Size,
            OriginalBytes = usenet.Size,
            Host = IsTorBoxDownloadReady(usenet.DownloadPresent, usenet.Cached, usenet.DownloadFinished).ToString(),
            Split = 0,
            Progress = (Int64)(usenet.Progress * 100.0),
            Status = usenet.DownloadState,
            Type = DownloadType.Nzb,
            Added = ChangeTimeZone(usenet.CreatedAt)!.Value,
            Files = (usenet.Files ?? []).Select(m => new DebridClientFile
                                        {
                                            Path = NormalizeTorBoxFilePath(m.Name),
                                            Bytes = m.Size,
                                            Id = m.Id,
                                            Selected = true,
                                            ProviderDownloadId = usenet.Id,
                                            Md5 = m.Md5,
                                            Hash = m.Hash,
                                            MimeType = m.Mimetype,
                                            ShortName = m.ShortName,
                                            AbsolutePath = m.AbsolutePath,
                                            S3Path = m.S3Path
                                        })
                                        .ToList(),
            Links = [],
            Ended = ChangeTimeZone(usenet.UpdatedAt),
            Speed = usenet.DownloadSpeed,
            Seeders = 0
        };
    }

    private async Task<T> HandleErrors<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is RateLimitException rateLimitException)
        {
            throw rateLimitException;
        }
        catch (TorBoxException ex) when ("active_limit".Equals(ex.Error, StringComparison.OrdinalIgnoreCase))
        {
            coordinator.UpdateCooldown(TorBoxApiHost, TimeSpan.FromMinutes(2));

            throw new RateLimitException(ex.Message, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex.Message.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
        {
            coordinator.UpdateCooldown(TorBoxApiHost, TimeSpan.FromMinutes(2));

            throw new RateLimitException(ex.Message, TimeSpan.FromMinutes(2));
        }
    }

    private async Task HandleErrors(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RateLimitException)
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is RateLimitException rateLimitException)
        {
            throw rateLimitException;
        }
        catch (TorBoxException ex) when ("active_limit".Equals(ex.Error, StringComparison.OrdinalIgnoreCase))
        {
            coordinator.UpdateCooldown(TorBoxApiHost, TimeSpan.FromMinutes(2));

            throw new RateLimitException(ex.Message, TimeSpan.FromMinutes(2));
        }
        catch (Exception ex) when (ex.Message.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
        {
            coordinator.UpdateCooldown(TorBoxApiHost, TimeSpan.FromMinutes(2));

            throw new RateLimitException(ex.Message, TimeSpan.FromMinutes(2));
        }
    }

    private async Task<T> HandleAddTorrentErrors<T>(Func<Boolean, Task<T>> action)
    {
        return await HandleErrors(() => action(false));
    }

    private async Task<String> HandleAddUsenetErrors(Func<Boolean, Task<String>> action)
    {
        return await HandleErrors(() => action(false));
    }

    private DateTimeOffset? ChangeTimeZone(DateTimeOffset? dateTimeOffset)
    {
        if (_offset == null)
        {
            return dateTimeOffset;
        }

        return dateTimeOffset?.Subtract(_offset.Value).ToOffset(_offset.Value);
    }

    private async Task<DebridClientTorrent?> GetInfo(String id, DownloadType type)
    {
        return await HandleErrors(async () =>
        {
            if (type == DownloadType.Nzb)
            {
                var usenet = await GetClient().Usenet.GetHashInfoAsync(id, true);

                if (usenet != null)
                {
                    return Map(usenet);
                }
            }
            else
            {
                var result = await GetClient().Torrents.GetHashInfoAsync(id, true);

                if (result != null)
                {
                    return Map(result);
                }

                var currentTorrents = await GetClient().Torrents.GetCurrentAsync(true);
                var currentMatch = currentTorrents?.FirstOrDefault(t => String.Equals(t.Hash, id, StringComparison.OrdinalIgnoreCase));

                if (currentMatch != null)
                {
                    return Map(currentMatch);
                }

                var queuedTorrents = await GetClient().Torrents.GetQueuedAsync(true);
                var queuedMatch = queuedTorrents?.FirstOrDefault(t => String.Equals(t.Hash, id, StringComparison.OrdinalIgnoreCase));

                if (queuedMatch != null)
                {
                    return Map(queuedMatch);
                }
            }

            return null;
        });
    }

    public static void MoveHashDirContents(String extractPath, Torrent torrent)
    {
        var hashDir = Path.Combine(extractPath, torrent.Hash);

        if (Directory.Exists(hashDir))
        {
            var innerFolder = Directory.GetDirectories(hashDir)[0];

            var moveDir = extractPath;

            if (!extractPath.EndsWith(torrent.RdName!))
            {
                moveDir = hashDir;
            }

            foreach (var file in Directory.GetFiles(innerFolder))
            {
                var destFile = Path.Combine(moveDir, Path.GetFileName(file));
                File.Move(file, destFile);
            }

            foreach (var dir in Directory.GetDirectories(innerFolder))
            {
                var destDir = Path.Combine(moveDir, Path.GetFileName(dir));
                Directory.Move(dir, destDir);
            }

            if (!extractPath.Contains(torrent.RdName!))
            {
                Directory.Delete(innerFolder, true);
            }
            else
            {
                Directory.Delete(hashDir, true);
            }
        }
    }

    public static String? GetSymlinkPath(Torrent torrent, Download download)
    {
        var file = GetFileFromDownload(torrent, download);

        if (file == null)
        {
            return DownloadHelper.GetDownloadPath(torrent, download);
        }

        var providerPath = FirstNonEmpty(file.AbsolutePath, file.Path, file.ShortName, DownloadHelper.GetFileName(download));

        return NormalizeProviderPath(providerPath);
    }

    private static Int32? TryGetProviderDownloadId(Torrent torrent)
    {
        var providerDownloadId = torrent.Files.Select(file => file.ProviderDownloadId).FirstOrDefault(id => id.HasValue);

        if (!providerDownloadId.HasValue ||
            providerDownloadId.Value < Int32.MinValue ||
            providerDownloadId.Value > Int32.MaxValue)
        {
            return null;
        }

        return (Int32)providerDownloadId.Value;
    }

    private static DebridClientFile? GetFileFromDownload(Torrent torrent, Download download)
    {
        var parsedFileId = TryGetFileId(download.Path);

        if (parsedFileId != null)
        {
            var match = torrent.Files.FirstOrDefault(file => file.Id == parsedFileId.Value);

            if (match != null)
            {
                return match;
            }
        }

        var fileName = DownloadHelper.GetFileName(download);

        if (String.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return torrent.Files.FirstOrDefault(file => EndsWithFileName(file.AbsolutePath, fileName)) ??
               torrent.Files.FirstOrDefault(file => EndsWithFileName(file.Path, fileName)) ??
               torrent.Files.FirstOrDefault(file => file.ShortName?.Equals(fileName, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static Int64? TryGetFileId(String link)
    {
        var segments = link.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Mirrors BuildFakeDlUrl. The 5-segment shape after RemoveEmptyEntries is
        // [scheme, host, "fakedl", torrentId, fileSegment]. The zip variant lives at
        // the same URL pattern but is handled by a different code path, so it's not
        // a numeric file id — return null and let the caller fall back.
        if (segments is not [_, _, "fakedl", _, var fileIdStr] || fileIdStr.Equals(FakeDlZipSegment, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Int64.TryParse(fileIdStr, out var fileId) ? fileId : null;
    }

    private static Boolean EndsWithFileName(String? path, String fileName)
    {
        return !String.IsNullOrWhiteSpace(path) && path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static String? FirstNonEmpty(params String?[] values)
    {
        return values.FirstOrDefault(value => !String.IsNullOrWhiteSpace(value));
    }

    private static String? NormalizeProviderPath(String? path)
    {
        return String.IsNullOrWhiteSpace(path) ? null : path.TrimStart('/', '\\');
    }

    private static String NormalizeTorBoxFilePath(String path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 1 ? String.Join("/", parts.Skip(1)) : path;
    }

    private static Boolean IsTorBoxDownloadReady(Boolean downloadPresent, Boolean cached, Boolean downloadFinished)
    {
        return downloadPresent || cached || downloadFinished;
    }

    private TorrentStatus LogUnmappedStatus(String? status, Torrent torrent)
    {
        if (!String.IsNullOrWhiteSpace(status))
        {
            logger.LogInformation("TorBoxDebridClient encountered an unmapped status: {Status} for torrent {TorrentName} with previous status {PreviousStatus}",
                                  status,
                                  torrent.RdName,
                                  torrent.RdStatus);
        }

        // Once TorBox has acknowledged the torrent, an unknown provider-side status should not fall all the way back to
        // the local queue state. Treat it as provider-side processing so the UI does not incorrectly show
        // "Not Yet Added to Provider" while TorBox is already handling it.
        return torrent.RdStatus is null or TorrentStatus.Queued ? TorrentStatus.Processing : torrent.RdStatus.Value;
    }

    private void Log(String message, Torrent? torrent = null)
    {
        if (torrent != null)
        {
            message = $"{message} {torrent.ToLog()}";
        }

        logger.LogDebug(message);
    }
}
