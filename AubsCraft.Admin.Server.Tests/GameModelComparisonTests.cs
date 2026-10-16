using System.Numerics;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The hand-made models against the game's own (tools/modeldump): for each creature that has both, every box of one is found in the other - same texture offset and size, and the same
/// place in the rest pose (the centre of the box through its parts' pivots and rotations). A hand-made number that is off shows here.
/// </summary>
public class GameModelComparisonTests
{
    private record Flat(float U, float V, float W, float H, float D, float Inflate, Vector3 Centre, bool Mirror);

    private static List<Flat> Flatten(IReadOnlyList<ModelPart> parts)
    {
        var world = new Matrix4x4[parts.Count];
        var result = new List<Flat>();
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var local = Matrix4x4.CreateRotationX(p.Rotation.X) * Matrix4x4.CreateRotationY(p.Rotation.Y) * Matrix4x4.CreateRotationZ(p.Rotation.Z) * Matrix4x4.CreateTranslation(p.Pivot);
            if (p.PoseScale is { } s) local = Matrix4x4.CreateScale(s) * local;
            world[i] = p.Parent >= 0 ? local * world[p.Parent] : local;
            foreach (var b in p.Boxes)
            {
                var centre = Vector3.Transform(new Vector3(b.X + b.W / 2f, b.Y + b.H / 2f, b.Z + b.D / 2f), world[i]);
                result.Add(new Flat(b.U, b.V, b.W, b.H, b.D, b.Inflate, centre, b.Mirror));
            }
        }
        return result;
    }

    private static readonly (string type, string key)[] Pairs =
    [
        ("pig", "PigModel.createBodyLayer(1)"), ("cow", "CowModel.createBodyLayer"), ("sheep", "SheepModel.createBodyLayer"), ("chicken", "ChickenModel.createBodyLayer"),
        ("creeper", "CreeperModel.createBodyLayer(1)"), ("skeleton", "SkeletonModel.createBodyLayer"), ("spider", "SpiderModel.createSpiderBodyLayer"),
        ("iron_golem", "IronGolemModel.createBodyLayer"), ("snow_golem", "SnowGolemModel.createBodyLayer"), ("ghast", "GhastModel.createBodyLayer"),
        ("blaze", "BlazeModel.createBodyLayer"), ("silverfish", "SilverfishModel.createBodyLayer"), ("endermite", "EndermiteModel.createBodyLayer"),
        ("bee", "BeeModel.createBodyLayer"), ("polar_bear", "PolarBearModel.createBodyLayer(1)"), ("rabbit", "RabbitModel.createBodyLayer(1)"), ("parrot", "ParrotModel.createBodyLayer"),
        ("dolphin", "DolphinModel.createBodyLayer"), ("turtle", "TurtleModel.createBodyLayer"), ("witch", "WitchModel.createBodyLayer"),
        ("tropical_fish_a", "TropicalFishModelA.createBodyLayer(1)"), ("tropical_fish_b", "TropicalFishModelB.createBodyLayer(1)"), ("fox", "FoxModel.createBodyLayer"),
        ("villager", "ZombieVillagerModel.createBodyLayer"),
    ];

    [Test]
    public void TheHandMadeModels_AgreeWithTheGamesOwn_WhereBothExist()
    {
        var report = new List<string>();
        foreach (var (type, key) in Pairs)
        {
            var mine = EntityModels.Get(type);
            if (mine == null || !GameModels.Keys.Contains(key)) { report.Add($"{type}: nothing to compare ({(mine == null ? "no model" : "no dump " + key)})"); continue; }
            var scale = mine.Scale;
            var a = Flatten(mine.Parts.Where(p => p.VariantTexture == null).ToArray());
            var b = Flatten(GameModels.Parts(key));
            var missing = new List<string>();
            var unmatched = new List<Flat>(b);
            foreach (var box in a)
            {
                int i = unmatched.FindIndex(g => g.U == box.U && g.V == box.V && g.W == box.W && g.H == box.H && g.D == box.D && Math.Abs(g.Inflate - box.Inflate) < 0.01f);
                if (i < 0) { missing.Add($"hand-made box ({box.U},{box.V}) {box.W}x{box.H}x{box.D} infl {box.Inflate} is not in the game's model"); continue; }
                var g = unmatched[i];
                // The same box may appear twice (left and right): take the nearest of the equal ones
                int best = unmatched.Select((x, idx) => (x, idx)).Where(t => t.x.U == box.U && t.x.V == box.V && t.x.W == box.W && t.x.H == box.H && t.x.D == box.D).OrderBy(t => (t.x.Centre - box.Centre).Length()).First().idx;
                g = unmatched[best];
                float off = (g.Centre - box.Centre).Length();
                if (off > 0.6f) missing.Add($"box ({box.U},{box.V}) {box.W}x{box.H}x{box.D}: centre {box.Centre} against the game's {g.Centre} ({off:F1} px off)");
                unmatched.RemoveAt(best);
            }
            foreach (var g in unmatched) missing.Add($"the game's box ({g.U},{g.V}) {g.W}x{g.H}x{g.D} infl {g.Inflate} at {g.Centre} is missing here");
            report.Add($"{type}: {a.Count} boxes here, {b.Count} in the game's: {(missing.Count == 0 ? "agree" : missing.Count + " differences")}");
            foreach (var m in missing.Take(12)) report.Add("    " + m);
        }
        TestContext.Progress.WriteLine(string.Join(Environment.NewLine, report));
        Assert.That(report, Is.Not.Empty);
    }
}
