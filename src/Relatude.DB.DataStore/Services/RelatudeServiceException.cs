namespace Relatude.DB.Common;

/// <summary>
/// A Relatude service - Imaging or FileToText - said no, or could not be reached for an answer. The
/// message repeats the service's own reason, which is written to be shown to whoever configured the
/// installation, so it can be logged or shown as it is.
/// <para><see cref="StatusCode"/> is the service's answer, or 0 when there was none: a timeout or a
/// dropped connection. What it means is the same for every Relatude service: 400 the call is
/// malformed, 401 no usable API key, 402 out of credits or no credit account for the service, 403 the
/// key or the license may not be used, 413 too large, 415 a kind of file nothing there reads, 422 the
/// input was turned down, 424 the charge was not confirmed, 429 too fast, 501 not offered there, 502
/// every provider behind the service failed, 503 the license server out of reach.</para>
/// </summary>
public class RelatudeServiceException(int statusCode, string message, string? reason = null, Exception? inner = null) : Exception(message, inner) {
    /// <summary>The HTTP status the service answered with, or 0 when it gave no answer at all.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>The service's own words, without the address and the status around them. Null when it gave none.</summary>
    public string? Reason { get; } = reason;

    /// <summary>
    /// Whether the call may have cost credits. False only for the answers a Relatude service gives
    /// before it charges anything - a malformed call, a refused key or license, missing credits, a
    /// file it does not have or cannot read, a rate limit, an operation not offered, the license
    /// server out of reach - so a call that failed that way can be made again without paying twice.
    /// True for everything else, a timeout and a dropped connection included: the service may have
    /// taken the credits before it failed, and they are never given back.
    /// </summary>
    public bool MayHaveBeenCharged => StatusCode is not (400 or 401 or 402 or 403 or 404 or 409 or 413 or 415 or 429 or 501 or 503);
}
