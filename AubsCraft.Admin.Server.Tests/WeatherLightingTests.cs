using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Rain greys and dims the day, a storm more; clear weather is the clear day exactly.</summary>
public class WeatherLightingTests
{
    [Test]
    public void NoRain_IsTheClearDay_ExactlyAtAnyHour()
    {
        foreach (float t in new[] { 0f, 0.2f, 0.25f, 0.5f, 0.8f })
            Assert.That(DayLighting.At(t, 0f, 0f), Is.EqualTo(DayLighting.At(t)), $"time {t}");
        // Thunder without rain is no weather at all (a storm is rain with thunder)
        Assert.That(DayLighting.At(0.5f, 0f, 1f), Is.EqualTo(DayLighting.At(0.5f)));
    }

    [Test]
    public void Rain_TakesSunlight_AndGreysTheSky_AtNoon()
    {
        var clear = DayLighting.At(0.5f);
        var rain = DayLighting.At(0.5f, 1f, 0f);
        Assert.That(rain.SunStrength, Is.LessThan(clear.SunStrength * 0.5f), "most of the sun is gone");
        Assert.That(rain.FogColor.Length(), Is.LessThan(clear.FogColor.Length()), "the sky is darker");
        // Greyer: the spread of the channels is smaller than the clear sky's blue
        float Spread(System.Numerics.Vector3 c) => Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z));
        Assert.That(Spread(rain.FogColor), Is.LessThan(Spread(clear.FogColor) * 0.5f), "and grey");
        Assert.That(rain.SunDirection, Is.EqualTo(clear.SunDirection), "the sun is where it was, only dimmer");
    }

    [Test]
    public void MoreRain_IsDarkerEachStep_AndAStormDarkerThanRain()
    {
        float last = float.MaxValue;
        foreach (float r in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            float sun = DayLighting.At(0.5f, r, 0f).SunStrength;
            Assert.That(sun, Is.LessThanOrEqualTo(last), $"rain {r}");
            last = sun;
        }
        Assert.That(DayLighting.At(0.5f, 1f, 1f).SunStrength, Is.LessThan(DayLighting.At(0.5f, 1f, 0f).SunStrength));
        Assert.That(DayLighting.At(0.5f, 1f, 1f).FogColor.Length(), Is.LessThan(DayLighting.At(0.5f, 1f, 0f).FogColor.Length()));
    }

    [Test]
    public void ARainyNight_IsStillNight_NotACloudyNoon()
    {
        var night = DayLighting.At(0f, 1f, 0f);
        var noon = DayLighting.At(0.5f, 1f, 0f);
        Assert.That(night.FogColor.Length(), Is.LessThan(noon.FogColor.Length() * 0.5f));
        Assert.That(night.SunStrength, Is.GreaterThan(0f), "never fully dark");
    }

    [Test]
    public void OutOfRangeWeather_IsClamped()
        => Assert.That(DayLighting.At(0.5f, 7f, 7f), Is.EqualTo(DayLighting.At(0.5f, 1f, 1f)));
}
