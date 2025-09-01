using System.Diagnostics;

namespace Relatude.DB.Connection {
    // making sure released connections are not used, in case external class hold on to them
    internal class ReservedConnection {
        internal ISingleThreadedConnection _innerConnection;
        internal bool AllowUse;
        internal bool Executing;
        object _lock = new();
        public ReservedConnection(ISingleThreadedConnection innerConnection) {
            _innerConnection = innerConnection;
            AllowUse = true;
        }
        public Task<Stream> SendAndReceiveBinary(Stream input) {
            try {
                lock (_lock) {
                    if (Executing) throw new Exception("Internal error. ReservedConnetion can only be executing one call at a time. ");
                    Executing = true;
                }
                if (!AllowUse) throw new Exception("Internal error. Connection is idle and should not be used. ");
                return _innerConnection.SendAndReceiveBinary(input);
            } finally {
                lock (_lock) Executing = false;
            }
        }
        //public Task<string?> SendAndReceiveJson(string method, string? input) {
        //    try {
        //        lock (_lock) {
        //            if (Executing) throw new Exception("Internal error. ReservedConnetion can only be executing one call at a time. ");
        //            Executing = true;
        //        }
        //        if (!AllowUse) throw new Exception("Internal error. Connection is idle and should not be used. ");
        //        return _innerConnection.SendAndReceiveJson(method, input);
        //    } finally {
        //        lock (_lock) Executing = false;
        //    }
        //}
    }
}
