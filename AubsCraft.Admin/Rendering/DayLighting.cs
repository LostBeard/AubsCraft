using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The sun, ambient light and fog for a time of day (0 = midnight, 0.25 = sunrise, 0.5 = noon). One definition for both
/// renderers, so the old and the VoxelEngine path light the world with the same numbers.
/// </summary>
public readonly record struct DayLighting(Vector3 SunDirection, Vector3 Ambient, Vector3 SunColor, Vector3 FogColor, float SunStrength)
{
    /// <summary>Fog starts and ends at these distances from the camera (blocks); the blend is quadratic.</summary>
    public const float FogStart = 250f;
    public const float FogEnd = 450f;

    /// <summary>
    /// Chunks from the camera's chunk to where terrain is fully fogged: ceil((FogEnd + 24) / 16). The 24 blocks are a chunk's half
    /// diagonal (11.3) and the camera's offset inside its own chunk, rounded up, so a chunk whose nearest corner is still short of the fog
    /// end is never dropped. Terrain beyond this many chunks is the fog colour and is not drawn.
    /// </summary>
    public const int FogEndChunks = 30;

    /// <summary>
    /// The light at a time of day, in the weather: <paramref name="rain"/> (0 clear .. 1 pouring) greys the sky and fog toward the colour of cloud and takes most of the sun's strength;
    /// <paramref name="thunder"/> (0..1, a storm) darkens it further. Both at 0 is the clear day exactly.
    /// </summary>
    public static DayLighting At(float timeOfDay, float rain, float thunder)
    {
        var clear = At(timeOfDay);
        rain = Math.Clamp(rain, 0f, 1f);
        thunder = Math.Clamp(thunder, 0f, 1f) * rain;
        if (rain <= 0f) return clear;
        // How bright the day is decides how bright the cloud is: a rainy night is still night
        float day = Math.Clamp(clear.SunStrength / 0.6f, 0f, 1f);
        var cloud = new Vector3(0.52f, 0.54f, 0.58f) * (0.18f + 0.82f * day) * (1f - 0.45f * thunder);
        var fog = Vector3.Lerp(clear.FogColor, cloud, 0.8f * rain);
        float sun = clear.SunStrength * (1f - 0.62f * rain - 0.2f * thunder);
        var sunColour = Vector3.Lerp(clear.SunColor, new Vector3(0.7f, 0.72f, 0.76f), 0.7f * rain);
        var ambient = clear.Ambient * (1f - 0.18f * rain - 0.12f * thunder);
        return new DayLighting(clear.SunDirection, ambient, sunColour, fog, Math.Max(sun, 0.03f));
    }

    public static DayLighting At(float timeOfDay)
    {
        float angle = timeOfDay * MathF.Tau;
        float sunY = -MathF.Cos(angle);
        float sx = MathF.Sin(angle) * 0.7f;
        float len = MathF.Sqrt(sx * sx + sunY * sunY + 0.35f * 0.35f);
        var sun = new Vector3(sx / len, sunY / len, 0.35f / len);

        float elevation = -MathF.Cos(angle);
        float dayFactor = Math.Clamp(elevation * 2f + 0.5f, 0f, 1f);
        float dawnFactor = Math.Clamp(1f - MathF.Abs(elevation) * 4f, 0f, 1f);

        float ar = Lerp(0.05f, 0.30f, dayFactor); ar = Lerp(ar, 0.25f, dawnFactor * 0.6f);
        float ag = Lerp(0.06f, 0.32f, dayFactor); ag = Lerp(ag, 0.18f, dawnFactor * 0.6f);
        float ab = Lerp(0.12f, 0.38f, dayFactor); ab = Lerp(ab, 0.12f, dawnFactor * 0.6f);

        float sr = Lerp(0.15f, 1.0f, dayFactor); sr = Lerp(sr, 1.0f, dawnFactor * 0.8f);
        float sg = Lerp(0.18f, 0.95f, dayFactor); sg = Lerp(sg, 0.55f, dawnFactor * 0.8f);
        float sb = Lerp(0.35f, 0.85f, dayFactor); sb = Lerp(sb, 0.25f, dawnFactor * 0.8f);

        float fr = Lerp(0.04f, 0.65f, dayFactor); fr = Lerp(fr, 0.85f, dawnFactor * 0.7f);
        float fg = Lerp(0.05f, 0.80f, dayFactor); fg = Lerp(fg, 0.50f, dawnFactor * 0.7f);
        float fb = Lerp(0.10f, 0.95f, dayFactor); fb = Lerp(fb, 0.30f, dawnFactor * 0.7f);
        float sunStrength = Math.Clamp(dayFactor * 0.55f + 0.05f, 0.05f, 0.55f);

        return new DayLighting(sun, new Vector3(ar, ag, ab), new Vector3(sr, sg, sb), new Vector3(fr, fg, fb), sunStrength);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
