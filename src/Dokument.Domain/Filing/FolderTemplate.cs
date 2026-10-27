using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dokument.Domain.Files;

namespace Dokument.Domain.Filing;

/// <summary>
/// Where a filing rule moves a file (docs/design.md §13.6): folders from the top of My files, separated by "/", which
/// may hold <c>{year}</c> and <c>{month}</c>, such as <c>Kvitton/{year}</c>. The month is two digits, so the folders
/// sort in order. Stored as written, with the folders trimmed and empty ones dropped.
/// </summary>
public sealed partial record FolderTemplate
{
    public const int MaxLength = 500;
    public const int MaxDepth = 10;

    private FolderTemplate(string value, IReadOnlyList<string> folders)
    {
        Value = value;
        Folders = folders;
    }

    public string Value { get; }

    /// <summary>The folders, top first, with their placeholders.</summary>
    public IReadOnlyList<string> Folders { get; }

    public bool HasDate => Value.Contains('{');

    public static bool TryCreate(
        string? value,
        [NotNullWhen(true)] out FolderTemplate? template,
        [NotNullWhen(false)] out string? error
    )
    {
        template = null;
        var folders = (value ?? "")
            .Normalize(NormalizationForm.FormC)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalized = string.Join('/', folders);
        error = null;
        if (folders.Length == 0)
        {
            error = "Name a folder to move files to.";
        }
        else if (normalized.Length > MaxLength)
        {
            error = $"A folder path can be at most {MaxLength} characters long.";
        }
        else if (folders.Length > MaxDepth)
        {
            error = $"A folder path can have at most {MaxDepth} folders.";
        }
        else if (
            folders
                .SelectMany(f => Placeholder().Matches(f))
                .FirstOrDefault(m => !IsKnown(m.Value)) is
            { } unknown
        )
        {
            error =
                $"\"{unknown.Value}\" isn't something a folder name can hold. Use {{year}} or {{month}}.";
        }
        else if (folders.Any(f => Placeholder().Replace(f, "").IndexOfAny(['{', '}']) >= 0))
        {
            error = "A brace only goes around {year} or {month}.";
        }
        else
        {
            // Every folder must make a valid name, whatever the date.
            foreach (var folder in folders)
            {
                if (!NodeName.TryCreate(Fill(folder, 2026, 12), out _, out var nameError))
                {
                    error = nameError;
                    break;
                }
            }
        }

        if (error is not null)
        {
            return false;
        }

        template = new FolderTemplate(normalized, folders);
        return true;
    }

    /// <summary>For trusted values, such as templates read back from the database.</summary>
    public static FolderTemplate From(string value) =>
        TryCreate(value, out var template, out var error)
            ? template
            : throw new ArgumentException(error, nameof(value));

    /// <summary>The folders for a file dated <paramref name="date"/>, in the date's own clock.</summary>
    public IReadOnlyList<NodeName> Resolve(DateTimeOffset date) =>
        Folders.Select(f => NodeName.From(Fill(f, date.Year, date.Month))).ToList();

    private static bool IsKnown(string placeholder) =>
        placeholder.Equals("{year}", StringComparison.OrdinalIgnoreCase)
        || placeholder.Equals("{month}", StringComparison.OrdinalIgnoreCase);

    private static string Fill(string folder, int year, int month) =>
        Placeholder()
            .Replace(
                folder,
                m =>
                    m.Value.Equals("{year}", StringComparison.OrdinalIgnoreCase)
                        ? year.ToString(CultureInfo.InvariantCulture)
                        : month.ToString("00", CultureInfo.InvariantCulture)
            );

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();

    public bool Equals(FolderTemplate? other) => other is not null && Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;
}
