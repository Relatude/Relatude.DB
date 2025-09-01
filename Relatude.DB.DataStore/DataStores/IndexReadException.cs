namespace Relatude.DB.DataStores;
public class IndexReadException : Exception {
    public IndexReadException(string? message, Exception? innerException) : base(message, innerException) {
    }
}
