namespace Relatude.DB.IO;
public interface IStream : IDisposable {
    string FileKey { get; }
    long Length { get; }
}
