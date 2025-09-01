namespace Relatude.DB.Connection {
    public class MultiThreadedHttpConnection : IMultiThreadedConnection {
        readonly ConnectionPool _pool;
        public MultiThreadedHttpConnection(RemoteConfiguration config) {
            _pool = new ConnectionPool(() => new SingleHttpConnection(config), config);
        }
        public async Task<Stream> SendAndReceiveBinary(Stream input) {
            var cn = _pool.ReserveConnection();
            try {
                var output = await cn.SendAndReceiveBinary(input);
                return output;
            } finally {
                _pool.ReleaseConnection(cn);
            }
        }
        public void Dispose() {
            _pool.Dispose();
        }

        //public async Task<string?> SendAndReceiveJson(string method, string? input) {
        //    var cn = _pool.ReserveConnection();
        //    try {
        //        var output = await cn.SendAndReceiveJson(method, input);
        //        return output;
        //    } finally {
        //        _pool.ReleaseConnection(cn);
        //    }
        //}
    }
}
