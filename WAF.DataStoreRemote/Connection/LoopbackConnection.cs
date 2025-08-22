using WAF.DataStores;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WAF.Connection {
    /// <summary>
    /// Loopback connection for testing serilization
    /// </summary>
    internal class LoopbackConnection : IMultiThreadedConnection {
        IDataStore _db;
        public LoopbackConnection(IDataStore? db) {
            if (db == null) throw new ArgumentNullException(nameof(db));
            _db = db;
        }
        public async Task<Stream> SendAndReceiveBinary(Stream input) {
            return await _db.BinaryCallAsync(input);
        }
        public void Dispose() {
            _db.Dispose();
        }

    }
}
