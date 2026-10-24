using System.Globalization;
using System.Text.RegularExpressions;

namespace Dokument.Domain.Identity;

/// <summary>
/// Reads the value of the IdP's quota claim (docs/design.md §17.1): a byte count (<c>53687091200</c>), a size
/// (<c>50GB</c>, <c>1.5 TiB</c>, <c>500m</c>), or <c>unlimited</c>. Sizes are binary like everywhere in the UI, so
/// <c>GB</c> and <c>GiB</c> both mean 1024³ bytes and the People page shows <c>50GB</c> as exactly 50 GiB.
/// </summary>
public static partial class QuotaClaim
{
    /// <summary>The quota the claim asks for, or null when the value can't be read or isn't above 0.</summary>
    public static UserQuota? TryParse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
        {
            return UserQuota.Unlimited;
        }

        var match = SizePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        var number = match.Groups["number"].Value;

        // A plain number counts bytes, so it has to be a whole one.
        if (unit.Length == 0 && number.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        if (
            !decimal.TryParse(
                number,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount
            )
        )
        {
            return null;
        }

        var power = unit.Length == 0 ? 0 : "BKMGT".IndexOf(char.ToUpperInvariant(unit[0]));
        decimal bytes;
        try
        {
            bytes = decimal.Floor(amount * (decimal)Math.Pow(1024, power));
        }
        catch (OverflowException)
        {
            return null;
        }

        return bytes is > 0 and <= long.MaxValue ? UserQuota.Limit((long)bytes) : null;
    }

    // B, or K/M/G/T with an optional B or iB: 50GB, 50 GiB, 50g, 1.5TB, 512 mb.
    [GeneratedRegex(
        @"^(?<number>[0-9]+(\.[0-9]+)?)\s*(?<unit>B|[KMGT](i?B)?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SizePattern();
}
