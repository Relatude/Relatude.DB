using Relatude.DB.IO;
using Relatude.DB.NodeServer.Json;
using System.Buffers.Binary;
using System.Text;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The file transfers of the admin UI: uploads, and the batched half of the folder download. All
/// binary, so none of them is a command.
/// <code>
///   POST ui/upload-part      one slice of a file, appended to that upload's temp file
///   POST ui/upload-commit    the temp file moved onto its key
///   POST ui/upload-abort     the temp file dropped
///   POST ui/upload-batch     many whole files in one request
///   POST ui/download-batch   many whole files back in one response
/// </code>
/// An upload lands in a temp folder and is moved onto its real key only once the last byte has
/// arrived, so a cancelled or broken one never leaves half a file where a whole one is expected.
/// Slicing keeps a big file's progress honest and lets a dropped connection resume from the byte
/// the server has rather than from zero.
/// <para>The batches are the other end of the same problem, and the reason both directions have
/// one: a folder of thousands of tiny files costs one round trip per batch instead of one per
/// file, which is what the time such a transfer takes is really made of.</para>
/// <para>Both batch bodies are framed the same way, all little endian: for every file an int32
/// name length, that many utf-8 bytes of the name, an int64 length, and then that many bytes.
/// End of body ends the batch. A download adds one byte between the name and the length, since a
/// file that cannot be read has to be reported without stopping the rest: 1 means the length and
/// the bytes are the file's, 0 means they are the reason it could not be read.</para>
/// </summary>
internal sealed class UIFileTransfer {
    const int copyBufferSize = 128 * 1024;
    const int maxRelativeNameBytes = 4096;
    readonly RelatudeDBServer _server;
    internal UIFileTransfer(RelatudeDBServer server) => _server = server;

    internal void Map(WebApplication app, string path) {
        app.MapPost(path + "upload-part", (HttpContext ctx, Guid ioId, Guid uploadId, long offset) => uploadPartAsync(ctx, ioId, uploadId, offset));
        app.MapPost(path + "upload-commit", (Guid ioId, Guid uploadId, string key, long size) => commit(ioId, uploadId, key, size));
        app.MapPost(path + "upload-abort", (Guid ioId, Guid uploadId) => abort(ioId, uploadId));
        app.MapPost(path + "upload-batch", (HttpContext ctx, Guid ioId, string? basePath) => uploadBatchAsync(ctx, ioId, basePath));
        app.MapPost(path + "download-batch", (HttpContext ctx, DownloadBatchPayload payload) => downloadBatchAsync(ctx, payload));
    }

