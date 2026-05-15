using System.Text.Json.Serialization;

namespace RdtClient.Data.Models.Internal;

public class DbCategory
{
    public String Name { get; set; } = "";

    public Boolean RemoveFromDashboard { get; set; }

    public Boolean RemoveFromProvider { get; set; }

    public Boolean RemoveLocalFiles { get; set; }

    /// Legacy field from the first Phase-1 cut: a single boolean for "dashboard + provider".
    /// Kept so old DB JSON still deserializes; CategoryParser migrates it into the three
    /// granular flags before any consumer sees the parsed list. WhenWritingDefault ensures
    /// fresh writes never re-emit the legacy bit (it always rounds to false after migration).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Boolean AutoRemoveOnFinish { get; set; }
}
