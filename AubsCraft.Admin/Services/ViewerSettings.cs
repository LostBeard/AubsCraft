namespace AubsCraft.Admin.Services;

/// <summary>What a viewer user can set in the viewer's own menu, remembered in the browser. Plain data: it crosses to the render worker and back.</summary>
/// <param name="DrawDistance">Chunks; 0: it adapts to the frame rate.</param>
/// <param name="FlySpeed">Blocks a second.</param>
/// <param name="FollowDistance">How far behind a followed player the camera stands (blocks).</param>
public record ViewerSettings(
    bool Stats = true, bool Compass = true, bool Minimap = true, bool PlayerList = true, bool Chat = true, bool NameTags = true, bool PlayerBars = true,
    int DrawDistance = 0, float Fov = 70f, float FlySpeed = 60f, float FollowDistance = 5f);

/// <summary>What the page knows and the worker's menu shows: only the page has the DOM, the sockets for voice and the headset.</summary>
public record ViewerPageState(
    bool Fullscreen = false, bool Lighting = true, bool Shading = true, bool VoiceOn = false, bool MicOn = false, string VoiceText = "",
    bool VrSupported = false, bool ArSupported = false, bool BlueMap = false, bool Admin = false, bool Editing = false,
    string[]? ServerIds = null, string[]? ServerLabels = null, string SelectedServer = "");
