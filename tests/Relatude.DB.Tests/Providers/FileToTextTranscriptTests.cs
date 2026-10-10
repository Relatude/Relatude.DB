using System.Diagnostics.CodeAnalysis;
using System.Text;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.FileToText;
using Relatude.DB.IO;

namespace Relatude.Providers;

/// <summary>
/// Recordings and videos through the FileToText provider: the transcript the service gives - a line
/// for each stretch of speech, beginning with when it is spoken - read without its times for an index
/// and written as subtitles; and a video's sound taken by the database's file converters before it is
/// sent, in parts when it plays for longer than the service takes in one call. The converter here is a
/// fake that writes what it was asked for, so what is under test is the provider's own decisions; the
/// FFmpeg plugin does the real conversion.
/// </summary>
[TestClass]
public class FileToTextTranscriptTests {
    const string _key = "cd4e092c-ac57-4661-86c8-9b3f4acf6438";

    const string _transcript =
        "[00:00:00.080 --> 00:00:02.500] Hei og velkommen.\n" +
        "[01:02:05.500 --> 01:02:07.004] To the <second> hour & more.";

    // ------------------------------------------------------------------ the transcript

    [TestMethod]
    public void ATranscriptIsReadLineByLineWithItsTimes() {
        var cues = Transcript.Parse(_transcript);

        Assert.HasCount(2, cues);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), cues[0].Start);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2500), cues[0].End);
        Assert.AreEqual("Hei og velkommen.", cues[0].Text);
        Assert.AreEqual(new TimeSpan(0, 1, 2, 5, 500), cues[1].Start);
        Assert.AreEqual(new TimeSpan(0, 1, 2, 7, 4), cues[1].End);
        Assert.AreEqual(_transcript, Transcript.Write(cues), "written back as the service writes it");
        Assert.IsTrue(Transcript.IsTimed(_transcript));
        Assert.IsFalse(Transcript.IsTimed("Annual report\n\fPage two"));
    }

    [TestMethod]
    public void WithoutItsTimesATranscriptIsWhatIsSaidLineByLine() {
        Assert.AreEqual("Hei og velkommen.\nTo the <second> hour & more.", Transcript.RemoveTimestamps(_transcript));
        Assert.AreEqual("[not a time] stays", Transcript.RemoveTimestamps("[not a time] stays"), "only a time at the start of a line goes");
        Assert.AreEqual("100 hours in", Transcript.RemoveTimestamps("[100:00:00.000 --> 100:00:01.000] 100 hours in"));
    }

    [TestMethod]
    public void ALineWithoutATimeGoesWithTheLineBeforeIt() {
        var cues = Transcript.Parse("before the first\r\n[00:00:01.000 --> 00:00:02.000] one\r\n  and more  \r\n\r\n[00:00:03.000 --> 00:00:04.000] two");

        Assert.HasCount(2, cues);
        Assert.AreEqual("one and more", cues[0].Text);
        Assert.AreEqual("two", cues[1].Text);
    }

    [TestMethod]
    public void ATranscriptIsWrittenAsWebVttAndSrtSubtitles() {
        var cues = Transcript.Parse(_transcript);

        Assert.AreEqual(
            "WEBVTT\n\n" +
            "00:00:00.080 --> 00:00:02.500\nHei og velkommen.\n\n" +
            "01:02:05.500 --> 01:02:07.004\nTo the &lt;second&gt; hour &amp; more.\n", Transcript.ToWebVtt(cues));
        Assert.AreEqual(
            "1\n00:00:00,080 --> 00:00:02,500\nHei og velkommen.\n\n" +
            "2\n01:02:05,500 --> 01:02:07,004\nTo the <second> hour & more.\n", Transcript.ToSrt(cues));
        Assert.AreEqual("WEBVTT\n", Transcript.ToWebVtt([]));
    }

    [TestMethod]
    public void ThePartsOfATranscriptAreTimedFromTheStartOfTheWhole() {
        var shifted = Transcript.Shift(Transcript.Parse(_transcript), TimeSpan.FromMinutes(55)).ToList();

        Assert.AreEqual(new TimeSpan(0, 0, 55, 0, 80), shifted[0].Start);
        Assert.AreEqual("[00:55:00.080 --> 00:55:02.500] Hei og velkommen.", Transcript.Line(shifted[0]));
    }

    [TestMethod]
    public async Task ATimedAnswerCanBeReadWithoutItsTimesAndAsSubtitles() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub, null);
        stub.EnqueueJson(200, answer(_transcript, duration: 3727.004, format: "ogg"));

        var text = await reader.ExtractTextAsync(TestSounds.Ogg("a recording"), "talk.ogg");

        Assert.IsTrue(text.Timed);
        Assert.AreEqual(3727.004, text.Duration);
        Assert.AreEqual("Hei og velkommen.\nTo the <second> hour & more.", text.TextWithoutTimestamps);
        Assert.HasCount(2, text.Cues);
        StringAssert.StartsWith(text.ToWebVtt(), "WEBVTT\n\n00:00:00.080 --> 00:00:02.500\n");
        Assert.AreEqual(1, text.PageTexts.Length);

        var plain = new FileToTextResult("Annual report", "pdf", null, 13, 1, false, false, null, null, null, 1, 9, false);
        Assert.AreEqual("Annual report", plain.TextWithoutTimestamps, "a text that is not a transcript is as it is");
        Assert.IsEmpty(plain.Cues);
    }

    // ------------------------------------------------------------------ videos and recordings

    [TestMethod]
    public async Task AVideoIsSentAsItsSoundTakenByTheFileConverters() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var sounds = new FakeSoundConverter();
        using var reader = provider(stub, sounds.Engine);
        stub.EnqueueJson(200, formats(maxAudioMinutes: 60));
        stub.EnqueueJson(200, answer("[00:00:01.000 --> 00:00:02.000] Hello.", duration: 10, format: "ogg"), ("X-Cache", "miss"));
        var video = TestSounds.Mp4();

        var text = await reader.ExtractTextAsync(video, "clips/talk.mp4", ["nb"]);

        Assert.AreEqual("[00:00:01.000 --> 00:00:02.000] Hello.", text.Text);
        Assert.AreEqual("mp4", text.Format, "the file is what it was, whatever was sent");
        Assert.IsTrue(text.Timed);
        Assert.AreEqual(10, text.Duration);
        // its length asked, then its sound taken as speech
        Assert.HasCount(2, sounds.Calls);
        Assert.IsInstanceOfType<FileAdjustmentMeta>(sounds.Calls[0].Adjustment);
        var sound = (FileAdjustmentAudio)sounds.Calls[1].Adjustment;
        Assert.AreEqual(FileFormat.Ogg, sound.RequestedFormat);
        Assert.AreEqual(1, sound.Channels);
        Assert.AreEqual(16000, sound.SampleRate);
        Assert.AreEqual(24, sound.BitRateKbps);
        Assert.IsTrue(sound.Speech);
        Assert.IsNull(sound.StartMs);
        Assert.IsNull(sound.DurationMs, "short enough for one call: all of it");
        Assert.AreEqual(FileFormat.Mp4, sounds.Calls[1].From);
        CollectionAssert.AreEqual(video, sounds.Calls[1].Input, "the converter reads the video as it was given");
        // the sound is what was sent, under the video's name
        var put = stub.Calls("PUT", "/files/").Single();
        CollectionAssert.AreEqual(FakeSoundConverter.SoundOf(sound), put.Body);
        var call = stub.Operations().Single(r => r.Path.EndsWith("/extract"));
        Assert.AreEqual("clips/talk.mp4", call.Json.GetProperty("fileName").GetString());
        Assert.AreEqual(0, Directory.GetFiles(sounds.TempFolder).Length, "nothing is left in the temp folder");
    }

    [TestMethod]
    public async Task ARecordingLongerThanTheServiceTakesIsSentInPartsAndTimedFromTheStart() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var sounds = new FakeSoundConverter { Duration = TimeSpan.FromSeconds(130) };
        using var reader = provider(stub, sounds.Engine);
        stub.EnqueueJson(200, formats(maxAudioMinutes: 1));
        stub.EnqueueJson(200, answer("[00:00:01.000 --> 00:00:02.000] One.", duration: 55, format: "ogg", credits: 1, left: 9), ("X-Cache", "hit"));
        stub.EnqueueJson(200, answer("[00:00:00.500 --> 00:00:03.000] Two.", duration: 55, format: "ogg", credits: 2, left: 7), ("X-Cache", "miss"));
        stub.EnqueueJson(200, answer("[00:00:10.000 --> 00:00:12.000] Three.\n[00:00:15.000 --> 00:00:19.000] Four.", duration: 20, format: "ogg", credits: 2, left: 5));

        var text = await reader.ExtractTextAsync(TestSounds.Mp3(), "lecture.mp3");

        // a part is 55 s, five less than the minute the service takes: 0-55, 55-110, and what is left
        Assert.AreEqual(
            "[00:00:01.000 --> 00:00:02.000] One.\n" +
            "[00:00:55.500 --> 00:00:58.000] Two.\n" +
            "[00:02:00.000 --> 00:02:02.000] Three.\n" +
            "[00:02:05.000 --> 00:02:09.000] Four.", text.Text);
        Assert.AreEqual("mp3", text.Format);
        Assert.AreEqual(130, text.Duration, "the whole recording's length");
        Assert.AreEqual(5, text.Credits, "every part paid for");
        Assert.AreEqual(5, text.CreditsLeft, "what the last part left");
        Assert.IsFalse(text.Cached, "not every part was read before");
        Assert.AreEqual(text.Text.Length, text.Characters);
        var parts = sounds.Calls.Skip(1).Select(c => (FileAdjustmentAudio)c.Adjustment).ToArray();
        Assert.HasCount(3, parts);
        Assert.AreEqual((null, (int?)55000), (parts[0].StartMs, parts[0].DurationMs));
        Assert.AreEqual(((int?)55000, (int?)55000), (parts[1].StartMs, parts[1].DurationMs));
        Assert.AreEqual(((int?)110000, (int?)null), (parts[2].StartMs, parts[2].DurationMs), "the last part takes what is left");
        Assert.HasCount(3, stub.Calls("PUT", "/files/"), "each part a file of its own");
    }

    [TestMethod]
    public async Task AVideoWithoutAConverterIsRefusedBeforeAnythingIsSent() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub, null);

        var error = await Assert.ThrowsExactlyAsync<RelatudeServiceException>(() => reader.ExtractTextAsync(TestSounds.Mp4(), "talk.mp4"));

        Assert.AreEqual(415, error.StatusCode);
        Assert.IsFalse(error.MayHaveBeenCharged);
        StringAssert.Contains(error.Message, "FFmpeg");
        Assert.IsEmpty(stub.Requests);
    }

    [TestMethod]
    public async Task ARecordingWithoutAConverterIsSentAsItIs() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var reader = provider(stub, null);
        stub.EnqueueJson(200, answer("[00:00:00.000 --> 00:00:01.000] Hi.", duration: 1, format: "mp3"));
        var mp3 = TestSounds.Mp3();

        var text = await reader.ExtractTextAsync(mp3, "hi.mp3");

        Assert.AreEqual("mp3", text.Format);
        CollectionAssert.AreEqual(mp3, stub.Calls("PUT", "/files/").Single().Body);
    }

    [TestMethod]
    public async Task AVideoReadFromAStreamGoesToATempFileForItsSound() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var sounds = new FakeSoundConverter();
        IFileToTextProvider reader = provider(stub, sounds.Engine);
        using (reader) {
            stub.EnqueueJson(200, formats(maxAudioMinutes: 0));
            stub.EnqueueJson(200, answer("[00:00:01.000 --> 00:00:02.000] Streamed.", duration: 2, format: "ogg"));
            var video = TestSounds.Mp4(extra: 100_000);

            var text = await reader.ExtractTextAsync(new MemoryStream(video), "film.mp4");

            Assert.AreEqual("[00:00:01.000 --> 00:00:02.000] Streamed.", text.Text);
            Assert.HasCount(1, sounds.Calls, "with no limit on the service, the length is not asked");
            CollectionAssert.AreEqual(video, sounds.Calls[0].Input, "the stream written whole, its start and the rest");
            StringAssert.StartsWith(sounds.Calls[0].Path, sounds.TempFolder, "in the engine's temp folder");
        }
    }

    [TestMethod]
    public async Task TheServicesLimitIsAskedOnceAndKept() {
        await using var stub = await RelatudeServiceStub.StartAsync("filetotext");
        using var sounds = new FakeSoundConverter();
        using var reader = provider(stub, sounds.Engine);
        stub.EnqueueJson(200, formats(maxAudioMinutes: 60));
        stub.EnqueueJson(200, answer("[00:00:01.000 --> 00:00:02.000] A.", duration: 10, format: "ogg"));
        stub.EnqueueJson(200, answer("[00:00:01.000 --> 00:00:02.000] A.", duration: 10, format: "ogg"));

        await reader.ExtractTextAsync(TestSounds.Mp4(), "a.mp4");
        await reader.ExtractTextAsync(TestSounds.Mp4(), "a.mp4");

        Assert.HasCount(1, stub.Requests.Where(r => r.Path.EndsWith("/formats")));
    }

    // ------------------------------------------------------------------ helpers

    static RelatudeServicesFileToTextProvider provider(RelatudeServiceStub stub, FileConversionEngine? engine) =>
        new(new FileToTextProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = _key }, null, () => engine);

    static string formats(int maxAudioMinutes) => $$"""
        {"formats":[{"key":"ogg","name":"Ogg audio","group":"audio","mediaType":"audio/ogg","extensions":[".ogg"],"available":true}],
         "creditsPerOperation":1,"creditsPerCachedOperation":1,"partBytes":10485760,"maxFileBytes":52428800,"maxAudioMinutes":{{maxAudioMinutes}}}
        """;

    static string answer(string text, double duration, string format, int credits = 1, int left = 9) =>
        System.Text.Json.JsonSerializer.Serialize(new {
            text, format, fileName = "x", characters = text.Length, pages = (int?)null, truncated = false, ocr = false, timed = true, duration,
            title = (string?)null, author = (string?)null, language = "nb-NO", credits, creditsLeft = left,
        });
}

