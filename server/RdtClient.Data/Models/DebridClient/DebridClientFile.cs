namespace RdtClient.Data.Models.DebridClient;

public class DebridClientFile
{
    public Int64 Id { get; set; }
    public String Path { get; set; } = default!;
    public Int64 Bytes { get; set; }
    public Boolean Selected { get; set; }
    public String? DownloadLink { get; set; }
    public Int64? ProviderDownloadId { get; set; }
    public String? Md5 { get; set; }
    public String? Hash { get; set; }
    public String? MimeType { get; set; }
    public String? ShortName { get; set; }
    public String? AbsolutePath { get; set; }
    public String? S3Path { get; set; }
}
