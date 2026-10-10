using System.Text;
using Relatude.DB.Common;

namespace Relatude.DB.FileToText;

/// <summary>
/// Whether a file is a recording or a video, and which kind, by its first bytes: what the FileToText
/// provider has the file converters take the sound of before it is sent. A file is judged by what it
/// holds, as the FileToText service judges it, so a video named notes.txt is a video.
/// </summary>
internal static class MediaFiles {
    /// <summary>How much of the start of a file <see cref="Detect"/> needs.</summary>
    public const int HeadBytes = 64;

    static readonly string[] _audioBrands = ["M4A ", "M4B ", "M4P ", "F4A ", "F4B "];
    // ISO media files that are pictures, not recordings or videos
    static readonly string[] _pictureBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1", "avif", "avis", "crx ", "jp2 ", "jpx ", "jpm "];
    static ReadOnlySpan<byte> Ebml => [0x1A, 0x45, 0xDF, 0xA3];
    static ReadOnlySpan<byte> Asf => [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11];

    /// <summary>The kind of recording or video the file is, or null when it is neither.</summary>
    public static FileFormat? Detect(ReadOnlySpan<byte> head) {
        if (head.Length < 4) return null;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8)) {
            if (head.Slice(8, 4).SequenceEqual("WAVE"u8)) return FileFormat.Wav;
            if (head.Slice(8, 4).SequenceEqual("AVI "u8)) return FileFormat.Avi;
            return null;
        }
        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8)) {
            var brand = Encoding.ASCII.GetString(head.Slice(8, 4));
            if (_pictureBrands.Contains(brand)) return null;
            if (_audioBrands.Contains(brand)) return FileFormat.M4a;
            return brand == "qt  " ? FileFormat.Mov : FileFormat.Mp4;
        }
        // a QuickTime file older than the ftyp box starts with one of the boxes a movie has
        if (head.Length >= 8 && (head.Slice(4, 4).SequenceEqual("moov"u8) || head.Slice(4, 4).SequenceEqual("mdat"u8) || head.Slice(4, 4).SequenceEqual("wide"u8))) return FileFormat.Mov;
        if (head.StartsWith(Ebml)) return head.IndexOf("webm"u8) >= 0 ? FileFormat.Webm : FileFormat.Mkv;
        if (head.StartsWith(Asf)) return FileFormat.Wmv; // Windows Media, sound or video
        if (head.StartsWith("FLV\x01"u8)) return FileFormat.Flv;
        if (head.StartsWith("OggS"u8)) return FileFormat.Ogg;
        if (head.StartsWith("fLaC"u8)) return FileFormat.Flac;
        if (head.StartsWith("ID3"u8)) return FileFormat.Mp3;
        // MPEG audio layer III: eleven bits of sync, a version that exists, a bit rate and a sample rate that are not reserved
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && ((head[1] >> 3) & 3) != 1 && ((head[1] >> 1) & 3) == 1
            && (head[2] >> 4) is not (0 or 15) && ((head[2] >> 2) & 3) != 3) return FileFormat.Mp3;
        // ADTS, AAC as a stream: twelve bits of sync and layer 0
        if (head[0] == 0xFF && (head[1] & 0xF6) == 0xF0) return FileFormat.Aac;
        return null;
    }

    /// <summary>The key the FileToText service knows a kind of file by: mp4, ogg and so on.</summary>
    public static string Key(FileFormat format) => format.ToString().ToLowerInvariant();
}
