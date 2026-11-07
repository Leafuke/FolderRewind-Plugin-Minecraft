using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MineRewind.Tests")]
namespace MineRewind.Pack;

internal static class ProductIdentity
{
    internal static Version NumericVersion(string product)
    {
        if (!Regex.IsMatch(product, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"))
            throw new InvalidDataException("Invalid product SemVer.");
        var core = product.Split('-', '+')[0].Split('.').Select(int.Parse).ToArray();
        return new(core[0], core[1], core[2], 0);
    }

    internal static bool ProductMatches(string? actual, string expected)
        => actual == expected || actual is not null && Regex.IsMatch(actual, "^" + Regex.Escape(expected)
            + (expected.Contains('+') ? @"\.[a-fA-F0-9]{7,64}$" : @"\+[a-fA-F0-9]{7,64}$"));
}
