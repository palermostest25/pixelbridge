using System.Text.Json.Nodes;

namespace PixelBridge.Core;

public sealed record PhoneCamera(string Id, string Facing, double MinZoom, double MaxZoom, bool Optical, bool Electronic);
public sealed record CameraLens(string CameraId, string Title, double Zoom)
{
    public override string ToString() => Title;
}

/// <summary>Capabilities, controls and geometry shared by both desktop clients.</summary>
public sealed class CameraControls
{
    public IReadOnlyList<PhoneCamera> Cameras { get; }
    public IReadOnlyList<CameraLens> Lenses { get; }
    public PhoneCamera? Current { get; }
    public CameraControls(JsonObject? capabilities, BridgeSettings settings)
    {
        Cameras = (capabilities?["cameras"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(c => c["id"] != null).Select(c => new PhoneCamera((string)c["id"]!, (string?)c["facing"] ?? "back",
                Math.Max(.1, (double?)c["minZoom"] ?? 1), Math.Max(1, (double?)c["maxZoom"] ?? 1),
                (bool?)c["opticalStabilization"] ?? false, (bool?)c["videoStabilization"] ?? false)).ToArray();
        Current = Cameras.FirstOrDefault(c => c.Id == settings.CameraId);
        Lenses = Cameras.SelectMany(c => c.Facing == "front" ? new[] { new CameraLens(c.Id, "Front", Math.Clamp(1, c.MinZoom, c.MaxZoom)) }
            : c.MinZoom < .95 ? new[] { new CameraLens(c.Id, "Ultra wide", c.MinZoom), new CameraLens(c.Id, "Wide", 1) }
            : new[] { new CameraLens(c.Id, "Back", 1) }).ToArray();
    }
    public object UpdateOptions(BridgeSettings settings) => new { updateOnly = true,
        zoomRatio = Math.Clamp(settings.CameraZoom, Current?.MinZoom ?? 1, Current?.MaxZoom ?? 1), stabilization = settings.CameraStabilization };
    public static int Rotation(BridgeSettings settings, JsonObject? config) =>
        settings.CameraOrientation == "auto" ? (int?)config?["rotationDegrees"] ?? 0 : int.TryParse(settings.CameraOrientation, out var r) ? r : 0;
    public static string StabilizationStatus(JsonObject? config) => config?["opticalStabilizationActive"] == null ? "Waiting for camera confirmation"
        : (bool?)config["opticalStabilizationActive"] == true ? "Optical stabilization active"
        : (bool?)config["videoStabilizationActive"] == true ? "Electronic stabilization active" : "Stabilization off";
    public static bool FormatChanged(JsonObject? previous, JsonObject next) => previous == null ||
        new[] { "cameraId", "width", "height", "codec" }.Any(k => previous[k]?.ToJsonString() != next[k]?.ToJsonString());
}
