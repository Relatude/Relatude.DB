using System.Text;
using Relatude.DB.Common;
using Relatude.DB.FileToText;

namespace Relatude.Providers;

/// <summary>
/// The FileToText provider against the requests it puts on the wire: the file sent once to
/// files/{sha256}, then named by its SHA-256 in the extract call with its name and languages, and the
/// text coming back as JSON. The stub holds to the service's file protocol (<see cref="RelatudeServiceStub"/>).
/// </summary>
[TestClass]
public class FileToTextProviderWireTests {
    const string _key = "cd4e092c-ac57-4661-86c8-9b3f4acf6438";

    static RelatudeServicesFileToTextProvider provider(RelatudeServiceStub stub) =>
        new(new FileToTextProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = _key });

    const string _answer = """
        {"text":"Annual report\n\fPage two\n","format":"pdf","fileName":"report.pdf","characters":23,"pages":2,"truncated":false,"ocr":true,
         "title":"Annual report","author":"Ole","language":"en","credits":1,"creditsLeft":499}
        """;

    [TestMethod]
    public async Task AFileIsSentOnceAndItsTextReadWithItsNameAndLanguages() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub);
        var file = Encoding.UTF8.GetBytes("%PDF-1.7 pretend this is a scanned report");
        stub.EnqueueJson(200, _answer, ("X-Cache", "miss"));

        var text = await reader.ExtractTextAsync(file, "reports/2026/report.pdf", ["nb", " en ", ""]);

        Assert.AreEqual("Annual report\n\fPage two\n", text.Text);
        CollectionAssert.AreEqual(new[] { "Annual report\n", "Page two\n" }, text.PageTexts);
        Assert.AreEqual("pdf", text.Format);
        Assert.AreEqual(2, text.Pages);
        Assert.IsTrue(text.Ocr);
        Assert.IsFalse(text.Truncated);
        Assert.AreEqual("Annual report", text.Title);
        Assert.AreEqual("Ole", text.Author);
        Assert.AreEqual(499, text.CreditsLeft);
        Assert.IsFalse(text.Cached);

        var sha = RelatudeServiceStub.Sha256(file);
        var put = stub.Calls("PUT", "/files/").Single();
        Assert.AreEqual("/api/filetotext/files/" + sha, put.Path);
        CollectionAssert.AreEqual(file, put.Body);
        var call = stub.Operations().Single();
        Assert.AreEqual("POST", call.Method);
        Assert.AreEqual("/api/filetotext/extract", call.Path);
        Assert.AreEqual("Bearer " + _key, call.Header("Authorization"));
        Assert.AreEqual(sha, call.Json.GetProperty("file").GetString());
        Assert.AreEqual("reports/2026/report.pdf", call.Json.GetProperty("fileName").GetString(), "the name goes as given; the service drops the folder");
        var languages = call.Json.GetProperty("languages");
        Assert.AreEqual(2, languages.GetArrayLength(), "blank codes are left out");
        Assert.AreEqual("nb", languages[0].GetString());
        Assert.AreEqual("en", languages[1].GetString());

        // read again, anew: the file is named, not sent, and the call asks for a new reading
        stub.EnqueueJson(200, _answer, ("X-Cache", "miss"));
        await reader.ExtractTextAsync(file, fresh: true);
        Assert.AreEqual(1, stub.Calls("PUT", "/files/").Length);
        var fresh = stub.Operations()[^1];
        Assert.AreEqual("no-cache", fresh.Header("Cache-Control"));
        Assert.IsFalse(fresh.Json.TryGetProperty("fileName", out _));
        Assert.IsFalse(fresh.Json.TryGetProperty("languages", out _));
    }

    [TestMethod]
    public async Task ATextReadBeforeSaysSo() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub);
        stub.EnqueueJson(200, """{"text":"hello\n","format":"txt","characters":6,"pages":null,"truncated":false,"ocr":false,"credits":0,"creditsLeft":10}""", ("X-Cache", "hit"));

        var text = await reader.ExtractTextAsync(Encoding.UTF8.GetBytes("hello"), "a.txt");

        Assert.IsTrue(text.Cached);
        Assert.IsNull(text.Pages);
        Assert.AreEqual(0, text.Credits);
        CollectionAssert.AreEqual(new[] { "hello\n" }, text.PageTexts, "a file without pages is one page");
    }

    /// <summary>
    /// A kind of file nothing reads is refused before anything is charged, which is what a caller
    /// indexing every file it has needs to tell apart from a failure it paid for.
    /// </summary>
    [TestMethod]
    public async Task AFileNothingReadsIsRefusedAndCostNothing() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub);
        stub.EnqueueJson(415, """{"error":"Nothing here reads files of this kind (doc)."}""");

        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => reader.ExtractTextAsync([0xD0, 0xCF, 0x11, 0xE0], "old.doc"));

        Assert.AreEqual(415, error.StatusCode);
        Assert.IsFalse(error.MayHaveBeenCharged);
        Assert.AreEqual("Nothing here reads files of this kind (doc).", error.Reason);

        stub.EnqueueJson(422, """{"error":"The file is locked with a password."}""");
        var locked = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => reader.ExtractTextAsync([1, 2, 3], "secret.pdf"));
        Assert.IsTrue(locked.MayHaveBeenCharged, "a file that could not be read was paid for");
        Assert.AreEqual(2, stub.Operations().Length, "neither is repeated");
    }

    [TestMethod]
    public async Task AStreamIsReadToItsEndAndSentLikeTheBytes() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        IFileToTextProvider reader = provider(stub);
        using (reader) {
            var bytes = Encoding.UTF8.GetBytes(new string('x', 5000));
            stub.EnqueueJson(200, """{"text":"x\n","format":"txt","characters":2,"truncated":false,"ocr":false,"credits":1,"creditsLeft":9}""");

            await reader.ExtractTextAsync(new MemoryStream(bytes), "x.txt");

            CollectionAssert.AreEqual(bytes, stub.Files[RelatudeServiceStub.Sha256(bytes)]);
        }
    }

    [TestMethod]
    public async Task TheFormatsAreReadWithoutALicense() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = new RelatudeServicesFileToTextProvider(new FileToTextProviderSettings { ServiceUrl = stub.BaseUrl });
        stub.EnqueueJson(200, """
            {"formats":[{"key":"pdf","name":"PDF","group":"pdf","mediaType":"application/pdf","extensions":[".pdf"],"available":true},
                        {"key":"doc","name":"Word 97-2003","group":"document","mediaType":"application/msword","extensions":["doc","dot"],"available":false}],
             "creditsPerOperation":1,"creditsPerCachedOperation":1,"partBytes":10485760,"maxFileBytes":52428800}
            """);

        var formats = await reader.GetFormatsAsync();

        Assert.AreEqual(2, formats.Formats.Length);
        Assert.AreEqual(52428800, formats.MaxFileBytes);
        Assert.AreEqual("pdf", formats.ForFileName("Report.PDF")?.Key, "an extension is matched with or without its dot, in any case");
        Assert.IsFalse(formats.ForFileName("old.dot")!.Available);
        Assert.IsNull(formats.ForFileName("noextension"));
        var request = stub.Single();
        Assert.AreEqual("/api/filetotext/formats", request.Path);
        Assert.IsNull(request.Header("Authorization"));
    }

    [TestMethod]
    public void TheHostedServiceIsTheDefault() {
        using var hosted = new RelatudeServicesFileToTextProvider(new FileToTextProviderSettings());
        Assert.AreEqual("https://filetotext.services.relatude.com", hosted.ServiceUrl);
        Assert.IsTrue(RelatudeServicesFileToTextProvider.IsProviderName("RelatudeServices"));
        Assert.IsTrue(RelatudeServicesFileToTextProvider.IsProviderName(nameof(RelatudeServicesFileToTextProvider)));
        Assert.IsFalse(RelatudeServicesFileToTextProvider.IsProviderName("My.Own.Reader"));
    }
}
