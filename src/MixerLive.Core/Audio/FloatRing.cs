namespace MixerLive.Core.Audio;

/// <summary>
/// Single-producer / single-consumer ring buffer of interleaved float frames.
/// Positions are monotonic frame counters; capacity is a power of two.
/// The producer never blocks: if the consumer falls behind, old data is overwritten and the
/// consumer detects the overrun from the position gap.
///
/// The first <see cref="GuardFrames"/> frames are mirrored after the end of the buffer, so a
/// window of up to that many frames starting anywhere can be read as one contiguous span
/// (needed by the sinc resampler's SIMD loop).
/// </summary>
public sealed class FloatRing
{
    public const int GuardFrames = 4096;

    private readonly float[] _buf;
    private readonly int _mask;
    private long _write;
    private long _read;

    public int Channels { get; }
    public int CapacityFrames { get; }

    public FloatRing(int channels, int minCapacityFrames)
    {
        int cap = 1024;
        while (cap < minCapacityFrames) cap <<= 1;
        Channels = channels;
        CapacityFrames = cap;
        _mask = cap - 1;
        _buf = new float[(cap + GuardFrames) * channels];
    }

    public long WritePos => Volatile.Read(ref _write);
    public long ReadPos => Volatile.Read(ref _read);

    /// <summary>Frames available to the consumer (clamped to capacity).</summary>
    public int Available
    {
        get
        {
            long d = WritePos - ReadPos;
            return d <= 0 ? 0 : (int)Math.Min(d, CapacityFrames);
        }
    }

    /// <summary>Producer: appends <paramref name="frames"/> interleaved frames.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int frames)
    {
        int ch = Channels;
        if (frames > CapacityFrames)
        {
            // Keep only the newest frames that fit.
            interleaved = interleaved.Slice((frames - CapacityFrames) * ch);
            frames = CapacityFrames;
        }
        long w = _write;
        int start = (int)(w & _mask);
        int first = Math.Min(frames, CapacityFrames - start);
        interleaved.Slice(0, first * ch).CopyTo(_buf.AsSpan(start * ch));
        Mirror(start, first);
        if (frames > first)
        {
            interleaved.Slice(first * ch, (frames - first) * ch).CopyTo(_buf.AsSpan(0));
            Mirror(0, frames - first);
        }
        Volatile.Write(ref _write, w + frames);
    }

    /// <summary>Producer: appends silence.</summary>
    public void WriteSilence(int frames)
    {
        int ch = Channels;
        frames = Math.Min(frames, CapacityFrames);
        long w = _write;
        int start = (int)(w & _mask);
        int first = Math.Min(frames, CapacityFrames - start);
        _buf.AsSpan(start * ch, first * ch).Clear();
        Mirror(start, first);
        if (frames > first)
        {
            _buf.AsSpan(0, (frames - first) * ch).Clear();
            Mirror(0, frames - first);
        }
        Volatile.Write(ref _write, w + frames);
    }

    /// <summary>Copies the part of [from, from+count) that lies inside the guard region to the mirrored tail.</summary>
    private void Mirror(int fromFrame, int count)
    {
        int end = Math.Min(fromFrame + count, GuardFrames);
        if (fromFrame >= end) return;
        int ch = Channels;
        _buf.AsSpan(fromFrame * ch, (end - fromFrame) * ch).CopyTo(_buf.AsSpan((CapacityFrames + fromFrame) * ch));
    }

    /// <summary>Consumer: raw sample access by absolute frame position.</summary>
    public float Sample(long frame, int channel) => _buf[((int)(frame & _mask)) * Channels + channel];

    /// <summary>
    /// Consumer: a contiguous view of <paramref name="frames"/> interleaved frames starting at an
    /// absolute position. <paramref name="frames"/> must not exceed <see cref="GuardFrames"/>.
    /// </summary>
    public ReadOnlySpan<float> Window(long startFrame, int frames)
    {
        int s = (int)(startFrame & _mask);
        return _buf.AsSpan(s * Channels, frames * Channels);
    }

    /// <summary>Consumer: publishes how far it has read (only informational for the producer).</summary>
    public void SetReadPos(long pos) => Volatile.Write(ref _read, pos);
}
