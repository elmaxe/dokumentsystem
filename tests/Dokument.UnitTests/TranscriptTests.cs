using Dokument.Domain.Search;
using Dokument.Infrastructure.Ml;
using Dokument.Worker.Media;

namespace Dokument.UnitTests;

public sealed class TranscriptTests
{
    private static TranscriptLine Line(int start, int end, string text) =>
        new(start, end, text, []);

    [Fact]
    public void Pages_are_about_a_minute_and_end_at_a_pause()
    {
        var pager = new TranscriptPager();
        var pages = new List<TranscriptPage>();
        // A line every 10 s, each 9.5 s long: the gaps are too short to end a page until one is a real pause.
        for (var i = 0; i < 7; i++)
        {
            Assert.Null(pager.Add(Line(i * 10_000, i * 10_000 + 9_500, $"line {i}")));
        }

        // 70 s in, after a 2 s pause: the first page ends.
        pages.Add(pager.Add(Line(71_500, 75_000, "after the pause"))!);
        pages.Add(pager.Flush()!);

        Assert.Equal(0, pages[0].StartMs);
        Assert.Equal(69_500, pages[0].EndMs);
        Assert.Equal(7, pages[0].Lines.Count);
        Assert.StartsWith("line 0 line 1", pages[0].Text);
        Assert.Equal(71_500, pages[1].StartMs);
        Assert.Null(pager.Flush());
    }

    [Fact]
    public void A_page_never_runs_past_ninety_seconds_even_without_a_pause()
    {
        var pager = new TranscriptPager();
        TranscriptPage? page = null;
        var start = 0;
        while (page is null)
        {
            page = pager.Add(Line(start, start + 4_950, "word"));
            start += 5_000;
        }

        Assert.True(page.EndMs - page.StartMs <= 90_000);
        Assert.True(page.EndMs - page.StartMs > 80_000);
    }

    [Fact]
    public void Lines_keep_their_word_times_as_json()
    {
        var lines = new[]
        {
            new TranscriptLine(
                1_000,
                2_500,
                "Hej på dig",
                [new(1_000, 1_300, "Hej"), new(1_400, 1_600, "på")]
            ),
        };
        var json = TranscriptLine.Serialize(lines);
        Assert.Equal(
            """[{"s":1000,"e":2500,"t":"Hej på dig","w":[[1000,1300,"Hej"],[1400,1600,"på"]]}]""",
            json
        );
        Assert.Equal(lines[0].Words, TranscriptLine.Deserialize(json)[0].Words);
    }

    [Fact]
    public void Captions_are_webvtt_with_short_lines()
    {
        var vtt = WebVtt.From([
            Line(1_000, 3_000, "Hej och välkommen."),
            Line(3_600_000 + 61_500, 3_600_000 + 63_000, "Ett <b>test</b> --> slut"),
        ]);
        Assert.Equal(
            """
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            Hej och välkommen.

            01:01:01.500 --> 01:01:03.000
            Ett &lt;b>test&lt;/b> -> slut

            """.Replace("\r\n", "\n"),
            vtt
        );
    }

    [Fact]
    public void A_long_line_is_split_into_cues_at_words()
    {
        var words = Enumerable
            .Range(0, 30)
            .Select(i => new TranscriptWord(i * 500, i * 500 + 400, $"ord{i}"))
            .ToList();
        var line = new TranscriptLine(
            0,
            15_000,
            string.Join(' ', words.Select(w => w.Text)),
            words
        );

        var cues = WebVtt.Cues(line).ToList();
        Assert.True(cues.Count >= 3);
        Assert.All(cues, c => Assert.True(c.Text.Length <= WebVtt.LineLength * 2));
        Assert.All(cues, c => Assert.True(c.End - c.Start <= 7_000));
        Assert.Equal(0, cues[0].Start);
        Assert.Equal(14_900, cues[^1].End);
    }

    [Fact]
    public void Reads_the_ml_services_lines()
    {
        Assert.Equal(
            new TranscribeEvent.Detected(
                "sv",
                0.93,
                60_000,
                50_000,
                "small",
                "faster-whisper 1.2.1"
            ),
            MlClient.Parse(
                """{"type": "detected", "language": "sv", "language_probability": 0.93, "duration_ms": 60000, "speech_ms": 50000, "model": "small", "engine": "faster-whisper 1.2.1"}"""
            )
        );
        var segment = Assert.IsType<TranscribeEvent.Segment>(
            MlClient.Parse(
                """{"type": "segment", "start_ms": 0, "end_ms": 2500, "text": "Hej.", "words": [[0, 400, "Hej."]]}"""
            )
        );
        Assert.Equal(new TranscriptWord(0, 400, "Hej."), segment.Line.Words.Single());
        Assert.Equal(
            new TranscribeEvent.Done(2),
            MlClient.Parse("""{"type": "done", "segments": 2}""")
        );
        Assert.Equal(
            new TranscribeEvent.Failed("out of memory"),
            MlClient.Parse("""{"type": "error", "message": "out of memory"}""")
        );
        Assert.Throws<MlProtocolException>(() => MlClient.Parse("""{"type": "surprise"}"""));
        Assert.Throws<MlProtocolException>(() => MlClient.Parse("not json"));
    }

    [Fact]
    public void Sends_the_first_audio_stream_as_16_khz_mono_opus()
    {
        var args = TranscribeHandler.AudioArguments("/in.mov", "/out.ogg");
        Assert.Equal(
            [
                "-i",
                "/in.mov",
                "-map",
                "0:a:0",
                "-vn",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-c:a",
                "libopus",
                "-b:a",
                "24k",
                "-f",
                "ogg",
                "-y",
                "/out.ogg",
            ],
            args
        );
    }
}
