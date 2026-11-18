using Relatude.DB.Common;

namespace Relatude.DB.DataStores;

/// <summary>A file for <see cref="IDataStore.FileUploadManyAsync"/>: the file property it goes into, its bytes and its name.</summary>
public sealed record FileUpload(PropertyPath Target, Stream Source, string FileName);

/// <summary>What <see cref="IDataStore.FileUploadManyAsync"/> made of one file: its value as stored, or why it was not.</summary>
public sealed record FileUploadResult(FileValue? Value, Exception? Error) {
    public bool Stored => Value != null;
}
