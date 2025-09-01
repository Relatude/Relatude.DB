using Relatude.DB.Common;
using System.Text.Json;

namespace Relatude.DB.Connection {
    public class RemoteServerException : Exception {
        static readonly long fixedErrorMessageLength = 102847L;
        static readonly string errorFlagValue = "6d19ae8e-4e08-47fd-86e3-40c1f2f6e055";
        public RemoteServerException(string? message, string? stackTrace) : base(message) {
            RemoteStackTrace = stackTrace;
        }
        public static void ThrowExceptionIfContentIsServerError(Stream stream) {
            if (stream.Length == fixedErrorMessageLength) { // first indicator, fast check
                var asString = stream.ReadString();
                stream.Position = 0; // resetting position
                if (asString.Length > errorFlagValue.Length && asString.StartsWith(errorFlagValue)) { // second indicator, slower check
                    var json = asString.Substring(errorFlagValue.Length);
                    var errDetails = JsonSerializer.Deserialize<RemoteServerErrorDetails>(json);
                    if (errDetails != null) {
                        throw new RemoteServerException(errDetails.Message, errDetails.Stacktrace);
                    } else {
                        throw new RemoteServerException("Unknown remote server error. ", string.Empty);
                    }
                }
            }
        }
        public string? RemoteStackTrace { get; }
        public static MemoryStream CreateErrorResponseStream(Exception ex) {
            var json = JsonSerializer.Serialize(new RemoteServerErrorDetails { Message = ex.Message, Stacktrace = ex.StackTrace });
            var errorStream = new MemoryStream(new byte[fixedErrorMessageLength]); // fixing length 
            if (json.Length > fixedErrorMessageLength / 4) { // just in case error message exceeds fixed length of error data
                json = JsonSerializer.Serialize(new RemoteServerErrorDetails { Message = "Server error", Stacktrace = "Too much details" });
            }
            errorStream.WriteString(errorFlagValue + json);
            errorStream.Position = 0;
            return errorStream;
        }
        public static string CreateErrorResponseJson(Exception ex) {
            var json = JsonSerializer.Serialize(new RemoteServerErrorDetails { Message = ex.Message, Stacktrace = ex.StackTrace });
            if (json.Length > fixedErrorMessageLength / 4) { // just in case error message exceeds fixed length of error data
                json = JsonSerializer.Serialize(new RemoteServerErrorDetails { Message = "Server error", Stacktrace = "Too much details" });
            }
            return json;
        }
    }
}