    /// <summary>
    /// Appends one slice to the upload's temp file. The offset the client believes it is at must be
    /// the size the temp file actually has: a mismatch answers 409 with the size the server holds,
    /// which is what a client resumes from. The answer carries the new size for the same reason.
    /// </summary>
    async Task<IResult> uploadPartAsync(HttpContext ctx, Guid ioId, Guid uploadId, long offset) {
        unlimitBody(ctx);
        var io = _server.GetIO(ioId);
        var temp = UploadTempKey(uploadId);
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
        var temp = UploadTempKey(uploadId);
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
            MoveIntoPlace(io, temp, fileKey);
        } catch (Exception exception) {
            io.DeleteFileIfItExists(temp);
            return Results.BadRequest(new { error = exception.Message }); // a locked destination, say
        }
        return Results.Ok();
    }

    IResult abort(Guid ioId, Guid uploadId) {
        var io = _server.GetIO(ioId);
        io.DeleteFileIfItExists(UploadTempKey(uploadId));
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
            var temp = UploadTempKey(Guid.NewGuid());
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
                    MoveIntoPlace(io, temp, fileKey);
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

    /// <summary>
    /// Streams whole files back in one response, framed as described above. A file that cannot be
    /// read is reported in its own frame and the rest still arrive, so one locked file costs the
    /// download nothing.
    /// <para>The files are opened a few ahead of the one being written, and the small ones read whole
    /// while the ones before them are still going out. Where every open is a round trip - blob
    /// storage, or the network share behind an App Service's home folder - those waits are what a
    /// batch of small files is made of, and this way they overlap instead of adding up one after the
    /// other. The frames are gathered in a buffer rather than written one by one: every write to the
    /// response is a flush, so a batch of two hundred small files used to leave as some four hundred
    /// tiny packets.</para>
    /// </summary>
    async Task<IResult> downloadBatchAsync(HttpContext ctx, DownloadBatchPayload payload) {
        if (payload.Keys.Length == 0) return Results.BadRequest(new { error = "No files to download. " });
        var io = _server.GetIO(payload.IoId);
        var token = ctx.RequestAborted;
        var buffer = new byte[copyBufferSize];
        ctx.Response.ContentType = "application/octet-stream";
        // flushed explicitly at the end and never disposed: disposing it would dispose the response
        // body under it, and synchronously at that, which Kestrel refuses
        var output = new BufferedStream(ctx.Response.Body, batchOutputBufferSize);
        var ahead = new Queue<Task<BatchFile>>();
        var next = 0;
        void readAhead() {
            while (ahead.Count < batchReadAhead && next < payload.Keys.Length) {
                var key = payload.Keys[next++];
                ahead.Enqueue(Task.Run(() => openBatchFileAsync(io, key, token), token));
            }
        }
        try {
            readAhead();
            while (ahead.Count > 0) {
                using var file = await ahead.Dequeue();
                readAhead();
                var reason = file.Failure == null ? [] : Encoding.UTF8.GetBytes(file.Failure);
                await output.WriteAsync(frameHeader(file.Key, file.Failure == null, file.Failure == null ? file.Length : reason.Length), token);
                if (file.Failure != null) {
                    await output.WriteAsync(reason, token);
                    continue;
                }
                if (file.Bytes != null) {
                    await output.WriteAsync(file.Bytes, token);
                    continue;
                }
                var remaining = file.Length;
                while (remaining > 0) {
                    var read = await file.Source!.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                    // the promised length is already on the wire, so a file that shrank under us
                    // can only end the response - padding it would hand over a corrupt file
                    if (read == 0) throw new EndOfStreamException(file.Key + " ended before its last byte. ");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    remaining -= read;
                }
            }
            await output.FlushAsync(token);
        } finally {
            // what was opened ahead of a batch that ended early - cancelled, or broken by a file that
            // shrank - is closed here rather than left to the finalizer
            while (ahead.Count > 0) {
                try {
                    (await ahead.Dequeue()).Dispose();
                } catch {
                    // it failed or was cancelled while opening: there is nothing to close
                }
            }
        }
        return Results.Empty;
    }

    // how many files of a batch are opened ahead of the one being written, and how small a file must
    // be to be read whole while it waits: small enough that the ones waiting cost a couple of MB at most
    const int batchReadAhead = 8;
    const int batchWholeFileBytes = 256 * 1024;
    const int batchOutputBufferSize = 64 * 1024;

    /// <summary>A file of a download batch, opened - and read whole, when it is small - ahead of its turn.</summary>
    sealed class BatchFile(string key) : IDisposable {
        public string Key { get; } = key;
        public Stream? Source { get; set; }
        public byte[]? Bytes { get; set; }
        public long Length { get; set; }
        public string? Failure { get; set; }
        public void Dispose() {
            Source?.Dispose();
            Source = null;
        }
    }

    static async Task<BatchFile> openBatchFileAsync(IIOProvider io, string key, CancellationToken token) {
        var file = new BatchFile(key);
        try {
            // not shared with writers: a copy of a file mid-write is a copy that is wrong,
            // and the single file download this sits beside refuses one for the same reason
            file.Source = UIServer.OpenFileForReading(io, key.SplitKey(), shareWithWriters: false);
            if (file.Source == null) {
                file.Failure = "The file was not found. ";
                return file;
            }
            file.Length = file.Source.Length;
            if (file.Length <= batchWholeFileBytes) {
                var bytes = new byte[file.Length];
                await file.Source.ReadExactlyAsync(bytes, token);
                file.Bytes = bytes;
                file.Dispose();
            }
        } catch (OperationCanceledException) {
            file.Dispose();
            throw;
        } catch (Exception exception) {
            file.Dispose();
            file.Bytes = null;
            // read whole before a byte of it was promised, a file that shrank can still be reported
            // on its own rather than end the response for every file after it
            file.Failure = exception is EndOfStreamException ? "The file changed while it was being read. "
                : exception is IOException ? "The file is in use. "
                : exception.Message;
        }
        return file;
    }

    static byte[] frameHeader(string name, bool ok, long length) {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var header = new byte[4 + nameBytes.Length + 1 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(header, nameBytes.Length);
        nameBytes.CopyTo(header, 4);
        header[4 + nameBytes.Length] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5 + nameBytes.Length), length);
        return header;
    }

    // Providers that cannot move a file (blob storage) copy the bytes instead; everything else
    // renames, which is a metadata operation even for a file of gigabytes. Internal because a move
    // between storages (UIFileMove) stages its copies the same way.
    internal static void MoveIntoPlace(IIOProvider io, string[] temp, string[] fileKey) {
        io.DeleteFileIfItExists(fileKey);
        if (io.CanRenameFile) {
            io.RenameFile(temp, fileKey);
        } else {
            io.CopyFile(temp, fileKey);
            io.DeleteFileIfItExists(temp);
        }
    }

    /// <summary>Where an upload is staged while it is still arriving. Internal because the node file
    /// upload (UIQuery) stages through the same routes and then commits the temp file into a file
    /// property instead of onto a key.</summary>
    internal static string[] UploadTempKey(Guid uploadId) => [FileKeyUtility.UploadFolderName, uploadId.ToString("N") + ".part"];

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

sealed record DownloadBatchPayload(Guid IoId, string[] Keys);
