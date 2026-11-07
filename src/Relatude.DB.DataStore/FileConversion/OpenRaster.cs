using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Relatude.DB.FileConversion;

/// <summary>
/// What an OpenRaster file (.ora) holds: <c>stack.xml</c>, saying how the layers stack, and every other entry by its path
/// in the zip (the layers, <c>mergedimage.png</c>, the thumbnail, and whatever else a program put there). The
/// <c>mimetype</c> entry is not among them: it is written by <see cref="OpenRaster.Write"/>.
/// </summary>
public sealed class OpenRasterContent {
    public string StackXml { get; set; } = string.Empty;
    public Dictionary<string, byte[]> Entries { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The picture's size, from stack.xml's <c>image</c>.</summary>
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// OpenRaster files (https://www.openraster.org): a zip of layers, PNG or, in the extended format, SVG, with stack.xml
/// saying how they stack and mergedimage.png the picture they make. The engine never composes layers: what it converts
/// and shows of such a file is its merged image, which whatever wrote the file made. Reading and writing the zip is
/// here, so an editor in a browser hands over the layers and the merged image it drew and gets the file back.
/// </summary>
public static class OpenRaster {
    public const string MimeType = "image/openraster";
    public const string StackEntry = "stack.xml";
    public const string MergedImageEntry = "mergedimage.png";
    public const string ThumbnailEntry = "Thumbnails/thumbnail.png";
    const string mimetypeEntry = "mimetype";
    /// <summary>The thumbnail's size at most, each way, as the format says.</summary>
    public const int ThumbnailSize = 256;
    /// <summary>What a file may hold, unpacked, before reading it is refused (a zip can unpack to far more than it is).</summary>
    public const long DefaultMaxUnpacked = 1L << 30;
    static readonly byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The picture the layers make, as stored in the file (a PNG).</summary>
    public static MemoryStream ReadMergedImage(Stream ora) {
        using var zip = open(ora);
        var entry = zip.GetEntry(MergedImageEntry) ?? throw new InvalidDataException("The OpenRaster file has no " + MergedImageEntry + ".");
        if (entry.Length > DefaultMaxUnpacked) throw new InvalidDataException("The OpenRaster file's merged image is too large.");
        var output = new MemoryStream((int)Math.Max(0, entry.Length));
        using (var stream = entry.Open()) stream.CopyTo(output);
        output.Position = 0;
        return output;
    }

    /// <summary>Every entry of the file but its mimetype, and its stack read for the picture's size.</summary>
    public static OpenRasterContent Read(Stream ora, long maxUnpacked = DefaultMaxUnpacked) {
        using var zip = open(ora);
        var content = new OpenRasterContent();
        long total = 0;
        foreach (var entry in zip.Entries) {
            if (entry.FullName.EndsWith('/')) continue; // a folder
            if (entry.FullName == mimetypeEntry) continue;
            total += entry.Length;
            if (total > maxUnpacked) throw new InvalidDataException("The OpenRaster file unpacks to more than " + maxUnpacked + " bytes.");
            var bytes = new byte[entry.Length];
            using (var stream = entry.Open()) stream.ReadExactly(bytes);
            if (entry.FullName == StackEntry) content.StackXml = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            else content.Entries[entry.FullName] = bytes;
        }
        if (content.StackXml.Length == 0) throw new InvalidDataException("The OpenRaster file has no " + StackEntry + ".");
        (content.Width, content.Height) = sizeOf(parseStack(content.StackXml));
        return content;
    }

    /// <summary>
    /// The file holding the content: <c>mimetype</c> first and stored, as the format asks, then stack.xml and the entries.
    /// The stack must name the picture's size and only layers among the entries, every path in the zip must be a plain
    /// relative one, and there must be a merged image; a thumbnail is made from it when there is none.
    /// </summary>
    public static void Write(Stream output, OpenRasterContent content) {
        var stack = parseStack(content.StackXml);
        sizeOf(stack);
        foreach (var path in content.Entries.Keys) {
            if (!IsSafePath(path)) throw new InvalidDataException("Not a path an OpenRaster file may hold: " + path);
            if (path == StackEntry || path == mimetypeEntry) throw new InvalidDataException("The entry " + path + " is the file's own.");
        }
        foreach (var layer in stack.Descendants("layer")) {
            var src = (string?)layer.Attribute("src");
            if (string.IsNullOrEmpty(src)) throw new InvalidDataException("A layer in the stack has no src.");
            if (!content.Entries.ContainsKey(src)) throw new InvalidDataException("The stack names a layer the file does not hold: " + src);
        }
        if (!content.Entries.TryGetValue(MergedImageEntry, out var merged)) throw new InvalidDataException("An OpenRaster file needs its " + MergedImageEntry + ".");
        if (!IsPng(merged)) throw new InvalidDataException("The merged image is not a PNG.");
        var thumbnail = content.Entries.TryGetValue(ThumbnailEntry, out var given) ? given : MakeThumbnail(merged);
        var buffer = output.CanSeek ? output : new MemoryStream(); // a zip written to a stream that cannot seek gets data descriptors
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true)) {
            add(zip, mimetypeEntry, System.Text.Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            add(zip, StackEntry, System.Text.Encoding.UTF8.GetBytes(content.StackXml), CompressionLevel.Optimal);
            foreach (var (path, bytes) in content.Entries.OrderBy(e => e.Key, StringComparer.Ordinal)) {
                if (path == ThumbnailEntry) continue;
                add(zip, path, bytes, levelFor(path));
            }
            add(zip, ThumbnailEntry, thumbnail, CompressionLevel.NoCompression);
        }
        if (buffer != output) {
            buffer.Position = 0;
            buffer.CopyTo(output);
        }
    }

    /// <summary>The file as bytes.</summary>
    public static byte[] Write(OpenRasterContent content) {
        using var output = new MemoryStream();
        Write(output, content);
        return output.ToArray();
    }

    /// <summary>A relative path of names, no folder above it, no drive and no backslashes: what a zip entry of ours may be.</summary>
    public static bool IsSafePath(string path) {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) return false;
        foreach (var part in path.Split('/')) {
            if (part.Length == 0 || part == "." || part == "..") return false;
        }
        return true;
    }

