namespace AubsCraft.Live.Model.Editing;

/// <summary>What an edit did, as the plugin reports it (the panel passes it on to the viewer).</summary>
public sealed record EditResult(bool Ok, string? Edit, long Placed, long Skipped, bool Applied, string? Error);

/// <summary>An edit an admin asks for, as the viewer sends it to the panel (JSON).</summary>
public sealed class EditRequest
{
    public string? Label { get; set; }
    public List<EditOpDto> Ops { get; set; } = new();
}

/// <summary>
/// One step of an edit: <c>set</c> (<see cref="At"/>, <see cref="Block"/>), <c>fill</c> (<see cref="From"/> and <see cref="To"/> corners, <see cref="Block"/>, optionally only where <see cref="Only"/> is now, optionally
/// <see cref="Hollow"/>), or <c>blocks</c> (a sparse list: <see cref="Palette"/> and <see cref="Data"/>, base64 of (i16 dx, i16 dy, i16 dz, u16 palette index) records from the anchor <see cref="At"/>).
/// </summary>
public sealed class EditOpDto
{
    public string Op { get; set; } = "";
    public int[]? At { get; set; }
    public int[]? From { get; set; }
    public int[]? To { get; set; }
    public string? Block { get; set; }
    public string? Only { get; set; }
    public bool Hollow { get; set; }
    public string[]? Palette { get; set; }
    public string? Data { get; set; }
}

/// <summary>The block a ray hit, as the panel's pick answers (<c>/api/world/edit/pick</c>).</summary>
public sealed class PickReply
{
    public bool Hit { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public int Nx { get; set; }
    public int Ny { get; set; }
    public int Nz { get; set; }
    public string Block { get; set; } = "";
    public double Distance { get; set; }
    public double Hx { get; set; }
    public double Hy { get; set; }
    public double Hz { get; set; }
}
