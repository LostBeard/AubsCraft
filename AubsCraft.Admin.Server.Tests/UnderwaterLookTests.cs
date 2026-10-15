using System.Numerics;
using AubsCraft.Admin.Rendering;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Under water: when the camera counts as under it, and the murk it sees.</summary>
public class UnderwaterLookTests
{
    [Test]
    public void AnEyeInWater_WithWaterAbove_IsUnderIt()
    {
        Assert.That(UnderwaterLook.IsUnder(true, true, 63.1f), Is.True);
        Assert.That(UnderwaterLook.IsUnder(true, true, 63.95f), Is.True, "water above: the whole cell is water");
    }

    [Test]
    public void AnEyeInASurfaceCell_IsUnderOnlyBelowItsLoweredTop()
    {
        // The top water cell is lowered a tenth: the surface is at .9 of the cell
        Assert.That(UnderwaterLook.IsUnder(true, false, 63.5f), Is.True);
        Assert.That(UnderwaterLook.IsUnder(true, false, 63.89f), Is.True);
        Assert.That(UnderwaterLook.IsUnder(true, false, 63.91f), Is.False, "head above the surface");
        Assert.That(UnderwaterLook.IsUnder(true, false, -63.5f), Is.True, "below zero too: the fraction is of the cell, not of the number");
        Assert.That(UnderwaterLook.IsUnder(true, false, -63.05f), Is.False, "-63.05 is the top twentieth of the cell -64..-63: above its surface");
    }

    [Test]
    public void AnEyeNotInWater_IsNotUnderIt()
    {
        Assert.That(UnderwaterLook.IsUnder(false, true, 63.5f), Is.False, "water above but not here: in air under a ceiling of water, a bubble");
        Assert.That(UnderwaterLook.IsUnder(false, false, 63.5f), Is.False);
    }

    [Test]
    public void TheMurk_IsBlueGreen_DarkerAtNight_NeverBlack()
    {
        var noon = UnderwaterLook.FogColor(1f);
        var night = UnderwaterLook.FogColor(0f);
        Assert.That(noon.Z, Is.GreaterThan(noon.X).And.GreaterThan(noon.Y), "bluest");
        Assert.That(night.Z, Is.LessThan(noon.Z), "darker at night");
        Assert.That(night.Length(), Is.GreaterThan(0.05f), "never black");
        Assert.That(UnderwaterLook.FogColor(5f), Is.EqualTo(noon), "never brighter than noon");
        Assert.That(UnderwaterLook.FogEnd, Is.LessThan(40f).And.GreaterThan(10f), "a few dozen blocks");
        Assert.That(UnderwaterLook.FogStart, Is.LessThan(UnderwaterLook.FogEnd));
    }
}
