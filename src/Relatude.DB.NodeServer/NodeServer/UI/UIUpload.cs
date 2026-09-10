using Relatude.DB.IO;
using Relatude.DB.NodeServer.Json;
using System.Buffers.Binary;
using System.Text;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The file uploads of the admin UI. Every file lands in a temp folder first and is moved onto its
/// real key only once the last byte has arrived, so a cancelled or broken upload never leaves half
/// a file where a whole one is expected. Two shapes, both binary, so neither is a command:
/// <code>
///   POST ui/upload-part    one slice of a file, appended to that upload's temp file
///   POST ui/upload-commit  the temp file moved onto its key
///   POST ui/upload-abort   the temp file dropped
///   POST ui/upload-batch   many whole files in one request
/// </code>
/// Slicing keeps a big file's progress honest and lets a dropped connection resume from the byte
/// the server has rather than from zero. The batch is the other end of the same problem: a folder
/// of thousands of tiny files costs one round trip per batch instead of one per file, which is
/// what the latency of the upload is actually made of.
/// <para>The batch body is framed, all little endian: for every file an int32 name length, that
/// many utf-8 bytes of the '/'-separated name relative to the target folder, an int64 file length,
/// and then that many bytes of the file. End of body ends the batch.</para>
/// </summary>
internal sealed class UIUpload {
    const int copyBufferSize = 128 * 1024;
    const int maxRelativeNameBytes = 4096;
    readonly RelatudeDBServer _server;
    internal UIUpload(RelatudeDBServer server) => _server = server;

    internal void Map(WebApplication app, string path) {
        app.MapPost(path + "upload-part", (HttpContext ctx, Guid ioId, Guid uploadId, long offset) => uploadPartAsync(ctx, ioId, uploadId, offset));
        app.MapPost(path + "upload-commit", (Guid ioId, Guid uploadId, string key, long size) => commit(ioId, uploadId, key, size));
        app.MapPost(path + "upload-abort", (Guid ioId, Guid uploadId) => abort(ioId, uploadId));
        app.MapPost(path + "upload-batch", (HttpContext ctx, Guid ioId, string? basePath) => uploadBatchAsync(ctx, ioId, basePath));
    }

    /// <summary>
    /// Appends one slice to the upload's temp file. The offset the client believes it is at must be
    /// the size the temp file actually has: a mismatch answers 409 with the size the server holds,
    /// which is what a client resumes from. The answer carries the new size for the same reason.
    /// </summary>
    async Task<IResult> uploadPartAsync(HttpContext ctx, Guid ioId, Guid uploadId, long offset) {
        unlimitBody(ctx);
        var io = _server.GetIO(ioId);
        var temp = tempKey(uploadId);
        var received = io.GetFileSizeOrZeroIfUnknown(temp);
        if (offset == 0 && received > 0) { // a restarted upload reusing its id
            io.DeleteFileIfItExists(temp);
            received = 0;
        }
        if (offset != received) {
            return Results.Json(new { error = $"The upload holds {received} bytes, not {offset}. ", received }, RelatudeDBJsonOptions.Default, statusCode: 409);
        }
        try {
            using var ioStream = io.OpenAppend(temp);
            using var writeStream = new WriteStreamWrapper(ioStream);
            await ctx.Request.Body.CopyToAsync(writeStream, ctx.RequestAborted);
        } catch (Exception) {
            // half a slice is worse than none: the file goes back to where the client thinks it is,
            // so the same slice can simply be sent again. Nothing here may mask the real error.
            try {
                if (io.CanTruncate && io.GetFileSizeOrZeroIfUnknown(temp) > offset) io.TruncateFile(temp, offset);
                else if (!io.CanTruncate) io.DeleteFileIfItExists(temp);
            } catch { }
            throw;
        }
        return Results.Json(new { received = io.GetFileSizeOrZeroIfUnknown(temp) }, RelatudeDBJsonOptions.Default);
    }

    IResult commit(Guid ioId, Guid uploadId, string key, long size) {
        var io = _server.GetIO(ioId);
        var temp = tempKey(uploadId);
        var fileKey = key.SplitKey();
        if (!isWritableKey(io, fileKey, out var error)) {
            io.DeleteFileIfItExists(temp);
            return Results.BadRequest(new { error });
        }
        var received = io.GetFileSizeOrZeroIfUnknown(temp);
        if (received != size) {
            io.DeleteFileIfItExists(temp);
            return Results.BadRequest(new { error = $"The upload holds {received} bytes, {size} were expected. " });
        }
        try {
            moveIntoPlace(io, temp, fileKey);
        } catch (Exception exception) {
            io.DeleteFileIfItExists(temp);
            return Results.BadRequest(new { error = exception.Message }); // a locked destination, say
        }
        return Results.Ok();
    }