/// <summary>The starts of recordings and videos, which is all the provider looks at before it hands them to a converter.</summary>
static class TestSounds {
    public static byte[] Mp4(int extra = 64) => [0, 0, 0, 0x18, .. "ftypisom"u8, 0, 0, 2, 0, .. "isomiso2"u8, .. new byte[extra]];
    public static byte[] Mp3() => [0xFF, 0xFB, 0x90, 0x64, .. new byte[400]];
    public static byte[] Ogg(string what) => [.. "OggS"u8, 0, 2, .. Encoding.UTF8.GetBytes(what)];
}

/// <summary>
/// A file converter that takes the "sound" of a recording or a video by writing what it was asked for,
/// and tells its length as <see cref="Duration"/>; every conversion is kept, with the file it read.
/// Its engine keeps its temp files in a folder of its own, deleted with it.
/// </summary>
sealed class FakeSoundConverter : IFileConverter, IDisposable {
    public sealed record Call(string Path, byte[] Input, FileFormat From, FileAdjustmentBase Adjustment);

    public readonly List<Call> Calls = [];
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
    public string TempFolder => Engine.LocalTempFolderPath;
    public FileConversionEngine Engine { get; }
    readonly string _folder = Path.Combine(Path.GetTempPath(), "relatude-sound-" + Guid.NewGuid().ToString("N"));

