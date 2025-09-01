namespace Relatude.DB.DataStores.Stores;
public class LogReadException : Exception {
    public LogReadException(string? message, Exception? innerException) : base(message, innerException) { }
}
