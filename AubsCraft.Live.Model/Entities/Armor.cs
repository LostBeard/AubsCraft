using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// What a player wears, as the live feed says it in the player's variant: four pieces (helmet, chestplate, leggings, boots) joined with '|', each the armor's material (<c>diamond</c>, <c>iron</c>,
/// <c>gold</c>, <c>chainmail</c>, <c>netherite</c>, <c>turtle_scute</c>, <c>leather</c>), leather with its dye as <c>leather#A06540</c>, a piece not worn empty: <c>diamond|diamond||iron</c>.
/// </summary>
public readonly record struct WornArmor(string? Helmet, string? Chest, string? Legs, string? Boots)
{
    public bool Any => Helmet != null || Chest != null || Legs != null || Boots != null;

    /// <summary>The armor a variant says (none when it says nothing that is armor).</summary>
    public static WornArmor Parse(string? variant)
    {
        if (string.IsNullOrEmpty(variant)) return default;
        var parts = variant.Split('|');
        if (parts.Length != 4) return default;
        string? Piece(string s) { var m = Material(s); return ArmorLook.Materials.Contains(m) ? s : null; }
        return new WornArmor(Piece(parts[0]), Piece(parts[1]), Piece(parts[2]), Piece(parts[3]));
    }

    /// <summary>The material of a piece (<c>leather#A06540</c> is leather).</summary>
    public static string Material(string piece) { int hash = piece.IndexOf('#'); return hash < 0 ? piece : piece[..hash]; }

    /// <summary>The dye of a leather piece (the game's default brown when it has none).</summary>
    public static Vector3 LeatherColour(string piece)
    {
        int hash = piece.IndexOf('#');
        if (hash >= 0 && piece.Length - hash - 1 == 6 && int.TryParse(piece.AsSpan(hash + 1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return new Vector3(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        return new Vector3(0xA0 / 255f, 0x65 / 255f, 0x40 / 255f);
    }
}

/// <summary>The textures of armor (paths under textures/entity) and the boxes that wear them.</summary>
public static class ArmorLook
{
    public static readonly string[] Materials = ["leather", "chainmail", "iron", "gold", "diamond", "netherite", "turtle_scute"];

    /// <summary>The textures to pack: every material's layer for the helmet, chestplate and boots, and for the leggings (the turtle's shell is a helmet only), and leather's undyed overlay.</summary>
    public static IEnumerable<string> TextureNames()
    {
        foreach (var m in Materials) yield return $"equipment/humanoid/{m}";
        foreach (var m in Materials.Where(m => m != "turtle_scute")) yield return $"equipment/humanoid_leggings/{m}";
        yield return "equipment/humanoid/leather_overlay";
        yield return "equipment/humanoid_leggings/leather_overlay";
    }

    // The boxes of the armor model (the game's HumanoidModel with its 1.0 and 0.5 deformations, in the old 64 x 32 layout the armor textures keep): texture offset, corner, size, mirror
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) Head = (0, 0, -4, -8, -4, 8, 8, 8, false);
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) Body = (16, 16, -4, 0, -2, 8, 12, 4, false);
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) RightArm = (40, 16, -3, -2, -2, 4, 12, 4, false);
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) LeftArm = (40, 16, -1, -2, -2, 4, 12, 4, true);
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) RightLeg = (0, 16, -2, 0, -2, 4, 12, 4, false);
    private static readonly (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) LeftLeg = (0, 16, -2, 0, -2, 4, 12, 4, true);

    /// <summary>Append the armor boxes that go with a part of the player's body (the part's own matrix places them).</summary>
    public static void Add(List<PartDraw> output, Matrix4x4 model, PartRole role, in WornArmor worn)
    {
        switch (role)
        {
            case PartRole.Head: Piece(output, model, Head, worn.Helmet, 1.0f, leggings: false); break;
            case PartRole.Body:
                Piece(output, model, Body, worn.Chest, 1.0f, leggings: false);
                Piece(output, model, Body, worn.Legs, 0.5f, leggings: true);
                break;
            case PartRole.RightArm: Piece(output, model, RightArm, worn.Chest, 1.0f, leggings: false); break;
            case PartRole.LeftArm: Piece(output, model, LeftArm, worn.Chest, 1.0f, leggings: false); break;
            case PartRole.RightLeg:
                Piece(output, model, RightLeg, worn.Legs, 0.5f, leggings: true);
                Piece(output, model, RightLeg, worn.Boots, 1.0f, leggings: false);
                break;
            case PartRole.LeftLeg:
                Piece(output, model, LeftLeg, worn.Legs, 0.5f, leggings: true);
                Piece(output, model, LeftLeg, worn.Boots, 1.0f, leggings: false);
                break;
        }
    }

    private static void Piece(List<PartDraw> output, Matrix4x4 model, (float u, float v, float x, float y, float z, float w, float h, float d, bool mirror) box, string? piece, float inflate, bool leggings)
    {
        if (piece == null) return;
        var material = WornArmor.Material(piece);
        if (leggings && material == "turtle_scute") return;
        string folder = leggings ? "humanoid_leggings" : "humanoid";
        bool leather = material == "leather";
        output.Add(new PartDraw
        {
            Model = model, Origin = new Vector3(box.x, box.y, box.z), Inflate = inflate, Size = new Vector3(box.w, box.h, box.d), Mirror = box.mirror,
            Texture = $"equipment/{folder}/{material}", U = box.u, V = box.v, Tint = leather ? WornArmor.LeatherColour(piece) : Vector3.One,
        });
        // Leather has an overlay (the stitching, the buckles) that the dye does not touch, over the dyed layer
        if (leather)
            output.Add(new PartDraw
            {
                Model = model, Origin = new Vector3(box.x, box.y, box.z), Inflate = inflate + 0.01f, Size = new Vector3(box.w, box.h, box.d), Mirror = box.mirror,
                Texture = $"equipment/{folder}/leather_overlay", U = box.u, V = box.v, Tint = Vector3.One,
            });
    }
}
