using System.Windows;
using System.Windows.Media;

namespace TeXeed.App.Controls;

public static class EditorTypography
{
    public const string FamilyName = "Sarasa Mono SC, Cascadia Code, Consolas, Microsoft YaHei";
    public static FontFamily DefaultFamily { get; } = new(FamilyName);
    public static FontWeight DefaultWeight { get; } = FindLightWeight(Fonts.SystemFontFamilies);

    internal static FontWeight FindLightWeight(IEnumerable<FontFamily> families)
    {
        var sarasa = families.FirstOrDefault(family => string.Equals(family.Source, "Sarasa Mono SC", StringComparison.OrdinalIgnoreCase));
        return sarasa?.GetTypefaces().Any(face => face.Weight == FontWeights.Light && face.Style == FontStyles.Normal) == true
            ? FontWeights.Light : FontWeights.Normal;
    }
    internal static FontWeight WeightFor(string family) => family.StartsWith("Sarasa Mono SC", StringComparison.OrdinalIgnoreCase) ? DefaultWeight : FontWeights.Normal;
}
