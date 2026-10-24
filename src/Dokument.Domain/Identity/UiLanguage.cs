namespace Dokument.Domain.Identity;

/// <summary>A language the UI and emails are written in (docs/design.md §23).</summary>
public sealed record UiLanguage
{
    public static readonly UiLanguage Swedish = new("sv");
    public static readonly UiLanguage English = new("en");
    public static readonly IReadOnlyList<UiLanguage> All = [Swedish, English];

    private UiLanguage(string code) => Code = code;

    /// <summary>The BCP 47 tag, as the web and mobile i18n packages use it.</summary>
    public string Code { get; }

    public static UiLanguage? TryParse(string? code) =>
        All.FirstOrDefault(l =>
            string.Equals(l.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)
        );

    public static UiLanguage Parse(string code) =>
        TryParse(code) ?? throw new FormatException($"Unknown language '{code}'.");

    public override string ToString() => Code;
}