    public FakeSoundConverter() {
        Directory.CreateDirectory(_folder);
        Engine = new FileConversionEngine(null!, [this], new IOProviderDisk(_folder));
        Directory.CreateDirectory(TempFolder);
    }

    public static byte[] SoundOf(FileAdjustmentAudio adjustment) =>
        [.. "OggS"u8, .. Encoding.ASCII.GetBytes($" sound from {adjustment.StartMs ?? 0} ms for {adjustment.DurationMs?.ToString() ?? "the rest"}")];

    public int ThreadCount { get; set; } = 1;
    public int CallDelayMs { get; set; }
    public void Initialize(FileConversionEngine conversionEngine) { }
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) =>
        inBase is FileType.Video or FileType.Audio && outDetailed is FileFormat.Ogg or FileFormat.FileMetaJson;
    public Task<bool> CancelAsync(Guid key) => Task.FromResult(false);

    public Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var input = source.GetLocalFilePathOrThrow();
        lock (Calls) Calls.Add(new Call(input, File.ReadAllBytes(input), info.FromFormat, info.IdWithAdjustment.Adjustment));
        var output = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + ".out");
        File.WriteAllBytes(output, info.IdWithAdjustment.Adjustment is FileAdjustmentAudio sound
            ? SoundOf(sound)
            : new BasicFileMeta { Duration = Duration }.ToBytes());
        return Task.FromResult(new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), null, output));
    }

    public bool TryGetLiveStatus(Guid key, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        status = null;
        return false;
    }

    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor) => [];

    public void Dispose() {
        Engine.Dispose();
        try { Directory.Delete(_folder, true); } catch { }
    }
}
