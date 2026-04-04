namespace Relatude.DB.Common;

public static class ExceptionExt {
    static public bool CausedByOutOfMemory(this Exception ex) {
        if (ex is OutOfMemoryException) return true;
        if (ex.InnerException != null) return ex.InnerException.CausedByOutOfMemory();
        return false;
    }
}
