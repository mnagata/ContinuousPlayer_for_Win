using System.Text.RegularExpressions;

namespace ContinuousPlayer.Core;

public sealed record PcmOutputCandidate(PcmFormat Format, bool Extensible);

public static partial class ExclusiveAudioPolicy
{
    public static IReadOnlyList<PcmOutputCandidate> Candidates(PcmFormat source)
    {
        if (source.Encoding != PcmEncoding.Integer || source.ContainerBits is not (16 or 24 or 32) ||
            source.ValidBits <= 0 || source.ValidBits > source.ContainerBits || source.Channels <= 0 || source.SampleRate <= 0)
            throw new NotSupportedException("対応していないPCM形式です。");
        var candidates = new List<PcmOutputCandidate>();
        void Add(int width, int valid)
        {
            var format = source with { ContainerBits = width, ValidBits = valid };
            if (width == valid && source.Channels <= 2) candidates.Add(new(format with { ChannelMask = 0 }, false));
            candidates.Add(new(format with { ChannelMask = source.ChannelMask != 0 ? source.ChannelMask :
                source.Channels == 1 ? 4 : source.Channels == 2 ? 3 : 0 }, true));
        }
        Add(source.ContainerBits, source.ValidBits);
        if (source.ContainerBits < 32) Add(32, source.ValidBits);
        if (source.ValidBits < 32) Add(32, 32);
        return candidates.Distinct().ToArray();
    }

    public static byte[] Widen(ReadOnlySpan<byte> source, int sourceBits, int targetBits)
    {
        if (sourceBits is not (16 or 24 or 32) || targetBits is not (16 or 24 or 32) || targetBits < sourceBits)
            throw new ArgumentException("PCMの切り捨ては行いません。");
        var fromWidth = sourceBits / 8;
        var toWidth = targetBits / 8;
        if (source.Length % fromWidth != 0) throw new InvalidDataException("不完全なPCMサンプルです。");
        var result = new byte[source.Length / fromWidth * toWidth];
        for (int from = 0, to = 0; from < source.Length; from += fromWidth, to += toWidth)
            source.Slice(from, fromWidth).CopyTo(result.AsSpan(to + toWidth - fromWidth, fromWidth));
        return result;
    }

    // MMDevice endpoint IDs also occur inside WinRT device-interface paths.
    public static bool SameEndpoint(string? first, string? second) => first != null && second != null &&
        string.Equals(Canonical(first), Canonical(second), StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string value)
    {
        var match = EndpointPattern().Match(value);
        return match.Success ? match.Value : value;
    }
    [GeneratedRegex(@"\{0\.0\.[01]\.00000000\}\.\{[0-9a-fA-F-]{36}\}")]
    private static partial Regex EndpointPattern();
}
