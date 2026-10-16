using System.Numerics;
using AubsCraft.Admin.Editing;
using AubsCraft.Live.Model.Editing;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Building from the viewer: how a block is turned when it is put down, the plans a click makes, the history, and the outline drawn round the block pointed at.</summary>
public class EditSessionTests
{
    // yaw 0 looks east (+X), 90 south (+Z), 180 west, -90 north - the viewer's camera
    [TestCase(0, "east")]
    [TestCase(90, "south")]
    [TestCase(180, "west")]
    [TestCase(-90, "north")]
    [TestCase(30, "east")]
    [TestCase(60, "south")]
    [TestCase(-170, "west")]
    public void TheCameraYawIsACompassDirection(double yaw, string expected) => Assert.That(PlacementRules.LookDirection(yaw), Is.EqualTo(expected));

    [Test]
    public void AStairFacesThePlayer_AndSitsOnTheHalfThatWasClicked()
    {
        // Looking east at the side of a wall: the stair faces west (toward the player); the lower half of the wall's block gives a bottom stair
        Assert.That(PlacementRules.Orient("minecraft:oak_stairs", -1, 0, 0, 0.2, 0), Is.EqualTo("minecraft:oak_stairs[facing=west,half=bottom]"));
        Assert.That(PlacementRules.Orient("minecraft:oak_stairs", -1, 0, 0, 0.8, 0), Is.EqualTo("minecraft:oak_stairs[facing=west,half=top]"));
        Assert.That(PlacementRules.Orient("minecraft:oak_stairs", 0, 1, 0, 1.0, 90), Is.EqualTo("minecraft:oak_stairs[facing=north,half=bottom]"), "on top of a block, looking south");
        Assert.That(PlacementRules.Orient("minecraft:oak_stairs", 0, -1, 0, 0.0, 90), Is.EqualTo("minecraft:oak_stairs[facing=north,half=top]"), "under a block");
    }

    [Test]
    public void ASlabGoesWhereTheClickWas()
    {
        Assert.That(PlacementRules.Orient("minecraft:stone_slab", 0, 1, 0, 1.0, 0), Is.EqualTo("minecraft:stone_slab[type=bottom]"));
        Assert.That(PlacementRules.Orient("minecraft:stone_slab", 0, -1, 0, 0.0, 0), Is.EqualTo("minecraft:stone_slab[type=top]"));
        Assert.That(PlacementRules.Orient("minecraft:stone_slab", 1, 0, 0, 0.75, 0), Is.EqualTo("minecraft:stone_slab[type=top]"));
    }

