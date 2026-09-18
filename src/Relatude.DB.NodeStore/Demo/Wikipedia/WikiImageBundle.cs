using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Relatude.DB.Demo.Wikipedia;

/// <summary>One picture in a bundle, as the index describes it.</summary>
public readonly record struct WikiBundleImage(string FileName, WikiImageFormat Format, bool IsLead, long Offset, int Length) {
    public string ContentType => WikiImageFormats.ToContentType(Format);
    public string FileExtension => WikiImageFormats.ToExtension(Format);
}

/// <summary>What a picture in a bundle actually is, whatever its file name claims.</summary>
public enum WikiImageFormat : byte {
    Unknown = 0,
    Jpeg = 1,
    Png = 2,
    Gif = 3,
    Webp = 4,
    Svg = 5,
    Avif = 6,
    Bmp = 7,
    Tiff = 8,
}

public static class WikiImageFormats {
    public static string ToContentType(WikiImageFormat format) => format switch {
        WikiImageFormat.Jpeg => "image/jpeg",
        WikiImageFormat.Png => "image/png",
        WikiImageFormat.Gif => "image/gif",
        WikiImageFormat.Webp => "image/webp",
        WikiImageFormat.Svg => "image/svg+xml",
        WikiImageFormat.Avif => "image/avif",
        WikiImageFormat.Bmp => "image/bmp",
        WikiImageFormat.Tiff => "image/tiff",
        _ => "application/octet-stream",
    };

    /// <summary>The extension the bytes should be stored under, including the dot.</summary>
    public static string ToExtension(WikiImageFormat format) => format switch {
        WikiImageFormat.Jpeg => ".jpg",
        WikiImageFormat.Png => ".png",
        WikiImageFormat.Gif => ".gif",
        WikiImageFormat.Webp => ".webp",
        WikiImageFormat.Svg => ".svg",
        WikiImageFormat.Avif => ".avif",
        WikiImageFormat.Bmp => ".bmp",
        WikiImageFormat.Tiff => ".tif",
        _ => ".img",
    };
}

/// <summary>
/// Reads a <c>.wikimg</c> image bundle: the pictures of a Wikipedia corpus in one indexed file,
/// written by the Wikipedia Corpus Builder.
///
/// <para>
/// The pictures originally live in a Kiwix ZIM archive, which is a real format - a sorted
/// directory of tens of millions of entries and zstd compressed clusters - and matching one to an
/// article means parsing the article's HTML and undoing two layers of URI escaping to get back to
/// its Commons file name. None of that happens here. The corpus builder does all of it once and
/// writes this instead, so the whole reader is a file handle, <see cref="BinaryPrimitives"/> and
/// <see cref="Encoding.UTF8"/> - no package reference, no decoder, nothing to keep in step with an
/// external format.
/// </para>
///
/// <para>
/// Nothing is indexed on open, and memory does not depend on the size of the bundle. A lookup
/// binary searches the sorted article table on disk, which is about twenty reads of sixteen bytes
/// at three million articles, so an import that wants a few thousand articles out of a thirty
/// gigabyte bundle reads only what it asks for.
/// </para>
///
/// <para>
/// The layout, little endian throughout, is:
/// </para>
/// <code>
/// 0    8   magic "RWIKIMG1"
/// 8    4   version
/// 12   4   flags
/// 16   8   articleCount
/// 24   8   blobCount
/// 32   8   tableOffset
/// 40   8   blobBytes
/// 48   8   createdUtc ticks
/// 56   8   reserved
/// 64  ...  manifests and raw image bytes, interleaved
/// table    articleCount records of [pageId:int64][manifestOffset:int64], sorted by pageId
/// </code>
/// <para>
/// A manifest is <c>imageCount:int32</c> followed, per picture, by
/// <c>format:byte, isLead:byte, nameLength:int32, name, blobOffset:int64, blobLength:int32</c>.
/// Blobs are raw bytes with no framing and no compression - every format in there is compressed
/// already - and two articles using the same picture point at the same blob.
/// </para>
/// </summary>
public sealed class WikiImageBundle : IDisposable {
    static ReadOnlySpan<byte> magic => "RWIKIMG1"u8;
    const int supportedVersion = 1;
    const int headerSize = 64;
    const int tableRecordSize = 16;

    readonly SafeFileHandle _file;
    readonly long _tableOffset;

    WikiImageBundle(SafeFileHandle file, long articleCount, long blobCount, long tableOffset, long blobBytes, DateTime createdUtc) {
        _file = file;
        _tableOffset = tableOffset;
        ArticleCount = articleCount;
        BlobCount = blobCount;
        BlobBytes = blobBytes;
        CreatedUtc = createdUtc;
    }

    /// <summary>Articles that have at least one picture in the bundle.</summary>
    public long ArticleCount { get; }
    /// <summary>Distinct pictures, after the deduplication the builder did.</summary>
    public long BlobCount { get; }
    /// <summary>Bytes of image data, excluding the index.</summary>
    public long BlobBytes { get; }
    /// <summary>When the bundle was built.</summary>
    public DateTime CreatedUtc { get; }

    /// <summary>Articles looked up that the bundle had nothing for.</summary>
    public long ArticlesNotFound { get; private set; }
    /// <summary>Pictures asked for that the bundle did not carry.</summary>
    public long ImagesNotFound { get; private set; }
    /// <summary>Pictures whose bytes were read out.</summary>
    public long ImagesRead { get; private set; }

