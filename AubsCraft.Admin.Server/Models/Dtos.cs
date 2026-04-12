namespace AubsCraft.Admin.Server.Models;

public enum ActivityEventType
{
    PlayerJoin,
    PlayerLeave,
    Chat,
    Death,
    Advancement,
    WhitelistRejection,
    AdminAction,
    ServerMessage,
}

public record ServerStatusDto(
    bool Connected,
    int Online,
    int Max,
    List<string> Players,
    double Tps1Min,
    double Tps5Min,
    double Tps15Min);

public record WorldTimeWeatherDto(
    int TimeTicks,
    string TimeFormatted);

public record BlueMapConfigDto(
    string Url,
    bool Enabled);

public record HeightmapStreamDto(
    int X,
    int Z,
    string Heights,
    string BlockIds,
    List<string> Palette);

public record ChunkStreamDto(
    int X,
    int Z,
    string Blocks,
    List<string> Palette);

public record PlayerPositionDto(
    string Name,
    float X,
    float Y,
    float Z);

public record ActivityEventDto(
    DateTime Timestamp,
    ActivityEventType Type,
    string? PlayerName,
    string Details);

public record ChatMessageDto(
    DateTime Timestamp,
    string PlayerName,
    string Message);

public record TpsReadingDto(
    DateTime Timestamp,
    double Tps1Min,
    double Tps5Min,
    double Tps15Min);

public class PluginInfo
{
    public string FileName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Website { get; set; } = "";
    public bool Enabled { get; set; }
    public long FileSize { get; set; }
    public string? Error { get; set; }
}
