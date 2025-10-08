namespace Relatude.DB.Common;
public static class Validator {
    public static T ThrowIfNull<T>(T? obj, string? message = null) {
        if (obj == null) {
            if (message == null) message = $"The value of {typeof(T).Name} cannot be null. ";
            throw new ArgumentNullException(message);
        }
        return obj;
    }
}