    public static WikiImageBundle Open(string path) {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("No bundle path given. ", nameof(path));
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try {
            Span<byte> header = stackalloc byte[headerSize];
            readExactly(file, header, 0);
            if (!header[..8].SequenceEqual(magic))
                throw new InvalidDataException("Not a Wikipedia image bundle: the magic number does not match. ");
            var version = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            if (version != supportedVersion)
                throw new InvalidDataException("Image bundle version " + version + " is not supported; this build reads version " + supportedVersion + ". ");
            return new WikiImageBundle(
                file,
                BinaryPrimitives.ReadInt64LittleEndian(header[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(header[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(header[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(header[40..]),
                new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header[48..]), DateTimeKind.Utc));
        } catch {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the pictures of one article that the caller asked for, keyed by the corpus file name,
    /// so the caller can tell which of them the bundle actually had.
    /// </summary>
    public Dictionary<string, WikiImageBytes> ReadImages(long pageId, IReadOnlyList<string> wantedFileNames, long maxBytesPerImage = 16L * 1024 * 1024) {
        var result = new Dictionary<string, WikiImageBytes>(StringComparer.OrdinalIgnoreCase);
        if (wantedFileNames.Count == 0) return result;

        var available = GetImages(pageId);
        if (available.Count == 0) {
            ArticlesNotFound++;
            ImagesNotFound += wantedFileNames.Count;
            return result;
        }

        foreach (var wanted in wantedFileNames) {
            if (result.ContainsKey(wanted)) continue;

            var match = available.FirstOrDefault(i => string.Equals(i.FileName, wanted, StringComparison.OrdinalIgnoreCase));
            if (match.Length <= 0 || match.Length > maxBytesPerImage) {
                ImagesNotFound++;
                continue;
            }

            result[wanted] = new WikiImageBytes(ReadBytes(match), match.ContentType, match.FileExtension);
            ImagesRead++;
        }
        return result;
    }

    /// <summary>Everything the bundle holds for one article, in the order the corpus listed it.
    /// Empty when the bundle has nothing for that article.</summary>
    public IReadOnlyList<WikiBundleImage> GetImages(long pageId) {
        if (!tryFindManifest(pageId, out var manifestOffset)) return [];
        return readManifest(manifestOffset);
    }

    /// <summary>The bytes of one picture.</summary>
    public byte[] ReadBytes(WikiBundleImage image) {
        if (image.Length <= 0) return [];
        var data = new byte[image.Length];
        readExactly(_file, data, image.Offset);
        return data;
    }

    bool tryFindManifest(long pageId, out long manifestOffset) {
        Span<byte> record = stackalloc byte[tableRecordSize];
        long low = 0, high = ArticleCount - 1;
        while (low <= high) {
            var mid = low + (high - low) / 2;
            readExactly(_file, record, _tableOffset + mid * tableRecordSize);
            var found = BinaryPrimitives.ReadInt64LittleEndian(record);
            if (found == pageId) {
                manifestOffset = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
                return true;
            }
            if (found < pageId) low = mid + 1; else high = mid - 1;
        }
        manifestOffset = 0;
        return false;
    }

    List<WikiBundleImage> readManifest(long offset) {
        Span<byte> count = stackalloc byte[4];
        readExactly(_file, count, offset);
        var imageCount = BinaryPrimitives.ReadInt32LittleEndian(count);
        if (imageCount is <= 0 or > 4096) return [];

        // One read covers the whole manifest in every realistic case, and it is grown rather than
        // read field by field because a seek per field would cost far more than the slack.
        var buffer = new byte[4 + imageCount * 160];
        var read = readAtMost(_file, buffer, offset);

        var images = new List<WikiBundleImage>(imageCount);
        var at = 4;
        for (var i = 0; i < imageCount; i++) {
            if (at + 6 > read && !tryGrow(ref buffer, ref read, offset, at + 6)) break;

            var format = (WikiImageFormat)buffer[at];
            var isLead = buffer[at + 1] != 0;
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at + 2));
            if (nameLength is < 0 or > 8192) break;

            var next = at + 6 + nameLength + 12;
            if (next > read && !tryGrow(ref buffer, ref read, offset, next)) break;

            var name = Encoding.UTF8.GetString(buffer, at + 6, nameLength);
            var blobOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(at + 6 + nameLength));
            var blobLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at + 6 + nameLength + 8));

            images.Add(new WikiBundleImage(name, format, isLead, blobOffset, blobLength));
            at = next;
        }
        return images;
    }

    bool tryGrow(ref byte[] buffer, ref int read, long offset, int atLeast) {
        var bigger = new byte[Math.Max(buffer.Length * 2, atLeast + 64)];
        read = readAtMost(_file, bigger, offset);
        buffer = bigger;
        return read >= atLeast;
    }

    /// <summary>Fills the buffer, or returns fewer bytes only at the end of the file. One
    /// <see cref="RandomAccess.Read(SafeFileHandle, Span{byte}, long)"/> may return less than was
    /// asked for, which on a multi megabyte image would quietly produce a corrupt picture.</summary>
    static int readAtMost(SafeFileHandle file, Span<byte> buffer, long offset) {
        var total = 0;
        while (total < buffer.Length) {
            var read = RandomAccess.Read(file, buffer[total..], offset + total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    static void readExactly(SafeFileHandle file, Span<byte> buffer, long offset) {
        if (readAtMost(file, buffer, offset) < buffer.Length)
            throw new EndOfStreamException("The image bundle ends before offset " + (offset + buffer.Length) + "; it is truncated. ");
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>The bytes of one picture, plus what they are.</summary>
public readonly record struct WikiImageBytes(byte[] Data, string ContentType, string FileExtension);
