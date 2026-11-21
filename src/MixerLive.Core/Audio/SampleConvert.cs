using NAudio.Wave;

namespace MixerLive.Core.Audio;

internal enum SampleKind
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
    Unsupported
}

/// <summary>Converts between raw WASAPI buffers and interleaved stereo float.</summary>
internal static unsafe class SampleConvert
{
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    public static SampleKind Detect(WaveFormat f)
    {
        bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = f.Encoding == WaveFormatEncoding.Pcm;
        if (f is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == SubtypeFloat;
            isPcm = ext.SubFormat == SubtypePcm;
        }
        if (isFloat && f.BitsPerSample == 32) return SampleKind.Float32;
        if (isPcm)
        {
            switch (f.BitsPerSample)
            {
                case 16: return SampleKind.Pcm16;
                case 24: return SampleKind.Pcm24;
                case 32: return SampleKind.Pcm32;
            }
        }
        return SampleKind.Unsupported;
    }

    public static void ToStereoFloat(IntPtr src, SampleKind kind, int srcChannels, int frames, Span<float> dst)
    {
        int o = 0;
        switch (kind)
        {
            case SampleKind.Float32:
            {
                float* p = (float*)src;
                for (int n = 0; n < frames; n++, p += srcChannels)
                {
                    float l = p[0];
                    float r = srcChannels > 1 ? p[1] : l;
                    dst[o++] = l;
                    dst[o++] = r;
                }
                break;
            }
            case SampleKind.Pcm16:
            {
                short* p = (short*)src;
                const float k = 1f / 32768f;
                for (int n = 0; n < frames; n++, p += srcChannels)
                {
                    float l = p[0] * k;
                    float r = srcChannels > 1 ? p[1] * k : l;
                    dst[o++] = l;
                    dst[o++] = r;
                }
                break;
            }
            case SampleKind.Pcm24:
            {
                byte* p = (byte*)src;
                const float k = 1f / 8388608f;
                int stride = srcChannels * 3;
                for (int n = 0; n < frames; n++, p += stride)
                {
                    float l = Read24(p) * k;
                    float r = srcChannels > 1 ? Read24(p + 3) * k : l;
                    dst[o++] = l;
                    dst[o++] = r;
                }
                break;
            }
            case SampleKind.Pcm32:
            {
                int* p = (int*)src;
                const float k = 1f / 2147483648f;
                for (int n = 0; n < frames; n++, p += srcChannels)
                {
                    float l = p[0] * k;
                    float r = srcChannels > 1 ? p[1] * k : l;
                    dst[o++] = l;
                    dst[o++] = r;
                }
                break;
            }
            default:
                dst.Slice(0, frames * 2).Clear();
                break;
        }
    }

    public static void FromStereoFloat(ReadOnlySpan<float> src, int frames, IntPtr dst, SampleKind kind, int dstChannels)
    {
        int i = 0;
        switch (kind)
        {
            case SampleKind.Float32:
            {
                float* p = (float*)dst;
                for (int n = 0; n < frames; n++, p += dstChannels)
                {
                    float l = src[i++];
                    float r = src[i++];
                    WriteFrame(p, dstChannels, l, r);
                }
                break;
            }
            case SampleKind.Pcm16:
            {
                short* p = (short*)dst;
                for (int n = 0; n < frames; n++, p += dstChannels)
                {
                    float l = src[i++];
                    float r = src[i++];
                    if (dstChannels == 1)
                    {
                        p[0] = ToPcm16(0.5f * (l + r));
                    }
                    else
                    {
                        p[0] = ToPcm16(l);
                        p[1] = ToPcm16(r);
                        for (int c = 2; c < dstChannels; c++) p[c] = 0;
                    }
                }
                break;
            }
            case SampleKind.Pcm32:
            {
                int* p = (int*)dst;
                for (int n = 0; n < frames; n++, p += dstChannels)
                {
                    float l = src[i++];
                    float r = src[i++];
                    if (dstChannels == 1)
                    {
                        p[0] = ToPcm32(0.5f * (l + r));
                    }
                    else
                    {
                        p[0] = ToPcm32(l);
                        p[1] = ToPcm32(r);
                        for (int c = 2; c < dstChannels; c++) p[c] = 0;
                    }
                }
                break;
            }
            case SampleKind.Pcm24:
            {
                byte* p = (byte*)dst;
                int stride = dstChannels * 3;
                for (int n = 0; n < frames; n++, p += stride)
                {
                    float l = src[i++];
                    float r = src[i++];
                    if (dstChannels == 1)
                    {
                        Write24(p, 0.5f * (l + r));
                    }
                    else
                    {
                        Write24(p, l);
                        Write24(p + 3, r);
                        for (int c = 2; c < dstChannels; c++) { p[c * 3] = 0; p[c * 3 + 1] = 0; p[c * 3 + 2] = 0; }
                    }
                }
                break;
            }
        }
    }

    private static void WriteFrame(float* p, int channels, float l, float r)
    {
        if (channels == 1)
        {
            p[0] = 0.5f * (l + r);
            return;
        }
        p[0] = l;
        p[1] = r;
        for (int c = 2; c < channels; c++) p[c] = 0f;
    }

    private static int Read24(byte* p) => (p[0] | (p[1] << 8) | (p[2] << 16)) << 8 >> 8;

    private static void Write24(byte* p, float v)
    {
        int s = (int)(Math.Clamp(v, -1f, 1f) * 8388607f);
        p[0] = (byte)s;
        p[1] = (byte)(s >> 8);
        p[2] = (byte)(s >> 16);
    }

    private static short ToPcm16(float v) => (short)(Math.Clamp(v, -1f, 1f) * 32767f);

    private static int ToPcm32(float v) => (int)(Math.Clamp(v, -1f, 1f) * 2147483647f);
}
