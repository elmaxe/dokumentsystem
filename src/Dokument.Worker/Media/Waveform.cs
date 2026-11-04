using System.Buffers.Binary;

namespace Dokument.Worker.Media;

/// <summary>
/// An audio file's loudness over time, for the player's waveform (docs/design.md §14). Samples are added as ffmpeg
/// decodes them (mono, <see cref="SampleRate"/>) and folded into slices: each slice keeps its lowest and highest sample
/// and its RMS. Slices start at 20 ms; whenever there are more than <see cref="MaxSlices"/>, neighbours are merged in
/// pairs, so a long recording ends up with between half and all of that many, without knowing its length up front.
/// </summary>
/// <remarks>
/// The stored format, little-endian, is a 16-byte header (<c>DKWF</c>, version 1, three zero bytes, the seconds per
/// slice as a float32, the slice count as a uint32) and then three bytes per slice: the lowest sample's size, the
/// highest sample, and the RMS. All three are scaled so the loudest sample in the file is 255, which keeps the shape of
/// a quiet recording; nobody reads absolute levels off a timeline.
/// </remarks>
public sealed class Waveform
{
    public const int SampleRate = 8000;
    public const int MaxSlices = 32768;
    public const int HeaderBytes = 16;
    private const int FirstSliceSamples = SampleRate / 50;

    private readonly List<Slice> slices = [];
    private int samplesPerSlice = FirstSliceSamples;
    private Slice current;
    private int inCurrent;

    private struct Slice
    {
        public float Low;
        public float High;
        public double SumOfSquares;
        public int Samples;
    }

    public int Count => slices.Count + (inCurrent > 0 ? 1 : 0);

    public double SecondsPerSlice => (double)samplesPerSlice / SampleRate;

    /// <summary>Adds signed 16-bit samples, as <c>-f s16le</c> writes them.</summary>
    public void Add(ReadOnlySpan<short> samples)
    {
        foreach (var raw in samples)
        {
            var sample = raw / 32768f;
            if (inCurrent == 0)
            {
                current = new Slice { Low = sample, High = sample };
            }
            else
            {
                current.Low = Math.Min(current.Low, sample);
                current.High = Math.Max(current.High, sample);
            }

            current.SumOfSquares += (double)sample * sample;
            current.Samples++;
            if (++inCurrent == samplesPerSlice)
            {
                slices.Add(current);
                inCurrent = 0;
                if (slices.Count > MaxSlices)
                {
                    Halve();
                }
            }
        }
    }

    /// <summary>Merges neighbouring slices, doubling how long each one is.</summary>
    private void Halve()
    {
        var merged = new List<Slice>((slices.Count + 1) / 2);
        for (var i = 0; i < slices.Count; i += 2)
        {
            if (i + 1 == slices.Count)
            {
                // An odd one out becomes the start of the next, longer slice.
                current = Merge(slices[i], current, inCurrent > 0);
                inCurrent += slices[i].Samples;
                break;
            }

            merged.Add(Merge(slices[i], slices[i + 1], true));
        }

        slices.Clear();
        slices.AddRange(merged);
        samplesPerSlice *= 2;
    }

    private static Slice Merge(Slice a, Slice b, bool hasB) =>
        hasB
            ? new Slice
            {
                Low = Math.Min(a.Low, b.Low),
                High = Math.Max(a.High, b.High),
                SumOfSquares = a.SumOfSquares + b.SumOfSquares,
                Samples = a.Samples + b.Samples,
            }
            : a;

    /// <summary>The stored format (see the remarks). An empty recording has no slices.</summary>
    public byte[] Encode()
    {
        var all = new List<Slice>(slices);
        if (inCurrent > 0)
        {
            all.Add(current);
        }

        var loudest = 0f;
        foreach (var s in all)
        {
            loudest = Math.Max(loudest, Math.Max(-s.Low, s.High));
        }

        var bytes = new byte[HeaderBytes + all.Count * 3];
        "DKWF"u8.CopyTo(bytes);
        bytes[4] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), (float)SecondsPerSlice);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)all.Count);
        var scale = loudest > 0 ? 255 / loudest : 0;
        for (var i = 0; i < all.Count; i++)
        {
            var s = all[i];
            var rms = Math.Sqrt(s.SumOfSquares / s.Samples);
            var at = HeaderBytes + i * 3;
            bytes[at] = ToByte(Math.Max(0, -s.Low) * scale);
            bytes[at + 1] = ToByte(Math.Max(0, s.High) * scale);
            bytes[at + 2] = ToByte(rms * scale);
        }

        return bytes;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>The slices of an encoded waveform as (low, high, rms), for drawing tiles and for tests.</summary>
    public static (byte Low, byte High, byte Rms)[] Decode(
        ReadOnlySpan<byte> encoded,
        out double secondsPerSlice
    )
    {
        if (
            encoded.Length < HeaderBytes
            || !encoded[..4].SequenceEqual("DKWF"u8)
            || encoded[4] != 1
        )
        {
            throw new FormatException("Not a waveform.");
        }

        secondsPerSlice = BinaryPrimitives.ReadSingleLittleEndian(encoded[8..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded[12..]);
        if (encoded.Length != HeaderBytes + count * 3)
        {
            throw new FormatException("The waveform is cut short.");
        }

        var result = new (byte, byte, byte)[count];
        for (var i = 0; i < count; i++)
        {
            var at = HeaderBytes + i * 3;
            result[i] = (encoded[at], encoded[at + 1], encoded[at + 2]);
        }

        return result;
    }
}
