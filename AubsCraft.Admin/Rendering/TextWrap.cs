namespace AubsCraft.Admin.Rendering;

/// <summary>Breaks a message into lines for a UI that draws each line on its own row (GameUI's chat box does).</summary>
public static class TextWrap
{
    /// <summary>
    /// Lines no wider than the budgets (the first line gets <paramref name="firstWidth"/>, the rest <paramref name="width"/>, measured with
    /// <paramref name="measure"/>), broken at spaces where possible and mid-word where one word is wider than a whole line.
    /// Never returns zero lines.
    /// </summary>
    public static List<string> Wrap(string text, float firstWidth, float width, Func<string, float> measure)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        float limit = Math.Max(firstWidth, 1f);
        foreach (var word in text.Split(' '))
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (measure(candidate) <= limit) { line.Clear(); line.Append(candidate); continue; }
            if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); limit = Math.Max(width, 1f); }
            // A word wider than a whole line: cut it
            string rest = word;
            while (measure(rest) > limit && rest.Length > 1)
            {
                int cut = rest.Length - 1;
                while (cut > 1 && measure(rest[..cut]) > limit) cut--;
                lines.Add(rest[..cut]);
                rest = rest[cut..];
                limit = Math.Max(width, 1f);
            }
            line.Append(rest);
        }
        lines.Add(line.ToString());
        return lines;
    }
}