    [Test]
    public void ALogLiesAlongTheFaceItWasPutOn()
    {
        Assert.That(PlacementRules.Orient("minecraft:oak_log", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:oak_log[axis=y]"));
        Assert.That(PlacementRules.Orient("minecraft:oak_log", 1, 0, 0, .5, 0), Is.EqualTo("minecraft:oak_log[axis=x]"));
        Assert.That(PlacementRules.Orient("minecraft:stripped_birch_wood", 0, 0, -1, .5, 0), Is.EqualTo("minecraft:stripped_birch_wood[axis=z]"));
        Assert.That(PlacementRules.Orient("minecraft:hay_block", 1, 0, 0, .5, 0), Is.EqualTo("minecraft:hay_block[axis=x]"));
    }

    [Test]
    public void ATorchOnAWallIsAWallTorch_AndOnTheGroundIsNot()
    {
        Assert.That(PlacementRules.Orient("minecraft:torch", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:torch"));
        Assert.That(PlacementRules.Orient("minecraft:torch", 1, 0, 0, .5, 0), Is.EqualTo("minecraft:wall_torch[facing=east]"));
        Assert.That(PlacementRules.Orient("minecraft:soul_torch", 0, 0, -1, .5, 0), Is.EqualTo("minecraft:soul_wall_torch[facing=north]"));
        Assert.That(PlacementRules.Orient("minecraft:redstone_torch", 0, 0, 1, .5, 0), Is.EqualTo("minecraft:redstone_wall_torch[facing=south]"));
        Assert.That(PlacementRules.Orient("minecraft:ladder", 0, 0, 1, .5, 0), Is.EqualTo("minecraft:ladder[facing=south]"));
    }

    [Test]
    public void ABlockWithAFront_FacesThePlayer_AndAPlainBlockIsUntouched()
    {
        Assert.That(PlacementRules.Orient("minecraft:furnace", 0, 1, 0, 1, 180), Is.EqualTo("minecraft:furnace[facing=east]"));
        Assert.That(PlacementRules.Orient("minecraft:chest", 0, 1, 0, 1, -90), Is.EqualTo("minecraft:chest[facing=south]"));
        Assert.That(PlacementRules.Orient("minecraft:red_glazed_terracotta", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:red_glazed_terracotta[facing=west]"));
        Assert.That(PlacementRules.Orient("minecraft:stone", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:stone"));
        Assert.That(PlacementRules.Orient("minecraft:air", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:air"));
    }

    [Test]
    public void ABlockWithItsOwnProperties_IsPlacedAsWritten()
    {
        Assert.That(PlacementRules.Orient("minecraft:oak_stairs[facing=north,half=top]", -1, 0, 0, .2, 0), Is.EqualTo("minecraft:oak_stairs[facing=north,half=top]"));
        Assert.That(PlacementRules.Orient("minecraft:oak_log[axis=z]", 0, 1, 0, 1, 0), Is.EqualTo("minecraft:oak_log[axis=z]"));
    }

    // ---- the session ----

    private static EditTarget Target(int x, int y, int z, int nx, int ny, int nz, string block = "minecraft:stone", double hy = 0.5)
        => new(x, y, z, nx, ny, nz, block, 3, x + 0.5, y + hy, z + 0.5);

    [Test]
    public void BreakingTheBlockPointedAt_IsASetToAir()
    {
        var s = new EditSession { Target = Target(4, -60, 7, 0, 1, 0, "minecraft:oak_planks") };
        var plan = s.PlanBreak()!;
        Assert.That(plan.Ops.Single().Op, Is.EqualTo("set"));
        Assert.That(plan.Ops.Single().At, Is.EqualTo(new[] { 4, -60, 7 }));
        Assert.That(plan.Ops.Single().Block, Is.EqualTo("minecraft:air"));
        Assert.That(plan.Label, Is.EqualTo("break oak planks"));
        Assert.That(new EditSession().PlanBreak(), Is.Null, "nothing pointed at");
    }

    [Test]
    public void PlacingPutsTheBlockInHandOnTheFaceThatWasHit_Turned()
    {
        var s = new EditSession { Target = Target(4, -60, 7, 1, 0, 0, hy: 0.8) };
        s.Selected = 8;   // oak stairs
        var plan = s.PlanPlace(cameraYawDegrees: 180)!;
        Assert.That(plan.Ops.Single().At, Is.EqualTo(new[] { 5, -60, 7 }), "against the east face");
        Assert.That(plan.Ops.Single().Block, Is.EqualTo("minecraft:oak_stairs[facing=east,half=top]"));
        s.Selected = 0;
        Assert.That(s.PlanPlace(0)!.Ops.Single().Block, Is.EqualTo("minecraft:stone"));
    }

    [Test]
    public void NothingIsPlacedOutsideTheWorld_AndAirInHandBreaks()
    {
        var s = new EditSession { Target = Target(0, 319, 0, 0, 1, 0) };
        Assert.That(s.PlanPlace(0), Is.Null, "above the top");
        s.Target = Target(0, -64, 0, 0, -1, 0);
        Assert.That(s.PlanPlace(0), Is.Null, "below the bottom");
        s.Target = Target(0, 10, 0, 0, 1, 0);
        s.SetSelectedBlock(EditSession.Air);
        Assert.That(s.PlanPlace(0)!.Ops.Single().Block, Is.EqualTo(EditSession.Air));
        Assert.That(s.PlanPlace(0)!.Label, Does.StartWith("break"));
    }

    [Test]
    public void TheEyedropperTakesTheBlockPointedAt()
    {
        var s = new EditSession { Target = Target(0, 0, 0, 0, 1, 0, "minecraft:oak_log[axis=x]"), Selected = 3 };
        Assert.That(s.PickBlock(), Is.True);
        Assert.That(s.Hotbar[3], Is.EqualTo("minecraft:oak_log[axis=x]"));
        Assert.That(s.Hotbar[0], Is.EqualTo("minecraft:stone"), "the other slots are untouched");
        s.Target = Target(0, 0, 0, 0, 1, 0, "minecraft:air");
        Assert.That(s.PickBlock(), Is.False);
        s.Target = null;
        Assert.That(s.PickBlock(), Is.False);
    }

    [Test]
    public void UndoAndRedoWalkTheHistory_AndANewEditForgetsWhatWasUndone()
    {
        var s = new EditSession();
        Assert.That(s.NextUndo, Is.Null);
        s.Record("a"); s.Record("b"); s.Record("c");
        Assert.That(s.NextUndo, Is.EqualTo("c"));
        s.Undone(); s.Undone();
        Assert.That((s.NextUndo, s.NextRedo, s.Undoable, s.Redoable), Is.EqualTo(("a", "b", 1, 2)));
        s.Redone();
        Assert.That((s.NextUndo, s.NextRedo), Is.EqualTo(("b", "c")));
        s.Undone();
        s.Record("d");   // a new edit after undoing: b and c are gone
        Assert.That((s.NextUndo, s.NextRedo, s.Redoable), Is.EqualTo(("d", (string?)null, 0)));
        s.Undone(); s.Undone(); s.Undone(); s.Undone();
        Assert.That(s.NextUndo, Is.Null);
        Assert.That(s.Undoable, Is.Zero);
    }

    [Test]
    public void TheHistoryIsBounded()
    {
        var s = new EditSession();
        for (int i = 0; i < 500; i++) s.Record("e" + i);
        Assert.That(s.Undoable, Is.EqualTo(200));
        Assert.That(s.NextUndo, Is.EqualTo("e499"));
    }

    [TestCase("minecraft:oak_stairs[facing=north]", "oak stairs")]
    [TestCase("minecraft:stone", "stone")]
    [TestCase("mod:odd_thing", "odd thing")]
    public void ABlockHasAReadableName(string block, string expected) => Assert.That(EditSession.DisplayName(block), Is.EqualTo(expected));

    // ---- the outline ----

    [Test]
    public void TheOutlineOfABlock_IsTwelveBarsAlongItsEdges()
    {
        var parts = new List<PartDraw>();
        SelectionBoxes.Outline(10, -60, 20, 1, 1, 1, new Vector3(0.1f, 0.1f, 0.1f), parts);
        Assert.That(parts.Count, Is.EqualTo(12));
        Assert.That(parts.All(p => p.Texture == "white"), Is.True);

        // Each bar, in blocks, lies on the block's edges: take its two corners and check they are inside the block grown by the bar
        foreach (var p in parts)
        {
            var lo = Vector3.Transform(p.Origin, p.Model);
            var hi = Vector3.Transform(p.Origin + p.Size, p.Model);
            Assert.That(lo.X, Is.GreaterThanOrEqualTo(10 - 0.05f)); Assert.That(hi.X, Is.LessThanOrEqualTo(11 + 0.05f));
            Assert.That(lo.Y, Is.GreaterThanOrEqualTo(-60 - 0.05f)); Assert.That(hi.Y, Is.LessThanOrEqualTo(-59 + 0.05f));
            Assert.That(lo.Z, Is.GreaterThanOrEqualTo(20 - 0.05f)); Assert.That(hi.Z, Is.LessThanOrEqualTo(21 + 0.05f));
            // Two of the three sides are thin, one is a full edge
            var size = hi - lo;
            Assert.That(new[] { size.X, size.Y, size.Z }.Count(v => v > 0.9f), Is.EqualTo(1), "one long side");
            Assert.That(new[] { size.X, size.Y, size.Z }.Count(v => v < 0.1f), Is.EqualTo(2), "two thin sides");
        }
        // Four bars along each axis
        Assert.That(parts.Count(p => p.Size.X > 10), Is.EqualTo(4));
        Assert.That(parts.Count(p => p.Size.Y > 10), Is.EqualTo(4));
        Assert.That(parts.Count(p => p.Size.Z > 10), Is.EqualTo(4));
    }

    [Test]
    public void TheOutlineOfABox_SpansAllItsBlocks()
    {
        var parts = new List<PartDraw>();
        SelectionBoxes.Outline(0, 0, 0, 4, 2, 3, Vector3.One, parts);
        var xs = parts.Where(p => p.Size.X > 10).Select(p => p.Size.X).Distinct().Single();
        var ys = parts.Where(p => p.Size.Y > 10).Select(p => p.Size.Y).Distinct().Single();
        var zs = parts.Where(p => p.Size.Z > 10).Select(p => p.Size.Z).Distinct().Single();
        Assert.That((xs / 16, ys / 16, zs / 16), Is.EqualTo((4f, 2f, 3f)).Within(0.05f));
    }

    // ---- the messages ----

    [Test]
    public void AnEditRequest_IsWrittenAsTheWebStyleJsonThePanelReads()
    {
        var request = new EditRequest
        {
            Label = "a \"quoted\" label",
            Ops =
            [
                new EditOpDto { Op = "set", At = [1, -60, 3], Block = "minecraft:stone" },
                new EditOpDto { Op = "fill", From = [0, 0, 0], To = [2, 2, 2], Block = "minecraft:glass", Only = "minecraft:air", Hollow = true },
                new EditOpDto { Op = "blocks", At = [5, 5, 5], Palette = ["minecraft:dirt"], Data = "AAAAAAAAAAA=" },
            ],
        };
        var json = EditJson.Write(request);
        var back = System.Text.Json.JsonSerializer.Deserialize<EditRequest>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.That(back.Label, Is.EqualTo(request.Label));
        Assert.That(back.Ops.Count, Is.EqualTo(3));
        Assert.That(back.Ops[0].At, Is.EqualTo(new[] { 1, -60, 3 }));
        Assert.That(back.Ops[1].Hollow, Is.True);
        Assert.That(back.Ops[1].Only, Is.EqualTo("minecraft:air"));
        Assert.That(back.Ops[1].To, Is.EqualTo(new[] { 2, 2, 2 }));
        Assert.That(back.Ops[2].Palette, Is.EqualTo(new[] { "minecraft:dirt" }));
        Assert.That(back.Ops[2].Data, Is.EqualTo("AAAAAAAAAAA="));
        Assert.That(json, Does.Not.Contain("hollow\":false"), "what is not set is not sent");
    }

    [Test]
    public void ThePanelsPickAndEditReplies_AreReadByHand()
    {
        var pick = EditJson.ReadPick("""{"hit":true,"x":-9,"y":-61,"z":8,"nx":0,"ny":1,"nz":0,"block":"minecraft:grass_block[snowy=false]","distance":3.25,"hx":-8.5,"hy":-60,"hz":8.5}""");
        Assert.That((pick.Hit, pick.X, pick.Y, pick.Z, pick.Ny, pick.Block, pick.Distance, pick.Hy), Is.EqualTo((true, -9, -61, 8, 1, "minecraft:grass_block[snowy=false]", 3.25, -60.0)));
        Assert.That(EditJson.ReadPick("""{"hit":false}""").Hit, Is.False);
        Assert.That(EditTarget.Of(EditJson.ReadPick("""{"hit":false}""")), Is.Null);
        Assert.That(EditTarget.Of(pick)!.Value.PlaceAt, Is.EqualTo((-9, -60, 8)));

        var ok = EditJson.ReadResult("""{"ok":true,"edit":"abc","placed":16,"skipped":2,"applied":true,"error":null}""");
        Assert.That(ok, Is.EqualTo(new EditResult(true, "abc", 16, 2, true, null)));
        var failed = EditJson.ReadResult("""{"ok":false,"edit":null,"placed":0,"skipped":0,"applied":false,"error":"Unknown block: x"}""");
        Assert.That((failed.Ok, failed.Error), Is.EqualTo((false, "Unknown block: x")));
        Assert.That(EditJson.ReadError("""{"error":"y must be between -64 and 319"}"""), Is.EqualTo("y must be between -64 and 319"));
        Assert.That(EditJson.ReadError("plain text"), Is.EqualTo("plain text"));
    }

    [Test]
    public void ThePaletteOffersBlocksThatCanBePlaced_Sorted()
    {
        var offered = EditSession.PlaceableBlocks(["minecraft:stone", "minecraft:air", "minecraft:water", "minecraft:oak_wall_sign", "minecraft:oak_sign", "minecraft:wall_torch", "minecraft:torch", "minecraft:piston_head", "minecraft:fire", "minecraft:gold_block", "minecraft:oak_stairs[facing=north]", "mod:thing"]);
        Assert.That(offered, Is.EqualTo(new[] { "minecraft:gold_block", "minecraft:oak_sign", "minecraft:stone", "minecraft:torch", "mod:thing" }));
    }

    // ---- a box selected, and blueprints on their way down ----

    [Test]
    public void TwoClicksMakeABox_AThirdStartsAnother()
    {
        var s = new EditSession { Tool = EditSession.Tools.Select };
        Assert.That(s.ClickSelect(), Is.False, "nothing pointed at");
        s.Target = Target(10, 5, 20, 0, 1, 0);
        Assert.That(s.ClickSelect(), Is.True);
        Assert.That(s.HasSelection, Is.False, "one corner is not a box");
        s.Target = Target(13, 7, 18, 0, 1, 0);
        s.ClickSelect();
        Assert.That(s.SelectionBox, Is.EqualTo((10, 5, 18, 4, 3, 3)), "any two corners, lowest first");
        s.Target = Target(0, 0, 0, 0, 1, 0);
        s.ClickSelect();
        Assert.That(s.HasSelection, Is.False, "a click after a box starts a new one");
        Assert.That(s.CornerA, Is.EqualTo((0, 0, 0)));
        s.ClearSelection();
        Assert.That(s.CornerA, Is.Null);
    }

    [Test]
    public void TheSelectionCanBeFilledClearedAndCopied()
    {
        var s = new EditSession { Tool = EditSession.Tools.Select };
        Assert.That(s.PlanFill(), Is.Null);
        s.Target = Target(10, 5, 20, 0, 1, 0); s.ClickSelect();
        s.Target = Target(12, 6, 22, 0, 1, 0); s.ClickSelect();
        s.Selected = 4;   // glass
        var fill = s.PlanFill()!.Ops.Single();
        Assert.That((fill.Op, fill.From, fill.To, fill.Block, fill.Hollow), Is.EqualTo(("fill", new[] { 10, 5, 20 }, new[] { 12, 6, 22 }, "minecraft:glass", false)));
        Assert.That(s.PlanFill(hollow: true)!.Ops.Single().Hollow, Is.True);
        var clear = s.PlanClear()!.Ops.Single();
        Assert.That((clear.Block, clear.To), Is.EqualTo(("minecraft:air", new[] { 12, 6, 22 })));
        var copy = s.PlanCopy("wall")!;
        Assert.That((copy.Name, copy.From, copy.To), Is.EqualTo(("wall", new[] { 10, 5, 20 }, new[] { 12, 6, 22 })));
        Assert.That(s.PlanCopy(EditSession.ClipboardName)!.Name, Is.EqualTo("~clipboard"));
    }

    [Test]
    public void ABlueprintOnItsWayDown_FollowsTheCursor_TurnedAndMirroredOnRequest()
    {
        var s = new EditSession();
        s.BeginPaste("house", 7, 5, 4);
        Assert.That(s.PasteSize, Is.EqualTo((7, 5, 4)));
        Assert.That(s.PasteAnchor(), Is.Null, "nothing pointed at");
        s.Target = Target(100, 10, 200, 0, 1, 0);
        // centred on the block that would be placed (100, 11, 200), standing on it
        Assert.That(s.PasteAnchor(), Is.EqualTo((97, 11, 198)));
        var plan = s.PlanPaste()!;
        Assert.That((plan.Name, plan.At, plan.Turns, plan.MirrorX), Is.EqualTo(("house", new[] { 97, 11, 198 }, 0, false)));

        s.RotatePaste();
        Assert.That(s.PasteSize, Is.EqualTo((4, 5, 7)), "a quarter turn swaps width and depth");
        Assert.That(s.PasteAnchor(), Is.EqualTo((98, 11, 197)));
        s.RotatePaste(); s.RotatePaste(); s.RotatePaste();
        Assert.That(s.PasteTurns, Is.Zero);
        s.MirrorPaste();
        Assert.That(s.PlanPaste()!.MirrorX, Is.True);
        s.CancelPaste();
        Assert.That(s.Paste, Is.Null);
        Assert.That(s.PlanPaste(), Is.Null);
    }

    [Test]
    public void TheNewMessagesAreWrittenAndReadByHand()
    {
        var copy = EditJson.Write(new BlueprintRequest { Name = "a \"b\"", From = [1, 2, 3], To = [4, 5, 6], Trim = false });
        var back = System.Text.Json.JsonSerializer.Deserialize<BlueprintRequest>(copy, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.That((back.Name, back.From, back.To, back.Trim), Is.EqualTo(("a \"b\"", new[] { 1, 2, 3 }, new[] { 4, 5, 6 }, false)));
        var paste = EditJson.Write(new PasteRequest { Name = "x", At = [7, 8, 9], Turns = 3, MirrorX = true, WithAir = true });
        var pasteBack = System.Text.Json.JsonSerializer.Deserialize<PasteRequest>(paste, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.That((pasteBack.Name, pasteBack.At, pasteBack.Turns, pasteBack.MirrorX, pasteBack.MirrorZ, pasteBack.WithAir), Is.EqualTo(("x", new[] { 7, 8, 9 }, 3, true, false, true)));

        var one = EditJson.ReadBlueprint("""{"name":"Wall","width":3,"height":2,"depth":1,"blocks":4,"author":"alice"}""");
        Assert.That((one.Name, one.Width, one.Height, one.Depth, one.Blocks, one.Author), Is.EqualTo(("Wall", 3, 2, 1, 4, "alice")));
        var list = EditJson.ReadBlueprints("""[{"name":"A","width":1,"height":1,"depth":1,"blocks":1,"author":"x"},{"name":"B","width":2,"height":2,"depth":2,"blocks":8,"author":"y"}]""");
        Assert.That(list.Select(b => b.Name), Is.EqualTo(new[] { "A", "B" }));
        Assert.That(EditJson.ReadBlueprints("""{"not":"a list"}"""), Is.Empty);
    }

    [Test]
    public void ABeamRunsFromItsOriginToItsEnd_InAnyDirection()
    {
        foreach (var dir in new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0), Vector3.Normalize(new Vector3(1, 2, -3)), Vector3.Normalize(new Vector3(-0.3f, -0.1f, 0.9f)) })
        {
            var parts = new List<PartDraw>();
            var origin = new Vector3(10, 5, 3);
            SelectionBoxes.Beam(origin, dir, 4f, 0.01f, Vector3.One, parts);
            var bar = parts.Single();
            // The bar starts on the origin and its far end is 4 blocks along the direction (its middle line)
            var start = Vector3.Transform(new Vector3(0, 0, bar.Origin.Z), bar.Model);
            var end = Vector3.Transform(new Vector3(0, 0, bar.Origin.Z + bar.Size.Z), bar.Model);
            Assert.That((start - origin).Length(), Is.LessThan(1e-3f), dir.ToString());
            Assert.That((end - (origin + dir * 4f)).Length(), Is.LessThan(1e-3f), dir.ToString());
            Assert.That(bar.Size.X, Is.EqualTo(0.01f * 16f).Within(1e-4));
        }
        var none = new List<PartDraw>();
        SelectionBoxes.Beam(Vector3.Zero, Vector3.UnitZ, 0f, 0.01f, Vector3.One, none);
        SelectionBoxes.Beam(Vector3.Zero, Vector3.Zero, 2f, 0.01f, Vector3.One, none);
        Assert.That(none, Is.Empty, "no length or no direction: nothing to draw");
    }

    [Test]
    public void AHandIsACubeAroundItsCentre()
    {
        var parts = new List<PartDraw>();
        SelectionBoxes.Cube(new Vector3(1, 2, 3), 0.1f, Vector3.One, parts);
        var cube = parts.Single();
        var lo = Vector3.Transform(cube.Origin, cube.Model);
        var hi = Vector3.Transform(cube.Origin + cube.Size, cube.Model);
        Assert.That(((lo + hi) / 2 - new Vector3(1, 2, 3)).Length(), Is.LessThan(1e-4f));
        Assert.That((hi - lo).X, Is.EqualTo(0.1f).Within(1e-4));
    }
}