    public static bool IsPng(byte[] bytes) => bytes.Length >= pngSignature.Length && bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature);

    /// <summary>
    /// The merged image made small enough for the format's thumbnail, its shape kept; the merged image itself when it is
    /// small already, or when it cannot be decoded here (a thumbnail is never why a file is not written).
    /// </summary>
    public static byte[] MakeThumbnail(byte[] png) {
        try {
            var (width, height) = NativeImage.ReadSize(new MemoryStream(png));
            if (width <= ThumbnailSize && height <= ThumbnailSize) return png;
            using var image = NativeImage.Load(new MemoryStream(png));
            var adj = new FileAdjustmentImage { RequestedFormat = FileFormat.Png };
            if (width >= height) adj.Width = ThumbnailSize;
            else adj.Height = ThumbnailSize;
            using var small = image.Adjust(adj);
            return small.Encode(FileFormat.Png);
        } catch (Exception) {
            return png;
        }
    }

    static ZipArchive open(Stream ora) {
        try {
            return new ZipArchive(ora, ZipArchiveMode.Read, leaveOpen: true);
        } catch (InvalidDataException error) {
            throw new InvalidDataException("Not an OpenRaster file: it is no zip. " + error.Message, error);
        }
    }
    static void add(ZipArchive zip, string path, byte[] bytes, CompressionLevel level) {
        var entry = zip.CreateEntry(path, level);
        using var stream = entry.Open();
        stream.Write(bytes);
    }
    // pictures are compressed already
    static CompressionLevel levelFor(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
        ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

    static XElement parseStack(string xml) {
        XDocument document;
        try {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        } catch (XmlException error) {
            throw new InvalidDataException("The OpenRaster stack is not XML: " + error.Message, error);
        }
        var root = document.Root;
        if (root == null || root.Name.LocalName != "image") throw new InvalidDataException("The OpenRaster stack's root is not an image.");
        return root;
    }
    static (int Width, int Height) sizeOf(XElement image) {
        if (!int.TryParse((string?)image.Attribute("w"), out var w) || !int.TryParse((string?)image.Attribute("h"), out var h) || w <= 0 || h <= 0) {
            throw new InvalidDataException("The OpenRaster stack does not give the picture's size.");
        }
        return (w, h);
    }
}
