using System.Text;
using Dokument.Domain.Files;
using Dokument.Server.Files;

namespace Dokument.UnitTests;

public class NodeNameTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("tab\there")]
    public void Rejects_unusable_names(string value) =>
        Assert.False(NodeName.TryCreate(value, out _, out _));

    [Fact]
    public void Rejects_names_over_255_bytes()
    {
        Assert.True(NodeName.TryCreate(new string('a', 255), out _, out _));
        Assert.False(NodeName.TryCreate(new string('å', 128), out _, out var error));
        Assert.Contains("255", error);
    }

    [Fact]
    public void Normalizes_to_NFC_and_compares_without_case()
    {
        var decomposed = NodeName.From("Märta.PDF");
        var composed = NodeName.From(" Märta.pdf ");

        Assert.Equal("Märta.PDF".Normalize(NormalizationForm.FormC), decomposed.Value);
        Assert.Equal(decomposed.Key, composed.Key);
        Assert.NotEqual(decomposed, composed);
    }

    [Theory]
    [InlineData("report.pdf", "report (1).pdf")]
    [InlineData("archive.tar.gz", "archive.tar (1).gz")]
    [InlineData(".bashrc", ".bashrc (1)")]
    [InlineData("Kvitton", "Kvitton (1)")]
    public void Copy_numbers_go_before_the_extension(string name, string expected) =>
        Assert.Equal(expected, NodeName.From(name).WithCopyNumber(1).Value);

    [Fact]
    public void Copy_numbers_still_fit_in_255_bytes()
    {
        var copy = NodeName.From(new string('a', 251) + ".pdf").WithCopyNumber(12);
        Assert.Equal(255, Encoding.UTF8.GetByteCount(copy.Value));
        Assert.EndsWith(" (12).pdf", copy.Value);
    }
}

public class ContentHashTests
{
    private const string Hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Parses_hex_in_either_case()
    {
        Assert.True(ContentHash.TryParse(Hex.ToUpperInvariant(), out var hash));
        Assert.Equal(Hex, hash.Hex);
        Assert.True(ContentHash.TryParse(Hex, out var same));
        Assert.Equal(hash, same);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz" + "b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void Rejects_anything_else(string? value) =>
        Assert.False(ContentHash.TryParse(value, out _));

    [Fact]
    public async Task Computes_SHA256()
    {
        var hash = await ContentHash.ComputeAsync(
            new MemoryStream(),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(Hex, hash.Hex);
        var owner = Guid.Parse("0b9f6a2e-3c1d-4e5f-8a7b-9c0d1e2f3a4b");
        Assert.Equal($"blobs/{owner}/e3/b0/{Hex}", StorageKey.For(owner, hash).Value);
    }
}

public class VersionRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static FileVersion Version(int number, DateTimeOffset at, bool keep = false) =>
        new()
        {
            NodeId = Guid.Empty,
            Number = number,
            BlobHash = ContentHash.FromBytes(new byte[32]),
            Size = 1,
            MimeType = "text/plain",
            OriginalName = NodeName.From("a.txt"),
            UploadedBy = Guid.Empty,
            UploadedAt = at,
            KeepForever = keep,
        };

    [Fact]
    public void Old_versions_go_30_days_after_they_were_replaced()
    {
        var v1 = Version(1, Now.AddDays(-50));
        var v2 = Version(2, Now.AddDays(-10), keep: true);
        var v3 = Version(3, Now.AddDays(-2));

        var dates = VersionRetention.Default.PurgeDates([v1, v2, v3], v3.Id, Now);

        Assert.Equal(Now.AddDays(-10).AddDays(30), dates[1]);
        Assert.Null(dates[2]);
        Assert.Null(dates[3]);
    }

    [Fact]
    public void Versions_beyond_the_limit_go_now()
    {
        var versions = Enumerable.Range(1, 4).Select(n => Version(n, Now.AddHours(n - 5))).ToList();

        var dates = new VersionRetention(TimeSpan.FromDays(30), MaxVersions: 3).PurgeDates(
            versions,
            versions[^1].Id,
            Now
        );

        Assert.Equal(Now, dates[1]);
        Assert.True(dates[2] > Now);
    }
}

public class TusMetadataTests
{
    [Fact]
    public void Reads_the_upload_request_from_metadata()
    {
        var folder = Guid.NewGuid();
        var header = string.Join(
            ',',
            $"filename {B64("Kvitto åäö.pdf")}",
            $"folderId {B64(folder.ToString())}",
            $"modifiedAt {B64("1761134400000")}",
            $"onConflict {B64("keepBoth")}",
            "flag"
        );

        Assert.True(TusEndpoints.TryParseMetadata(header, out var metadata, out _));
        Assert.Equal("", metadata["flag"]);
        Assert.True(TusEndpoints.TryReadRequest(metadata, 42, out var request, out _));
        Assert.Equal("Kvitto åäö.pdf", request.Name);
        Assert.Equal(folder, request.FolderId);
        Assert.Equal(NameConflictPolicy.KeepBoth, request.OnConflict);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1761134400000),
            request.ClientModifiedAt
        );
    }

    [Fact]
    public void Rejects_bad_values()
    {
        Assert.False(TusEndpoints.TryParseMetadata("filename !!!", out _, out _));
        Assert.False(TusEndpoints.TryReadRequest(new() { ["sha256"] = "nope" }, 1, out _, out _));
        Assert.False(
            TusEndpoints.TryReadRequest(new() { ["onConflict"] = "overwrite" }, 1, out _, out _)
        );
    }

    private static string B64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
