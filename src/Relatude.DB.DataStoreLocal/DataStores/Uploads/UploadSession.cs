using Relatude.DB.Common;
using System.Security.Cryptography;

namespace Relatude.DB.DataStores.Uploads;

internal class UploadSession(FileValue fileValue) {
    public DateTime LastAccessed = DateTime.MinValue;
    public void Touch() => LastAccessed = DateTime.UtcNow;
    public FileValue FileValue { get; set; } = fileValue;
    public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
}
