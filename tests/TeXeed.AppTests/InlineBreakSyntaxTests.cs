using System.IO;
using System.Xml.Linq;
using TeXeed.App.Controls;

internal static partial class Program
{
    private static async Task CheckInlineBreakSyntaxAsync(bool realTex)
    {
        static void Positions(string source, params int[] expected)
        {
            ChatTexInput.Validate(source);
            if (!ChatInlineBreaks.Positions(source).SequenceEqual(expected))
                throw new Exception("Wrong inline TeX boundaries for " + source);
        }
        const string protectedSum = @"a_1+\nobreak b_2+c_3+d_4";
        Positions(protectedSum, protectedSum.IndexOf("+c", StringComparison.Ordinal) + 1, protectedSum.LastIndexOf('+') + 1);
        const string radical = @"x_1=\sqrt[3]{a+b}+d_4";
        Positions(radical, radical.IndexOf('=') + 1, radical.LastIndexOf('+') + 1);
        const string absolute = @"x_1=|a+b|+d_4";
        Positions(absolute, absolute.IndexOf('=') + 1, absolute.LastIndexOf('+') + 1);
        Positions(@"(a_1+b_2)^{m+n}");
        var glyphs = XElement.Parse("""
            <svg viewBox="0 0 12 12"><defs>
              <path id="stroke" d="M0 0h2v3h-2z"/>
              <use id="larger" href="#stroke" transform="scale(1.5)"/>
            </defs><use href="#larger" x="4" y="2"/></svg>
            """);
        var ink = ChatSvg.Read(glyphs, "scaled-glyph.svg").Geometry.Bounds;
        if (ink.X != 4 || ink.Y != 2 || ink.Width != 3 || ink.Height != 4.5)
            throw new Exception("Scaled glyph definition was misplaced.");
        foreach (var invalid in new[] { "#larger", "another.svg#stroke" })
        {
            glyphs.Element("defs")!.Element("use")!.SetAttributeValue("href", invalid);
            try { ChatSvg.Read(glyphs, "invalid-glyph.svg"); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Accepted an invalid SVG glyph reference: " + invalid);
        }
        if (!realTex) return;

        // A negative unbraced dimension is read by TeX's number scanner. A location
        // whatsit in that argument is invalid; the original notation remains legal.
        const string kern = @"a_1=b_2+3\kern-1pt+4+c_5+d_6";
        var original = await ChatTexRenderer.RenderAsync(kern, false);
        var file = XElement.Load(original.Path);
        var markInDimension = ChatInlineBreaks.Positions(kern).Contains(kern.IndexOf("-1pt", StringComparison.Ordinal) + 1);
        if (original.Bounds.Width < ChatInlineBreaks.MinimumWidthBp || markInDimension &&
            (file.Attribute("data-texeed-breaks")?.Value != "" || original.InlineSlices.Count != 0))
            throw new Exception("An unmodified, legal TeX formula was lost after the break measurement failed.");
        try { await ChatTexRenderer.RenderAsync(kern + "^2^3", false); }
        catch (ChatTexRenderer.TexSyntaxException) { return; }
        throw new Exception("The unmodified TeX fallback accepted a double superscript.");
    }
}
