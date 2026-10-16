namespace AubsCraft.Admin.Editing;

/// <summary>
/// How a block is turned when it is placed, as the game does it for a player: a stair faces the player and sits on the top half when the upper half of a wall or the underside of a block was clicked, a log
/// lies along the face it was put on, a torch put on a wall is a wall torch. Only a block given with no properties of its own is turned; one that names its properties is placed as written.
/// </summary>
public static class PlacementRules
{
    /// <summary>The compass name of a horizontal direction.</summary>
    public static string Compass(int dx, int dz) => Math.Abs(dx) >= Math.Abs(dz) ? (dx > 0 ? "east" : "west") : (dz > 0 ? "south" : "north");

    /// <summary>The compass direction a camera looks along (yaw 0 is +X, 90 is +Z: the viewer's convention).</summary>
    public static string LookDirection(double yawDegrees)
    {
        double r = yawDegrees * Math.PI / 180.0;
        return Compass((int)Math.Round(Math.Cos(r) * 100), (int)Math.Round(Math.Sin(r) * 100));
    }

    public static string Opposite(string compass) => compass switch { "north" => "south", "south" => "north", "east" => "west", _ => "east" };

    /// <summary>
    /// The block data to place. <paramref name="normalX"/>, <paramref name="normalY"/>, <paramref name="normalZ"/> is the face that was clicked (pointing out of the block that was hit);
    /// <paramref name="hitFractionY"/> is where on that block's height the ray struck it (0 to 1); <paramref name="yawDegrees"/> is where the camera looks.
    /// </summary>
    public static string Orient(string block, int normalX, int normalY, int normalZ, double hitFractionY, double yawDegrees)
    {
        if (block.Contains('[')) return block;
        int colon = block.IndexOf(':');
        string ns = colon < 0 ? "minecraft" : block[..colon];
        string name = colon < 0 ? block : block[(colon + 1)..];
        string look = LookDirection(yawDegrees);
        string towardPlayer = Opposite(look);
        bool upperHalf = normalY < 0 || (normalY == 0 && hitFractionY > 0.5);

        // Stairs and slabs: the half the click was in
        if (name.EndsWith("_stairs", StringComparison.Ordinal))
            return $"{block}[facing={towardPlayer},half={(upperHalf ? "top" : "bottom")}]";
        if (name.EndsWith("_slab", StringComparison.Ordinal))
            return $"{block}[type={(upperHalf ? "top" : "bottom")}]";

        // Things that lie along an axis: along the face they were put on
        if (IsAxisBlock(name))
            return $"{block}[axis={(normalX != 0 ? "x" : normalZ != 0 ? "z" : "y")}]";

        // Torches and ladders on a wall face away from it
        if (name is "torch" or "soul_torch" or "redstone_torch")
        {
            if (normalX == 0 && normalZ == 0) return block;
            string wall = name == "redstone_torch" ? "redstone_wall_torch" : name.Replace("torch", "wall_torch");
            return $"{ns}:{wall}[facing={Compass(normalX, normalZ)}]";
        }
        if (name == "ladder")
            return normalX == 0 && normalZ == 0 ? block : $"{block}[facing={Compass(normalX, normalZ)}]";

        // Blocks with a front: it faces the player
        if (FacesPlayer(name)) return $"{block}[facing={towardPlayer}]";
        return block;
    }

    private static bool IsAxisBlock(string name) =>
        name.EndsWith("_log", StringComparison.Ordinal) || name.EndsWith("_wood", StringComparison.Ordinal) || name.EndsWith("_stem", StringComparison.Ordinal) || name.EndsWith("_hyphae", StringComparison.Ordinal)
        || name is "hay_block" or "bone_block" or "basalt" or "polished_basalt" or "smooth_basalt" or "purpur_pillar" or "quartz_pillar" or "deepslate" or "muddy_mangrove_roots" or "ochre_froglight" or "verdant_froglight" or "pearlescent_froglight";

    private static bool FacesPlayer(string name) =>
        name is "furnace" or "blast_furnace" or "smoker" or "chest" or "trapped_chest" or "ender_chest" or "dispenser" or "dropper" or "loom" or "stonecutter" or "lectern" or "carved_pumpkin" or "jack_o_lantern" or "bee_nest" or "beehive"
        || name.EndsWith("_glazed_terracotta", StringComparison.Ordinal);
}
