using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Dokument.Domain.Search;

/// <summary>How much of a <see cref="PartialDate"/> is known.</summary>
public enum DatePrecision
{
    Year,
    Month,
    Day,

    /// <summary>A date with a time of day, to the minute or second.</summary>
    Time,
}

/// <summary>
/// A date that may be known only to the year or month, such as when a scanned old photo was taken ("1974", "1974-07").
/// <see cref="Start"/> is the first moment it could be, as a wall-clock time stored as if it were UTC, the same
/// convention as a camera's clock without an offset (docs/design.md §10.3).
/// </summary>
public sealed record PartialDate
{
    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
    ];

    private PartialDate(DateTimeOffset start, DatePrecision precision)
    {
        Start = start;
        Precision = precision;
    }

    public DateTimeOffset Start { get; }
    public DatePrecision Precision { get; }

    /// <summary>A date from its first moment; a time of day is kept to the minute.</summary>
    public static PartialDate From(DateTimeOffset start, DatePrecision precision)
    {
        var at = start.UtcDateTime;
        return new(
            precision switch
            {
                DatePrecision.Year => new DateTimeOffset(at.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DatePrecision.Month => new DateTimeOffset(
                    at.Year,
                    at.Month,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero
                ),
                DatePrecision.Day => new DateTimeOffset(at.Date, TimeSpan.Zero),
                _ => new DateTimeOffset(
                    at.Date.AddHours(at.Hour).AddMinutes(at.Minute),
                    TimeSpan.Zero
                ),
            },
            precision
        );
    }

    /// <summary>Reads "1974", "1974-07", "1974-07-14" or "1974-07-14 15:30" (or with a T, and seconds).</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PartialDate? date)
    {
        date = null;
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var culture = CultureInfo.InvariantCulture;
        if (
            value.Length == 4
            && int.TryParse(value, NumberStyles.None, culture, out var year)
            && year >= 1
        )
        {
            date = new(new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), DatePrecision.Year);
        }
        else if (
            DateTime.TryParseExact(value, "yyyy-MM", culture, DateTimeStyles.None, out var month)
        )
        {
            date = new(new DateTimeOffset(month, TimeSpan.Zero), DatePrecision.Month);
        }
        else if (
            DateTime.TryParseExact(value, "yyyy-MM-dd", culture, DateTimeStyles.None, out var day)
        )
        {
            date = new(new DateTimeOffset(day, TimeSpan.Zero), DatePrecision.Day);
        }
        else if (
            DateTime.TryParseExact(value, TimeFormats, culture, DateTimeStyles.None, out var time)
        )
        {
            date = From(new DateTimeOffset(time, TimeSpan.Zero), DatePrecision.Time);
        }

        return date is not null;
    }

    /// <summary>The same forms <see cref="TryParse"/> reads, e.g. "1974-07".</summary>
    public override string ToString() =>
        Start.ToString(
            Precision switch
            {
                DatePrecision.Year => "yyyy",
                DatePrecision.Month => "yyyy-MM",
                DatePrecision.Day => "yyyy-MM-dd",
                _ => "yyyy-MM-dd HH:mm",
            },
            CultureInfo.InvariantCulture
        );
}
