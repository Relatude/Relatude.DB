namespace Relatude.DB.DataStores;
public class StateFileReadException : Exception {
    public StateFileReadException(string? message, Exception? innerException) : base(message, innerException) {
    }
}
