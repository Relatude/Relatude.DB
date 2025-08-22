using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WAF.Connection {
    public class MultiThreadedSocketConnection : IMultiThreadedConnection {
        readonly ConnectionPool _pool;
        public MultiThreadedSocketConnection(RemoteConfiguration config) {
            _pool = new ConnectionPool(() => new SingleSocketConnection(config), config);
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
