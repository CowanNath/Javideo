using System.Buffers.Binary;
using System.Text;

namespace Javideo.Worker.Services;

/// <summary>
/// Best-effort real duration of a local video file, parsed natively (no
/// ffmpeg): MP4/MOV via the moov/mvhd box, MKV via the Segment/Info/Duration
/// element. Other containers (.avi/.wmv/.ts/.strm) return false — callers fall
/// back to the scraped runtime.
/// </summary>
public static class MediaDuration
{
    public static bool TryGetSeconds(string path, out double seconds)
    {
        seconds = 0;
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp4" or ".m4v" or ".mov" => TryMp4(path, out seconds),
                ".mkv" => TryMkv(path, out seconds),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    // --- MP4/MOV: moov → mvhd → timescale + duration ---

    private static bool TryMp4(string path, out double seconds)
    {
        seconds = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!FindBox(fs, "moov", out var moovStart, out var moovSize)) return false;

        var pos = moovStart;
        var end = moovStart + moovSize;
        while (pos + 8 <= end)
        {
            var (total, type, content) = ReadBoxHeader(fs, pos, end);
            if (total <= 0) return false;
            if (type == "mvhd")
            {
                Span<byte> buf = stackalloc byte[32];
                fs.Seek(content, SeekOrigin.Begin);
                if (content + buf.Length > pos + total) return false;
                fs.ReadExactly(buf);
                byte version = buf[0];
                if (version == 1)
                {
                    uint timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(20, 4));
                    ulong duration = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(24, 8));
                    if (timescale == 0) return false;
                    seconds = duration / (double)timescale;
                }
                else if (version == 0)
                {
                    uint timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(12, 4));
                    uint duration = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(16, 4));
                    if (timescale == 0) return false;
                    seconds = duration / (double)timescale;
                }
                return double.IsFinite(seconds) && seconds > 0;
            }
            pos = pos + total;
        }
        return false;
    }

    /// <summary>Scan the stream for a top-level box by type. Returns its start
    /// offset and total size (header included).</summary>
    private static bool FindBox(Stream fs, string type, out long start, out long size)
    {
        start = 0; size = 0;
        var pos = 0L;
        var len = fs.Length;
        while (pos + 8 <= len)
        {
            var (total, boxType, content) = ReadBoxHeader(fs, pos, len);
            if (total <= 0) return false;
            if (boxType == type) { start = content; size = total - (content - pos); return true; }
            pos = pos + total;
        }
        return false;
    }

    private static (long Total, string Type, long ContentStart) ReadBoxHeader(Stream fs, long pos, long limit)
    {
        fs.Seek(pos, SeekOrigin.Begin);
        Span<byte> h = stackalloc byte[8];
        fs.ReadExactly(h);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(h);
        var type = Encoding.ASCII.GetString(h.Slice(4, 4));
        int headerLen = 8;
        long total = size;
        if (size == 1)
        {
            if (limit - pos < 16) return (0, type, pos + 8);
            Span<byte> large = stackalloc byte[8];
            fs.ReadExactly(large);
            total = (long)BinaryPrimitives.ReadUInt64BigEndian(large);
            headerLen = 16;
        }
        else if (size == 0)
        {
            total = limit - pos; // box extends to end of enclosing container
        }
        if (total < headerLen || total > limit - pos) return (0, type, pos + headerLen);
        return (total, type, pos + headerLen);
    }

    // --- MKV: Segment → Info → Duration (float) × TimecodeScale ---

    private const long EbmlHeaderId = 0x1A45DFA3;
    private const long SegmentId = 0x18538067;
    private const long InfoId = 0x1549A966;
    private const long DurationId = 0x4489;
    private const long TimecodeScaleId = 0x2AD7B1;

    private static bool TryMkv(string path, out double seconds)
    {
        seconds = 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var len = fs.Length;

        long pos = 0;
        // Skip the top-level EBML header element.
        if (!ReadElementId(fs, ref pos, out var id)) return false;
        if (id != EbmlHeaderId) return false;
        if (!ReadElementSize(fs, ref pos, out var headerSize, out var headerUnknown) || headerUnknown || headerSize > len - pos) return false;
        pos += headerSize;
        if (pos >= len) return false;

        // Walk top-level elements until Segment.
        bool inSegment = false;
        long segmentEnd = len;
        while (pos < len)
        {
            if (!ReadElementId(fs, ref pos, out id)) return false;
            if (!ReadElementSize(fs, ref pos, out var size, out var unknownSize)) return false;
            var content = pos;
            if (!unknownSize && size > len - content) return false;
            if (id == SegmentId)
            {
                inSegment = true;
                segmentEnd = unknownSize ? len : Math.Min(content + size, len);
                pos = content;
                break;
            }
            if (unknownSize) return false;
            pos = content + size;
        }
        if (!inSegment) return false;

        // Walk Segment children until Info.
        long infoStart = 0, infoSize = 0;
        bool foundInfo = false;
        while (pos < segmentEnd)
        {
            if (!ReadElementId(fs, ref pos, out id)) return false;
            if (!ReadElementSize(fs, ref pos, out var size, out var unknownSize)) return false;
            var content = pos;
            if (content > segmentEnd || (!unknownSize && size > segmentEnd - content)) return false;
            if (id == InfoId)
            {
                infoStart = content;
                infoSize = unknownSize ? segmentEnd - content : size;
                foundInfo = true;
                break;
            }
            if (unknownSize) return false; // can't skip an unknown-size non-Info element safely
            pos = content + size;
        }
        if (!foundInfo) return false;

        // Walk Info children for Duration + TimecodeScale.
        double duration = 0;
        long scale = 1_000_000; // spec default (nanoseconds)
        var p = infoStart;
        var infoEnd = infoStart + infoSize;
        Span<byte> buf = stackalloc byte[8];
        while (p < infoEnd)
        {
            if (!ReadElementId(fs, ref p, out id)) return false;
            if (!ReadElementSize(fs, ref p, out var size, out var unknownSize)) return false;
            if (unknownSize || p > infoEnd || size > infoEnd - p) return false;
            if (id == DurationId && (size == 4 || size == 8))
            {
                buf.Clear();
                fs.Seek(p, SeekOrigin.Begin);
                fs.ReadExactly(buf.Slice(0, (int)size));
                duration = size == 4
                    ? BinaryPrimitives.ReadSingleBigEndian(buf.Slice(0, 4))
                    : BinaryPrimitives.ReadDoubleBigEndian(buf.Slice(0, 8));
            }
            else if (id == TimecodeScaleId && size is > 0 and <= 8)
            {
                buf.Clear();
                fs.Seek(p, SeekOrigin.Begin);
                fs.ReadExactly(buf.Slice(8 - (int)size, (int)size));
                scale = (long)BinaryPrimitives.ReadUInt64BigEndian(buf);
                if (scale <= 0) return false;
            }
            p += size;
        }
        if (duration <= 0) return false;
        seconds = duration * scale / 1e9;
        return double.IsFinite(seconds) && seconds > 0;
    }

    // EBML variable-length integers: ID keeps its marker bits, size strips them.
    private static bool ReadElementId(Stream fs, ref long pos, out long id)
    {
        id = 0;
        fs.Seek(pos, SeekOrigin.Begin);
        var first = fs.ReadByte();
        if (first < 0) return false;
        int len = 1;
        while (len <= 4 && (first & (0x80 >> (len - 1))) == 0) len++;
        if (len > 4) return false;
        long value = first;
        for (var i = 1; i < len; i++)
        {
            var b = fs.ReadByte();
            if (b < 0) return false;
            value = (value << 8) | (byte)b;
        }
        pos += len;
        id = value;
        return true;
    }

    private static bool ReadElementSize(Stream fs, ref long pos, out long size, out bool unknown)
    {
        size = 0;
        unknown = false;
        fs.Seek(pos, SeekOrigin.Begin);
        var first = fs.ReadByte();
        if (first < 0) return false;
        int len = 1;
        while (len <= 8 && (first & (0x80 >> (len - 1))) == 0) len++;
        if (len > 8) return false;
        long value = first & (0xFF >> len);
        for (var i = 1; i < len; i++)
        {
            var b = fs.ReadByte();
            if (b < 0) return false;
            value = (value << 8) | (byte)b;
        }
        pos += len;
        unknown = value == (1L << (7 * len)) - 1; // all bits set → unknown size
        size = value;
        return true;
    }
}
