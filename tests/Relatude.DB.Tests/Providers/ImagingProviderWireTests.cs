using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.Imaging;

namespace Relatude.Providers;

/// <summary>
/// The Imaging provider against the requests it puts on the wire. It talks to the hosted Relatude
/// Imaging service, whose contract is its own: images sent once to files/{sha256} and named by their
/// SHA-256 in a JSON call from then on, the license API key as the bearer token, and an image answer
/// coming back as the PNG with what it cost in headers. The stub holds to the service's file protocol
/// (<see cref="RelatudeServiceStub"/>), so a mistake in sending or naming a file fails here.
/// </summary>
[TestClass]
public class ImagingProviderWireTests {
    const string _key = "cd4e092c-ac57-4661-86c8-9b3f4acf6438";

    static (string, string)[] priced(byte[] png, int credits = 1, int left = 99, bool cached = false) => [
        ("X-Credits", credits.ToString()), ("X-Credits-Left", left.ToString()),
        ("X-Sha256", RelatudeServiceStub.Sha256(png)), ("X-Cache", cached ? "hit" : "miss"),
    ];

    static RelatudeServicesImagingProvider provider(RelatudeServiceStub stub, string? key = _key) =>
        new(new ImagingProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = key });

    [TestMethod]
    public async Task AnImageIsSentOnceAndNamedByItsSha256AndTheAnswerComesBackWithItsPrice() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(640, 480, seed: 1);
        var cutout = RelatudeServiceStub.FakePng(640, 480, seed: 2);
        stub.EnqueueImage(cutout, priced(cutout, credits: 1, left: 41));

        var answer = await imaging.RemoveBackgroundAsync(photo);

        CollectionAssert.AreEqual(cutout, answer.Png);
        Assert.AreEqual(640, answer.Width, "the size is read from the PNG's own header");
        Assert.AreEqual(480, answer.Height);
        Assert.AreEqual(RelatudeServiceStub.Sha256(cutout), answer.Sha256);
        Assert.AreEqual(1, answer.Credits);
        Assert.AreEqual(41, answer.CreditsLeft);
        Assert.IsFalse(answer.Cached);

        var sha = RelatudeServiceStub.Sha256(photo);
        var put = stub.Calls("PUT", "/files/").Single();
        Assert.AreEqual("/api/imaging/files/" + sha, put.Path, "the file goes to the address of its own SHA-256, in lowercase");
        CollectionAssert.AreEqual(photo, put.Body);
        Assert.AreEqual("Bearer " + _key, put.Header("Authorization"));
        Assert.AreEqual("100-continue", put.Header("Expect"), "so a file the service has already is answered before the body goes");

        var call = stub.Operations().Single();
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("/api/imaging/remove-background", call.Path);
        Assert.AreEqual("Bearer " + _key, call.Header("Authorization"));
        Assert.AreEqual(sha, call.Json.GetProperty("image").GetString());
        Assert.IsNull(call.Header("Cache-Control"), "the answer kept for the same call is good enough unless asked otherwise");

        // the same image again is named, not sent
        stub.EnqueueImage(cutout, priced(cutout, cached: true));
        var again = await imaging.RemoveBackgroundAsync(photo);
        Assert.IsTrue(again.Cached);
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "an image the service holds is never sent twice");
        Assert.AreEqual(2, stub.Operations().Length);
    }

    /// <summary>
    /// The service holds every answer it gives for the license that asked, so an answer passed on to
    /// the next operation - a cutout to be upscaled - goes by its name alone.
    /// </summary>
    [TestMethod]
    public async Task AnAnswerPassedOnToTheNextOperationIsNotSentBack() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(100, 80, seed: 1);
        var cutout = RelatudeServiceStub.FakePng(100, 80, seed: 2);
        var large = RelatudeServiceStub.FakePng(400, 320, seed: 3);
        stub.EnqueueImage(cutout, priced(cutout));
        stub.EnqueueImage(large, priced(large));
        // what the real service does when it gives an answer: it keeps it, and the caller may name it
        stub.Files[RelatudeServiceStub.Sha256(cutout)] = cutout;

        var first = await imaging.RemoveBackgroundAsync(photo);
        var second = await imaging.UpscaleAsync(first.Png, 4);

        Assert.AreEqual(400, second.Width);
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "only the photo was sent");
        var upscale = stub.Operations().Last();
        Assert.AreEqual(first.Sha256, upscale.Json.GetProperty("image").GetString());
        Assert.AreEqual(4, upscale.Json.GetProperty("factor").GetInt32());
    }

    /// <summary>
    /// The service lets go of files nobody used for a while, so one this client knows it sent may be
    /// gone. The call is answered 409 with the file named - nothing is charged for that - and the file
    /// is sent again and the call made again.
    /// </summary>
    [TestMethod]
    public async Task AnImageTheServiceLetGoIsSentAgainAndTheCallMadeAgain() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(64, 64, seed: 1);
        var answer = RelatudeServiceStub.FakePng(64, 64, seed: 2);
        stub.EnqueueImage(answer, priced(answer));
        await imaging.RemoveBackgroundAsync(photo);

        stub.Files.Clear();
        stub.EnqueueImage(answer, priced(answer));
        var result = await imaging.RemoveBackgroundAsync(photo);

        CollectionAssert.AreEqual(answer, result.Png);
        Assert.AreEqual(2, stub.Calls("PUT", "/files/").Length, "sent again once the service said it no longer had it");
        Assert.AreEqual(3, stub.Operations().Length, "the call, the call answered 409, and the call made again");
    }

    /// <summary>
    /// A file larger than one request may carry goes in parts: the upload started, every part sent,
    /// and the upload completed, which has the service put the file together and check it. A part the
    /// service did not get is sent again, and the size the service gave is used from then on.
    /// </summary>
    [TestMethod]
    public async Task AFileLargerThanOneRequestGoesInParts() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        stub.PartBytes = 1000;
        stub.LosePartOnce = 1;
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(300, 200, seed: 1, extra: 3200); // four parts of 1000
        var answer = RelatudeServiceStub.FakePng(300, 200, seed: 2);
        stub.EnqueueImage(answer, priced(answer));

        await imaging.RemoveBackgroundAsync(photo);

        var sha = RelatudeServiceStub.Sha256(photo);
        CollectionAssert.AreEqual(photo, stub.Files[sha], "the parts put together are the file");
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "the whole file was tried first, and refused for its size");
        Assert.AreEqual(1, stub.Calls("POST", "/uploads").Count(r => r.Path.EndsWith("/uploads")));
        Assert.AreEqual(5, stub.Calls("PUT", "/uploads/").Length, "four parts, and the one the service missed sent again");
        Assert.AreEqual(2, stub.Calls("POST", "/uploads/").Count(r => r.Path.EndsWith("/complete")));

        // the part size is known now, so the next large file goes straight to parts
        var other = RelatudeServiceStub.FakePng(300, 200, seed: 9, extra: 3200);
        stub.EnqueueImage(answer, priced(answer));
        await imaging.RemoveBackgroundAsync(other);
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "no whole-file attempt once the part size is known");
        CollectionAssert.AreEqual(other, stub.Files[RelatudeServiceStub.Sha256(other)]);
    }

    [TestMethod]
    public async Task EveryOperationPutsItsFieldsOnTheWireAsTheServiceNamesThem() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var image = RelatudeServiceStub.FakePng(200, 100, seed: 1);
        var mask = RelatudeServiceStub.FakePng(200, 100, seed: 2);
        var reference = RelatudeServiceStub.FakePng(50, 50, seed: 3);
        var answer = RelatudeServiceStub.FakePng(200, 100, seed: 4);
        for (var i = 0; i < 6; i++) stub.EnqueueImage(answer, priced(answer));
        string sha(byte[] bytes) => RelatudeServiceStub.Sha256(bytes);

        await imaging.CreateImageAsync("A lighthouse at dusk", [reference], 1024, 768, transparent: true, fresh: true);
        var create = stub.Operations()[^1];
        Assert.AreEqual("/api/imaging/create-image", create.Path);
        Assert.AreEqual("A lighthouse at dusk", create.Json.GetProperty("description").GetString());
        Assert.AreEqual(sha(reference), create.Json.GetProperty("inspiration")[0].GetString());
        Assert.AreEqual(1024, create.Json.GetProperty("width").GetInt32());
        Assert.AreEqual(768, create.Json.GetProperty("height").GetInt32());
        Assert.IsTrue(create.Json.GetProperty("transparent").GetBoolean());
        Assert.AreEqual("no-cache", create.Header("Cache-Control"), "fresh asks for a new answer rather than the one kept");

        await imaging.CreateImageAsync("A lighthouse");
        var plain = stub.Operations()[^1].Json;
        foreach (var name in new[] { "inspiration", "width", "height", "transparent" }) {
            Assert.IsFalse(plain.TryGetProperty(name, out _), name + " is left out when not given: the service refuses a field it does not take, never a missing optional one");
        }

        await imaging.ManipulateImageAsync(image, "Make it winter", [reference], mask);
        var manipulate = stub.Operations()[^1].Json;
        Assert.AreEqual("Make it winter", manipulate.GetProperty("instruction").GetString());
        Assert.AreEqual(sha(image), manipulate.GetProperty("image").GetString());
        Assert.AreEqual(sha(reference), manipulate.GetProperty("references")[0].GetString());
        Assert.AreEqual(sha(mask), manipulate.GetProperty("mask").GetString());

        await imaging.RemoveObjectAsync(image, mask);
        var remove = stub.Operations()[^1];
        Assert.AreEqual("/api/imaging/remove-object", remove.Path);
        Assert.AreEqual(sha(mask), remove.Json.GetProperty("mask").GetString());

        await imaging.ExpandImageAsync(image, new ImageMargins(Top: 100, Left: 50), "more sky");
        var expand = stub.Operations()[^1].Json;
        Assert.AreEqual(100, expand.GetProperty("top").GetInt32());
        Assert.AreEqual(50, expand.GetProperty("left").GetInt32());
        Assert.IsFalse(expand.TryGetProperty("right", out _), "a side that does not move is left out");
        Assert.AreEqual("more sky", expand.GetProperty("hint").GetString());

        await imaging.ShrinkImageAsync(image, ImageMargins.All(10));
        var shrink = stub.Operations()[^1];
        Assert.AreEqual("/api/imaging/shrink-image", shrink.Path);
        foreach (var side in new[] { "top", "right", "bottom", "left" }) Assert.AreEqual(10, shrink.Json.GetProperty(side).GetInt32());

        Assert.AreEqual(3, stub.Calls("PUT", "/files/").Length, "the image, the reference and the mask, each sent once however often they were named");
    }

    [TestMethod]
    public async Task ImageToMetaReadsWhatTheImageShows() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var image = RelatudeServiceStub.FakePng(800, 600, seed: 1);
        stub.EnqueueJson(200, """
            {"title":"A red bicycle","description":"A red bicycle against a brick wall.","keywords":["bicycle","red","wall"],"language":"en",
             "focus":{"x":400,"y":310},"objects":[{"type":"vehicle","name":"bicycle","confidence":0.93,"x":120,"y":200,"width":520,"height":300},
             {"type":"spaceship","name":null,"confidence":0.2,"x":0,"y":0,"width":10,"height":10}],"credits":1,"creditsLeft":12}
            """, ("X-Cache", "hit"));

        var meta = await imaging.ImageToMetaAsync(image, "en");

        Assert.AreEqual("A red bicycle", meta.Title);
        CollectionAssert.AreEqual(new[] { "bicycle", "red", "wall" }, meta.Keywords);
        Assert.AreEqual(new ImagingFocusPoint(400, 310), meta.Focus);
        Assert.AreEqual(2, meta.Objects.Length);
        Assert.AreEqual(ImageObjectType.Vehicle, meta.Objects[0].ObjectType);
        Assert.AreEqual(ImageObjectType.Other, meta.Objects[1].ObjectType, "a type the database does not know is Other");
        Assert.AreEqual(520, meta.Objects[0].Width);
        Assert.AreEqual(12, meta.CreditsLeft);
        Assert.IsTrue(meta.Cached);
        var call = stub.Operations().Single();
        Assert.AreEqual("/api/imaging/image-to-meta", call.Path);
        Assert.AreEqual("en", call.Json.GetProperty("language").GetString());

        // a provider that finds no focus and no objects leaves them out
        stub.EnqueueJson(200, """{"title":"","description":"Something.","keywords":[],"credits":1,"creditsLeft":11}""");
        var bare = await imaging.ImageToMetaAsync(image);
        Assert.IsNull(bare.Focus);
        Assert.AreEqual(0, bare.Objects.Length);
        Assert.IsFalse(stub.Operations()[^1].Json.TryGetProperty("language", out _));
    }

    /// <summary>
    /// A call is paid for before the service hands it to a provider, and the credits are never given
    /// back. So it is repeated only on 429 and 503, the two answers the service gives before any money
    /// moves; a 502 is thrown at once, saying it may have been charged.
    /// </summary>
    [TestMethod]
    public async Task ACallIsRepeatedOnlyOnTheAnswersGivenBeforeAnythingIsCharged() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var image = RelatudeServiceStub.FakePng(64, 64, seed: 1);
        var answer = RelatudeServiceStub.FakePng(64, 64, seed: 2);

        stub.EnqueueJson(503, """{"error":"The license server cannot be reached."}""", ("Retry-After", "0"));
        stub.EnqueueJson(429, """{"error":"Too fast."}""", ("Retry-After", "0"));
        stub.EnqueueImage(answer, priced(answer));
        await imaging.RemoveBackgroundAsync(image);
        Assert.AreEqual(3, stub.Operations().Length, "503 and 429 are given before the charge, so the call is made again");

        foreach (var status in new[] { 500, 502, 504 }) {
            stub.Requests.Clear();
            stub.EnqueueJson(status, """{"error":"Every provider failed."}""");
            var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => imaging.RemoveBackgroundAsync(image));
            Assert.AreEqual(status, error.StatusCode);
            Assert.IsTrue(error.MayHaveBeenCharged, status + " can come after the charge");
            Assert.AreEqual(1, stub.Operations().Length, status + " must not be repeated: it may already have been paid for");
        }
    }

    [TestMethod]
    public async Task ARefusalRepeatsTheServicesReason() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        stub.EnqueueJson(402, """{"error":"This license has no 'ai_image' credit account."}""");

        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => imaging.RemoveBackgroundAsync(RelatudeServiceStub.FakePng(8, 8)));

        Assert.AreEqual(402, error.StatusCode);
        Assert.AreEqual("This license has no 'ai_image' credit account.", error.Reason);
        StringAssert.Contains(error.Message, "402");
        StringAssert.Contains(error.Message, "Relatude Imaging service");
        Assert.IsFalse(error.MayHaveBeenCharged, "a 402 is given before anything is charged");
    }

    [TestMethod]
    public async Task TheOperationsAreReadWithoutALicense() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub, key: null);
        stub.EnqueueJson(200, """
            {"operations":[{"key":"create-image","name":"Create image","available":true},{"key":"shrink-image","name":"Shrink image","available":false}],
             "creditsPerOperation":1,"creditsPerCachedOperation":0,"partBytes":10485760,"maxFileBytes":20971520}
            """);

        var operations = await imaging.GetOperationsAsync();

        Assert.AreEqual(2, operations.Operations.Length);
        Assert.IsTrue(operations.IsAvailable("create-image"));
        Assert.IsFalse(operations.IsAvailable("shrink-image"));
        Assert.IsFalse(operations.IsAvailable("no-such-thing"));
        Assert.AreEqual(0, operations.CreditsPerCachedOperation);
        Assert.AreEqual(20971520, operations.MaxFileBytes);
        var request = stub.Single();
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/api/imaging/operations", request.Path);
        Assert.IsNull(request.Header("Authorization"), "no key, and none needed");
    }

    /// <summary>
    /// On a server the key is the installation's own, asked for at every call, and comes before the
    /// one in the settings. What the service holds belongs to a license, so another key starts over:
    /// a file sent for one license is sent again for the next.
    /// </summary>
    [TestMethod]
    public async Task TheLicenseKeyIsAskedForAtEveryCallAndAnotherKeySendsTheFilesAgain() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        var key = "11111111-1111-1111-1111-111111111111";
        using var imaging = new RelatudeServicesImagingProvider(new ImagingProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "settings-key" }, () => key);
        var image = RelatudeServiceStub.FakePng(32, 32, seed: 1);
        var answer = RelatudeServiceStub.FakePng(32, 32, seed: 2);
        stub.EnqueueImage(answer, priced(answer));
        stub.EnqueueImage(answer, priced(answer));

        await imaging.RemoveBackgroundAsync(image);
        Assert.AreEqual("Bearer " + key, stub.Operations()[^1].Header("Authorization"), "the installation's key before the one in the settings");

        key = "22222222-2222-2222-2222-222222222222";
        await imaging.RemoveBackgroundAsync(image);
        Assert.AreEqual("Bearer " + key, stub.Operations()[^1].Header("Authorization"), "a new license applies at once");
        var puts = stub.Calls("PUT", "/files/");
        Assert.AreEqual(2, puts.Length, "the new license is offered the file too");
        Assert.AreEqual(0, puts[1].Body.Length, "and the service, holding the bytes already, answered before they were sent");
    }

    [TestMethod]
    public async Task WithoutAKeyNothingIsSentAndTheErrorSaysWhereToSetOne() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub, key: null);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => imaging.RemoveBackgroundAsync(RelatudeServiceStub.FakePng(8, 8)));
        StringAssert.Contains(error.Message, "API key");
        Assert.AreEqual(0, stub.Requests.Count);
    }

    /// <summary>
    /// The service answers rotate-if-needed with the image turned and X-Rotation saying how far, held
    /// for the license like any answer; or with no content and a rotation of 0 when it left it as it is.
    /// </summary>
    [TestMethod]
    public async Task RotateIfNeededSaysHowFarAndHasTheImageOnlyWhenItWasTurned() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(640, 480, seed: 1);
        var turned = RelatudeServiceStub.FakePng(480, 640, seed: 2);
        stub.EnqueueImage(turned, [.. priced(turned, credits: 1, left: 41), ("X-Rotation", "90")]);

        var rotation = await imaging.RotateIfNeededAsync(photo);

        Assert.AreEqual(90, rotation.Rotation);
        Assert.IsTrue(rotation.Rotated);
        CollectionAssert.AreEqual(turned, rotation.Image!.Png);
        Assert.AreEqual((480, 640), (rotation.Image.Width, rotation.Image.Height));
        Assert.AreEqual(RelatudeServiceStub.Sha256(turned), rotation.Image.Sha256);
        Assert.AreEqual((1, 41, false), (rotation.Credits, rotation.CreditsLeft, rotation.Cached));
        var call = stub.Operations().Single();
        Assert.AreEqual("/api/imaging/rotate-if-needed", call.Path);
        Assert.AreEqual(RelatudeServiceStub.Sha256(photo), call.Json.GetProperty("image").GetString());

        // left as it is: no image, and nothing to read but the headers
        stub.EnqueueNoContent(("X-Rotation", "0"), ("X-Credits", "1"), ("X-Credits-Left", "40"), ("X-Cache", "hit"));
        var left = await imaging.RotateIfNeededAsync(photo, fresh: true);
        Assert.AreEqual(0, left.Rotation);
        Assert.IsFalse(left.Rotated);
        Assert.IsNull(left.Image);
        Assert.AreEqual((1, 40, true), (left.Credits, left.CreditsLeft, left.Cached));
        Assert.AreEqual("no-cache", stub.Operations()[^1].Header("Cache-Control"));
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "the photo is sent once");

        // an answer that names no turn the service makes is a failure, not a guess
        stub.EnqueueImage(turned, [.. priced(turned), ("X-Rotation", "45")]);
        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => imaging.RotateIfNeededAsync(photo));
        StringAssert.Contains(error.Message, "'45'");
    }

    [TestMethod]
    public async Task AQuestionAboutAnImageIsAnsweredInWordsOrWithYesOrNoAndACertainty() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var photo = RelatudeServiceStub.FakePng(640, 480, seed: 1);
        stub.EnqueueJson(200, """{"answer":"A red bicycle.","credits":1,"creditsLeft":20}""");
        stub.EnqueueJson(200, """{"answer":false,"certainty":92,"credits":1,"creditsLeft":19}""", ("X-Cache", "hit"));

        var words = await imaging.AskAboutImageAsync(photo, "What is leaning on the wall?");
        var yesNo = await imaging.AskAboutImageBoolAsync(photo, "Is it raining?", fresh: true);

        Assert.AreEqual(("A red bicycle.", 1, 20, false), (words.Answer, words.Credits, words.CreditsLeft, words.Cached));
        Assert.AreEqual((false, 92, 19, true), (yesNo.Answer, yesNo.Certainty, yesNo.CreditsLeft, yesNo.Cached));
        var (ask, askBool) = (stub.Operations()[0], stub.Operations()[1]);
        Assert.AreEqual("/api/imaging/ask-about-image", ask.Path);
        Assert.AreEqual("What is leaning on the wall?", ask.Json.GetProperty("question").GetString());
        Assert.AreEqual(RelatudeServiceStub.Sha256(photo), ask.Json.GetProperty("image").GetString());
        Assert.AreEqual("/api/imaging/ask-about-image-bool", askBool.Path);
        Assert.AreEqual("Is it raining?", askBool.Json.GetProperty("question").GetString());
        Assert.AreEqual("no-cache", askBool.Header("Cache-Control"));
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length, "the photo is sent once for both questions");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => imaging.AskAboutImageAsync(photo, " "));
        Assert.AreEqual(2, stub.Operations().Length, "a question that is no question is refused before anything is sent");
    }

    [TestMethod]
    public async Task WhatTheServiceWouldRefuseIsRefusedBeforeAnythingIsSent() {
        await using var stub = await RelatudeServiceStub.StartAsync("imaging");
        using var imaging = provider(stub);
        var image = RelatudeServiceStub.FakePng(8, 8);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => imaging.CreateImageAsync("x", width: 100));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => imaging.CreateImageAsync(" "));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => imaging.UpscaleAsync(image, 3));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => imaging.ExpandImageAsync(image, new ImageMargins()));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => imaging.ShrinkImageAsync(image, new ImageMargins(Top: -1, Left: 5)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => imaging.RemoveBackgroundAsync([]));
        Assert.AreEqual(0, stub.Requests.Count);
    }

    [TestMethod]
    public void ThePngSizeIsReadFromItsHeaderAndNothingElseHasOne() {
        Assert.AreEqual((1920, 1080), RelatudeServicesImagingProvider.PngSize(RelatudeServiceStub.FakePng(1920, 1080)));
        Assert.AreEqual((0, 0), RelatudeServicesImagingProvider.PngSize("GIF89a not a png at all"u8));
        Assert.AreEqual((0, 0), RelatudeServicesImagingProvider.PngSize([]));
    }

    [TestMethod]
    public void TheHostedServiceIsTheDefaultAndBothNamesMeanIt() {
        using var hosted = new RelatudeServicesImagingProvider(new ImagingProviderSettings());
        Assert.AreEqual("https://imaging.services.relatude.com", hosted.ServiceUrl);
        using var own = new RelatudeServicesImagingProvider(new ImagingProviderSettings { ServiceUrl = " https://localhost:7001/ " });
        Assert.AreEqual("https://localhost:7001", own.ServiceUrl);
        Assert.IsTrue(RelatudeServicesImagingProvider.IsProviderName("RelatudeServices"));
        Assert.IsTrue(RelatudeServicesImagingProvider.IsProviderName(" relatudeservicesimagingprovider "));
        Assert.IsFalse(RelatudeServicesImagingProvider.IsProviderName(""));
        Assert.IsFalse(RelatudeServicesImagingProvider.IsProviderName("My.Own.Provider"));
    }
}
