using System.Text.Json.Serialization;

namespace RdtClient.Data.Models.QBittorrent;

public class TorrentTracker
{
    [JsonPropertyName("url")]
    public String? Url { get; set; }

    [JsonPropertyName("status")]
    public Int32? Status { get; set; }

    [JsonPropertyName("tier")]
    public Int32? Tier { get; set; }

    [JsonPropertyName("num_peers")]
    public Int32? NumPeers { get; set; }

    [JsonPropertyName("num_seeds")]
    public Int32? NumSeeds { get; set; }

    [JsonPropertyName("num_leeches")]
    public Int32? NumLeeches { get; set; }

    [JsonPropertyName("num_downloaded")]
    public Int32? NumDownloaded { get; set; }

    [JsonPropertyName("msg")]
    public String? Msg { get; set; }
}

