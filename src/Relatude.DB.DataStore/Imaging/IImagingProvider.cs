using System.Text.Json.Serialization;
using Relatude.DB.FileConversion;

namespace Relatude.DB.Imaging;

/// <summary>
/// Image AI on behalf of the database: creating images, changing them, saying what they show and
/// answering questions about them, so
/// application code does not hold a vendor account of its own. Reached through <c>NodeStore.Services.Imaging</c>.
///
/// <para>One implementation ships: <c>RelatudeServicesImagingProvider</c>, which calls the hosted
/// Relatude Imaging service and charges each call to the license. The interface is here so that an
/// account of your own can be plugged in later without any calling code changing, the way
/// <see cref="Relatude.DB.SMS.ISMSProvider"/> is.</para>
///
/// <para>Images go in as their bytes: PNG, JPEG or WebP. A JPEG whose Exif orientation turns it is
/// taken at the size it is shown, and so is its answer. An image answer is always a PNG
/// (<see cref="ImagingImage"/>). A <b>mask</b> is an image the size of the image it goes with: white,
/// or opaque, where the change is to be made, and black, or transparent, where the image stays as it is.</para>
///
/// <para>The same call made twice is answered from what the service kept, at the price it sets for
/// that. <c>fresh</c> asks for a new answer instead - another take on a description, say - which is
/// made and paid for again.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is configured
/// and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface IImagingProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log.</summary>
    string Name { get; }

    /// <summary>
    /// A new image from a description. <paramref name="inspiration"/> are images whose look the new
    /// one should take after. <paramref name="width"/> and <paramref name="height"/> are both given or
    /// neither; without them the provider picks the size. <paramref name="transparent"/> asks for the
    /// subject on a transparent background.
    /// </summary>
    Task<ImagingImage> CreateImageAsync(string description, IReadOnlyList<byte[]>? inspiration = null, int? width = null, int? height = null,
        bool transparent = false, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The image changed the way <paramref name="instruction"/> says, at its own size. With a
    /// <paramref name="mask"/> only what it covers is changed; <paramref name="references"/> are other
    /// images the change may draw on, which not every provider can combine.
    /// </summary>
    Task<ImagingImage> ManipulateImageAsync(byte[] image, string instruction, IReadOnlyList<byte[]>? references = null, byte[]? mask = null,
        bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The subject of the image on transparency, at the image's size.</summary>
    Task<ImagingImage> RemoveBackgroundAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The image <paramref name="factor"/> times larger each way: 2 or 4.</summary>
    Task<ImagingImage> UpscaleAsync(byte[] image, int factor = 2, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The image without what <paramref name="mask"/> covers, filled in as if it had never been there, at the image's size.</summary>
    Task<ImagingImage> RemoveObjectAsync(byte[] image, byte[] mask, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The image on a larger canvas, <paramref name="margins"/> pixels added on each side and the new
    /// space filled to match. <paramref name="hint"/> says what the new space should show.
    /// </summary>
    Task<ImagingImage> ExpandImageAsync(byte[] image, ImageMargins margins, string? hint = null, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The image on a smaller canvas, <paramref name="margins"/> pixels taken off each side, with the subject kept whole.</summary>
    Task<ImagingImage> ShrinkImageAsync(byte[] image, ImageMargins margins, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The image turned the right way up when it is upside down or on its side: what it shows is looked
    /// at, so a photo taken with the camera turned, or scanned upside down, stands upright again. When
    /// it is upright already, or which way is up cannot be told - a pattern, a view from straight above -
    /// it is left as it is. <see cref="ImagingRotation.Rotation"/> says how far it was turned clockwise:
    /// 0, 90, 180 or 270, and <see cref="ImagingRotation.Image"/> is the image turned, or null when it was
    /// left as it is. A call that leaves it as it is costs the same as one that turns it.
    /// </summary>
    Task<ImagingRotation> RotateIfNeededAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the image shows, for search and for cropping: a title, a description and keywords in
    /// <paramref name="language"/> (a code such as en or nb, or the provider's choice), and where
    /// available the point it is about and the things in it. The text in an image is not read: that
    /// is OCR, which belongs to <see cref="Relatude.DB.FileToText.IFileToTextProvider"/>.
    /// </summary>
    Task<ImagingMeta> ImageToMetaAsync(byte[] image, string? language = null, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The answer to <paramref name="question"/> about the image, in words, in the language the question
    /// is asked in: what is in it, what is going on, what something looks like. When the image does not
    /// show what the question asks about, the answer says so rather than guess. The answer is kept for
    /// this license only, since a question is words someone wrote.
    /// </summary>
    Task<ImagingAnswer> AskAboutImageAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yes (true) or no (false) to <paramref name="question"/> about the image - "Is there a person in
    /// it?", "Is it taken outdoors?" - with how sure the answer is: <see cref="ImagingBoolAnswer.Certainty"/>
    /// from 0, a guess with the image saying nothing either way, to 100, the image plainly showing it.
    /// </summary>
    Task<ImagingBoolAnswer> AskAboutImageBoolAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every operation the service has and whether it can be called there, what a call costs, and how
    /// large a file may be. Costs nothing, and needs no license.
    /// </summary>
    Task<ImagingOperations> GetOperationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An image an operation made, as a PNG. <see cref="Credits"/> is what the call cost and
/// <see cref="CreditsLeft"/> what is left on the license's account. <see cref="Cached"/> says the same
/// call was answered before and this is that answer, given again. <see cref="Sha256"/> names the image
/// at the service, which already holds it for this license, so passing it to a later call costs no upload.
/// </summary>
public sealed record ImagingImage(byte[] Png, int Width, int Height, string Sha256, int Credits, int CreditsLeft, bool Cached);

/// <summary>
/// What <see cref="IImagingProvider.RotateIfNeededAsync"/> did: <see cref="Rotation"/> is how far the
/// image was turned clockwise to stand upright - 0, 90, 180 or 270 - and <see cref="Image"/> the image
/// turned, as a PNG, or null when it was left as it is: upright already, or with no telling which way is
/// up. A quarter turn swaps the width and the height. <see cref="Credits"/>, <see cref="CreditsLeft"/>
/// and <see cref="Cached"/> are as for <see cref="ImagingImage"/>.
/// </summary>
public sealed record ImagingRotation(int Rotation, ImagingImage? Image, int Credits, int CreditsLeft, bool Cached) {
    /// <summary>Whether the image was turned, so <see cref="Image"/> is there.</summary>
    [JsonIgnore]
    public bool Rotated => Image != null;
}

/// <summary>
/// What an image shows. Title, description and keywords are always there, possibly empty. Focus is
/// the point the image is about and Objects what is in it, both in pixels of the image as shown and
/// both optional: not every provider finds them.
/// </summary>
public sealed record ImagingMeta(string Title, string Description, string[] Keywords, string? Language,
    ImagingFocusPoint? Focus, ImagingObject[] Objects, int Credits, int CreditsLeft, bool Cached);

/// <summary>A point in pixels, from the top left corner of the image as shown.</summary>
public sealed record ImagingFocusPoint(int X, int Y);

/// <summary>
/// The answer to a question about an image (<see cref="IImagingProvider.AskAboutImageAsync"/>), in words,
/// in the language the question was asked in. <see cref="Credits"/>, <see cref="CreditsLeft"/> and
/// <see cref="Cached"/> are as for <see cref="ImagingImage"/>.
/// </summary>
public sealed record ImagingAnswer(string Answer, int Credits, int CreditsLeft, bool Cached);

/// <summary>
/// The answer to a yes-or-no question about an image (<see cref="IImagingProvider.AskAboutImageBoolAsync"/>):
/// <see cref="Answer"/> true for yes, and <see cref="Certainty"/> how sure that is, from 0 - a guess, the
/// image saying nothing either way - to 100, the image plainly showing it. A "no" at 20 says little; a
/// "no" at 95 says the image shows it is not so.
/// </summary>
public sealed record ImagingBoolAnswer(bool Answer, int Certainty, int Credits, int CreditsLeft, bool Cached);

/// <summary>
/// Something in an image and the box around it, in pixels of the image as shown. <see cref="Type"/>
/// is one of face, person, text, animal, vehicle, furniture, food, building and other;
/// <see cref="Name"/> says what it is in words, when the provider says; <see cref="Confidence"/> is
/// between 0 and 1.
/// </summary>
public sealed record ImagingObject(string Type, string? Name, double Confidence, int X, int Y, int Width, int Height) {
    /// <summary><see cref="Type"/> as the database's own object type, <see cref="ImageObjectType.Other"/> for one it does not know.</summary>
    [JsonIgnore]
    public ImageObjectType ObjectType => Enum.TryParse<ImageObjectType>(Type, true, out var type) && Enum.IsDefined(type) ? type : ImageObjectType.Other;
}

/// <summary>Pixels to add to, or take from, each side of an image. At least one must be more than 0.</summary>
public sealed record ImageMargins(int Top = 0, int Right = 0, int Bottom = 0, int Left = 0) {
    /// <summary>The same number of pixels on every side.</summary>
    public static ImageMargins All(int pixels) => new(pixels, pixels, pixels, pixels);
}

/// <summary>
/// Every operation the service has, and whether a provider does it there; what an operation costs,
/// and what it costs when the answer was made before; and how files reach the service: up to
/// <see cref="PartBytes"/> in one request, larger in parts of that size, at most <see cref="MaxFileBytes"/>.
/// </summary>
public sealed record ImagingOperations(ImagingOperationInfo[] Operations, int CreditsPerOperation, int CreditsPerCachedOperation, int PartBytes, long MaxFileBytes) {
    /// <summary>Whether the operation with this key - create-image, upscale, rotate-if-needed, image-to-meta, ... - can be called.</summary>
    public bool IsAvailable(string key) => Operations.Any(o => o.Available && string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One operation: its key (create-image, manipulate-image, remove-background, upscale, remove-object, expand-image, shrink-image, rotate-if-needed, image-to-meta, ask-about-image, ask-about-image-bool), a name for people, and whether it can be called.</summary>
public sealed record ImagingOperationInfo(string Key, string Name, bool Available);

/// <summary>
/// How the database reaches an image AI service. The shape follows
/// <see cref="Relatude.DB.SMS.SMSProviderSettings"/>: a type name resolved when the database opens, an
/// endpoint, and the key that pays for the calls. The hosted Relatude service needs neither of the
/// last two on a server, which calls it with the installation's own license.
/// </summary>
public class ImagingProviderSettings {
    /// <summary>Which implementation is used. Empty or "RelatudeServices" is the hosted Relatude Imaging service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Empty uses the provider's own default; set it for a self-hosted or test deployment.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service charges each call to the
    /// installation's license and uses the license's API key, falling back to this one only where the
    /// server has none - or where the provider is built from code, without a server.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the answers are kept on this machine, so the same call is not made, nor paid for, twice:
    /// Native (the default) in a file beside the AI embedding cache, Memory while the process runs, None never.
    /// Kept: what an image shows, answers to questions about it, and that it stands upright; never an image.</summary>
    public Relatude.DB.Common.ServiceCacheType CacheType { get; set; } = Relatude.DB.Common.ServiceCacheType.Native;
}