    IResult abort(Guid ioId, Guid uploadId) {
        var io = _server.GetIO(ioId);
        io.DeleteFileIfItExists(tempKey(uploadId));
        return Results.Ok();
    }

    /// <summary>
    /// Reads whole files off one request body (see the framing above) and writes each into place.
    /// A file that cannot be written is reported by name and the upload carries on with the next
    /// one: its bytes are read and dropped, so the framing stays aligned whatever went wrong.
    /// </summary>
    async Task<IResult> uploadBatchAsync(HttpContext ctx, Guid ioId, string? basePath) {
        unlimitBody(ctx);
        var io = _server.GetIO(ioId);
        var baseKey = string.IsNullOrEmpty(basePath) ? [] : basePath.SplitKey();
        if (baseKey.Any(segment => !UIServer.IsValidSegment(io, segment))) return Results.BadRequest(new { error = "Invalid target folder. " });
        var body = ctx.Request.Body;
        var buffer = new byte[copyBufferSize];
        var errors = new List<string>();
        var written = 0;
        while (true) {
            var header = await readOrNullAtEndAsync(body, 4, ctx.RequestAborted);
            if (header == null) break;
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (nameLength <= 0 || nameLength > maxRelativeNameBytes) return Results.BadRequest(new { error = "Malformed upload batch. " });
            var name = Encoding.UTF8.GetString(await readAsync(body, nameLength, ctx.RequestAborted));
            var length = BinaryPrimitives.ReadInt64LittleEndian(await readAsync(body, 8, ctx.RequestAborted));
            if (length < 0) return Results.BadRequest(new { error = "Malformed upload batch. " });
            string[] fileKey = [.. baseKey, .. name.SplitKey()];
            var temp = tempKey(Guid.NewGuid());
            var failure = isWritableKey(io, fileKey, out var keyError) ? null : keyError;
            IAppendStream? stream = null;
            if (failure == null) {
                try {
                    stream = io.OpenAppend(temp);
                } catch (Exception exception) {
                    failure = exception.Message;
                }
            }
            var remaining = length;
            while (remaining > 0) {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ctx.RequestAborted);
                if (read == 0) throw new EndOfStreamException("The upload ended before " + name + " was complete. ");
                remaining -= read;
                if (stream == null) continue;
                try {
                    stream.Append(buffer, read);
                } catch (Exception exception) {
                    failure = exception.Message;
                    stream.Dispose();
                    stream = null;
                }
            }
            stream?.Dispose();
            if (failure == null) {
                try {
                    moveIntoPlace(io, temp, fileKey);
                    written++;
                } catch (Exception exception) {
                    failure = exception.Message;
                }
            }
            if (failure != null) {
                io.DeleteFileIfItExists(temp);
                errors.Add(name + ": " + failure);
            }
        }
        return Results.Json(new { written, errors }, RelatudeDBJsonOptions.Default);
    }

    // Providers that cannot move a file (blob storage) copy the bytes instead; everything else
    // renames, which is a metadata operation even for a file of gigabytes.
    static void moveIntoPlace(IIOProvider io, string[] temp, string[] fileKey) {
        io.DeleteFileIfItExists(fileKey);
        if (io.CanRenameFile) {
            io.RenameFile(temp, fileKey);
        } else {
            io.CopyFile(temp, fileKey);
            io.DeleteFileIfItExists(temp);
        }
    }

    static string[] tempKey(Guid uploadId) => [FileKeyUtility.UploadFolderName, uploadId.ToString("N") + ".part"];

    static bool isWritableKey(IIOProvider io, string[] fileKey, out string? error) {
        if (fileKey.Length == 0 || fileKey.Any(segment => !UIServer.IsValidSegment(io, segment))) {
            error = "Invalid file key. ";
            return false;
        }
        if (FileKeyUtility.State_IsStateFileKey(fileKey)) {
            error = "Uploading the state file is not allowed. ";
            return false;
        }
        error = null;
        return true;
    }

    // an uploaded database file is far past the default request limit
    static void unlimitBody(HttpContext ctx) {
        var sizeFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null && !sizeFeature.IsReadOnly) sizeFeature.MaxRequestBodySize = null;
    }

    static async Task<byte[]> readAsync(Stream body, int count, CancellationToken token) {
        var buffer = new byte[count];
        await body.ReadExactlyAsync(buffer, token);
        return buffer;
    }

    // the same, except that a body ending exactly here is the end of the batch rather than an error
    static async Task<byte[]?> readOrNullAtEndAsync(Stream body, int count, CancellationToken token) {
        var buffer = new byte[count];
        var read = await body.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, token);
        if (read == 0) return null;
        if (read < count) throw new EndOfStreamException("The upload batch ended mid header. ");
        return buffer;
    }
}
