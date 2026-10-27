using Dokument.Infrastructure.Files;

namespace Dokument.UnitTests;

public sealed class MimeTypeTests
{
    /// <summary>
    /// Recordings the worker plays and transcribes have to be typed as audio or video, or the viewer and the Type filter
    /// treat them as unknown files (§11.1, §14).
    /// </summary>
    [Theory]
    [InlineData("memo.amr", "audio/")]
    [InlineData("memo.awb", "audio/")]
    [InlineData("memo.m4a", "audio/")]
    [InlineData("memo.opus", "audio/")]
    [InlineData("memo.ogg", "audio/")]
    [InlineData("memo.oga", "audio/")]
    [InlineData("memo.flac", "audio/")]
    [InlineData("memo.wav", "audio/")]
    [InlineData("memo.aiff", "audio/")]
    [InlineData("memo.aif", "audio/")]
    [InlineData("memo.wma", "audio/")]
    [InlineData("memo.ape", "audio/")]
    [InlineData("memo.wv", "audio/")]
    [InlineData("memo.caf", "audio/")]
    [InlineData("clip.mkv", "video/")]
    [InlineData("clip.webm", "video/")]
    [InlineData("clip.flv", "video/")]
    [InlineData("clip.mts", "video/")]
    [InlineData("clip.m2ts", "video/")]
    [InlineData("clip.3gp", "video/")]
    [InlineData("clip.3g2", "video/")]
    [InlineData("clip.wmv", "video/")]
    [InlineData("clip.mpg", "video/")]
    [InlineData("clip.m4v", "video/")]
    [InlineData("clip.mov", "video/")]
    [InlineData("clip.avi", "video/")]
    public void Recordings_are_typed_as_audio_or_video(string name, string prefix) =>
        Assert.StartsWith(prefix, MimeTypes.For(name));
}
