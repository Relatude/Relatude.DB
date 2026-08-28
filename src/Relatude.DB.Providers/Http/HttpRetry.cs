namespace Relatude.DB.Http;
/// <summary>
/// Shared retry policy for the plain-HTTP providers in this plugin.
/// Retries transient failures (429, 408, 5xx and connection errors) with exponential backoff,
/// honoring a Retry-After header when the server sends one.
/// <para>This is the one retry in the database that does not use <c>Relatude.DB.Common.Retry</c>, and
/// the difference is deliberate. That helper waits on a failed operation - an exception - whereas most
/// retries here are triggered by a perfectly successful response carrying a 429 or a 5xx, and the last
/// such response has to be returned rather than thrown. The cadence has to differ too: a rate-limited
/// API dictates the wait through Retry-After, and needs jitter so that many clients backing off at
/// once do not return in lockstep - neither of which applies to waiting for a local file lock.</para>
/// </summary>
internal static class HttpRetry {
    const int _maxAttempts = 5;
    static readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(30);
    public static bool IsTransient(int statusCode) => statusCode is 408 or 429 or 500 or 502 or 503 or 504;
    // requests must be recreated per attempt (HttpRequestMessage is single use), hence the factory.
    // retryOnTimeout should be false for non-idempotent requests where a timed-out attempt may still
    // have been applied by the server (e.g. blob append blocks without a position guard).
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> createRequest, bool retryOnTimeout = true) {
        Exception? lastError = null;
        HttpResponseMessage? lastResponse = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++) {
            TimeSpan? retryAfter = null;
            try {
                var response = await client.SendAsync(createRequest(), HttpCompletionOption.ResponseHeadersRead);
                if (!IsTransient((int)response.StatusCode)) return response;
                if (attempt == _maxAttempts) return response;
                retryAfter = getRetryAfter(response);
                lastResponse?.Dispose();
                lastResponse = response;
            } catch (HttpRequestException ex) { // connection level failure, nothing reached the server
                lastError = ex;
            } catch (TaskCanceledException ex) when (retryOnTimeout) { // client side timeout
                lastError = ex;
            }
            if (attempt == _maxAttempts) break;
            await Task.Delay(getDelay(attempt, retryAfter));
        }
        if (lastResponse != null) return lastResponse;
        throw lastError!;
    }
    public static HttpResponseMessage Send(HttpClient client, Func<HttpRequestMessage> createRequest, bool retryOnTimeout = true) {
        Exception? lastError = null;
        HttpResponseMessage? lastResponse = null;
        for (var attempt = 1; attempt <= _maxAttempts; attempt++) {
            TimeSpan? retryAfter = null;
            try {
                var response = client.Send(createRequest(), HttpCompletionOption.ResponseHeadersRead);
                if (!IsTransient((int)response.StatusCode)) return response;
                if (attempt == _maxAttempts) return response;
                retryAfter = getRetryAfter(response);
                lastResponse?.Dispose();
                lastResponse = response;
            } catch (HttpRequestException ex) {
                lastError = ex;
            } catch (TaskCanceledException ex) when (retryOnTimeout) {
                lastError = ex;
            }
            if (attempt == _maxAttempts) break;
            Thread.Sleep(getDelay(attempt, retryAfter));
        }
        if (lastResponse != null) return lastResponse;
        throw lastError!;
    }
    static TimeSpan getDelay(int attempt, TimeSpan? retryAfter) {
        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero) {
            return retryAfter.Value < _maxDelay ? retryAfter.Value : _maxDelay;
        }
        var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        var delay = backoff + jitter;
        return delay < _maxDelay ? delay : _maxDelay;
    }
    static TimeSpan? getRetryAfter(HttpResponseMessage response) {
        var header = response.Headers.RetryAfter;
        if (header == null) return null;
        if (header.Delta.HasValue) return header.Delta;
        if (header.Date.HasValue) return header.Date.Value - DateTimeOffset.UtcNow;
        return null;
    }
}
