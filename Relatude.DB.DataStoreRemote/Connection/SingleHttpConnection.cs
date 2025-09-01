using Relatude.DB.Common;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace Relatude.DB.Connection {
    public class SingleHttpConnection : ISingleThreadedConnection, IMultiThreadedConnection { // Http is StateLessConnection and thread safe
        public static readonly bool ValidateChecksums = false; // affects performance with 3-7% 
        public static readonly bool ValidateLengths = true;
        readonly HttpClient _client;
        readonly string? _endPointAddress;
        public DateTime LastUseUtc { get; private set; } = DateTime.UtcNow;
        public bool FlaggedAsStalled { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        readonly Stopwatch _sw = new();
        public SingleHttpConnection(RemoteConfiguration config) {
            _endPointAddress = "https://";
            if (string.IsNullOrWhiteSpace(config.DNS)) {
                _endPointAddress += "localhost";
            } else {
                if (config.DNS.IndexOfAny(":/\\@, *".ToCharArray()) > -1) {
                    throw new ArgumentException("Invalid DNS. ");
                }
                _endPointAddress += config.DNS;
            }
            if (config.Port != 0) {
                _endPointAddress += ":" + config.Port;
            }
            if (!string.IsNullOrWhiteSpace(config.Path)) {
                if (_endPointAddress.EndsWith("/")) _endPointAddress = _endPointAddress[1..];
                if (!config.Path.StartsWith("/")) config.Path = "/" + config.Path;
                _endPointAddress += config.Path;
            }
            if (!_endPointAddress.EndsWith("/")) _endPointAddress += "/";
            _client = new HttpClient();
        }
        public async Task<Stream> SendAndReceiveBinary(Stream input) {
            _sw.Restart();
            LastUseUtc = DateTime.UtcNow;
            try {
                return await callHttpBinary(input);
            } catch (Exception ex) {
                if (FlaggedAsStalled) throw new Exception("Operation timed out and connection was closed. ", ex);
                throw;
            } finally {
                LastUseUtc = DateTime.UtcNow;
                _sw.Reset();
            }
        }
        //public async Task<string?> SendAndReceiveJson(string method, string? input) {
        //    _sw.Restart();
        //    LastUseUtc = DateTime.UtcNow;
        //    try {
        //        return await callHttpJson(method, input);
        //    } catch (Exception ex) {
        //        if (FlaggedAsStalled) throw new Exception("Operation timed out and connection was closed. ", ex);
        //        throw;
        //    } finally {
        //        LastUseUtc = DateTime.UtcNow;
        //        _sw.Reset();
        //    }
        //}
        async Task<Stream> callHttpBinary(Stream input) {
            input.Position = 0;
            using var requestData = new StreamContent(input);
            if (ValidateChecksums) {
                uint checkSum;
                if (input is MemoryStream ms) {
                    checkSum = ms.ToArray().GetChecksum();
                } else {
                    throw new NotSupportedException("Checksum validation is only possible with memory streams");
                }
                requestData.Headers.Add(HeaderNames.Header_Checksum, checkSum.ToString());
            }
            if (ValidateLengths) requestData.Headers.Add(HeaderNames.Header_Length, input.Length.ToString());
            using var response = await _client.PostAsync(_endPointAddress + "binary/", requestData);
            if (!response.IsSuccessStatusCode) throw new Exception((int)response.StatusCode + " HTTP error response. ");
            using var stream = await response.Content.ReadAsStreamAsync();
            var responseData = new MemoryStream();
            await stream.CopyToAsync(responseData);
            if (ValidateChecksums) {
                if (!response.Headers.Contains(HeaderNames.Header_Checksum)) throw new Exception("Missing checksum in header. ");
                var checksumS = response.Headers.GetValues(HeaderNames.Header_Checksum).FirstOrDefault();
                if (!int.TryParse(checksumS, out var checksum)) throw new Exception("Invalid checksum. ");
                if (checksum != responseData.ToArray().GetChecksum()) throw new Exception("Data does not validate against header checksum. ");
            }
            if (ValidateLengths) {
                if (!response.Headers.Contains(HeaderNames.Header_Length)) throw new Exception("Missing length value in header. ");
                var lengthS = response.Headers.GetValues(HeaderNames.Header_Length).FirstOrDefault();
                if (!long.TryParse(lengthS, out var length)) throw new Exception("Invalid length value in header. ");
                if (length != responseData.Length) throw new Exception("Data length does not validate against header length value. ");
            }
            responseData.Position = 0;
            return responseData;
        }
        //async Task<string?> callHttpJson(string method, string? input) {
        //    using var requestData = new StringContent(input, WAFGlobals.Encoding, "application/json");
        //    using var response = await _client.PostAsync(_endPointAddress + method.ToLower() + "/", requestData);
        //    if (!response.IsSuccessStatusCode) throw new Exception((int)response.StatusCode + " HTTP error response. ");
        //    return await response.Content.ReadAsStringAsync();
        //}
        public double CurrentCallDurationInMs() => _sw.Elapsed.TotalMilliseconds;
        public void Dispose() {
            _client.Dispose();
        }
    }
}
