using Relatude.DB.SMS;

namespace Relatude.Providers;

/// <summary>
/// The SMS provider against the exact requests it puts on the wire. It talks to the hosted Relatude
/// SMS service, whose contract is its own: the license API key as the bearer token, the number in
/// whatever shape the caller had it, and the service answering with the number it actually used.
/// <para>The stub is <see cref="AiServiceStub"/>, which is scripted by response rather than by
/// route, so it serves this service as well as the AI one.</para>
/// </summary>
[TestClass]
public class SmsProviderWireTests {
    [TestMethod]
    public async Task SendingPutsTheLicenseKeyAndTheConfiguredSenderOnTheWire() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings {
            ServiceUrl = stub.BaseUrl,
            ApiKey = "cd4e092c-ac57-4661-86c8-9b3f4acf6438",
            From = "MyShop",
        });
        stub.Enqueue(200, """{"messageId":"gw-123","to":"+4791234567","parts":1,"credits":1,"creditsLeft":98,"reference":"order-7"}""");

        var receipt = await provider.SendAsync("912 34 567", "Your order has shipped", reference: "order-7");

        Assert.AreEqual("gw-123", receipt.MessageId);
        Assert.AreEqual("+4791234567", receipt.To, "the service says which number it used, and that is what the caller keeps");
        Assert.AreEqual(1, receipt.Parts);
        Assert.AreEqual(1, receipt.Credits);
        Assert.AreEqual(98, receipt.CreditsLeft);
        Assert.AreEqual("order-7", receipt.Reference);

        var request = stub.Single();
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/api/sms/send", request.Path);
        Assert.AreEqual("Bearer cd4e092c-ac57-4661-86c8-9b3f4acf6438", request.Headers["Authorization"]);
        Assert.AreEqual("912 34 567", request.Json.GetProperty("to").GetString(), "the number goes as typed; normalising is the service's job");
        Assert.AreEqual("Your order has shipped", request.Json.GetProperty("message").GetString());
        Assert.AreEqual("MyShop", request.Json.GetProperty("from").GetString());
        Assert.AreEqual("order-7", request.Json.GetProperty("reference").GetString());
    }

    [TestMethod]
    public async Task ASenderGivenOnTheCallBeatsTheConfiguredOneAndNoSenderSendsNone() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" });
        stub.Enqueue(200, """{"messageId":"a","to":"+4791234567","parts":1,"credits":1,"creditsLeft":9}""");
        await provider.SendAsync("+4791234567", "hi", from: "Support");
        Assert.AreEqual("Support", stub.Single().Json.GetProperty("from").GetString());

        stub.Requests.Clear();
        stub.Enqueue(200, """{"messageId":"b","to":"+4791234567","parts":1,"credits":1,"creditsLeft":8}""");
        await provider.SendAsync("+4791234567", "hi");
        Assert.IsFalse(stub.Single().Json.TryGetProperty("from", out _), "with no sender anywhere the service picks its own");
    }

    [TestMethod]
    public async Task AQuoteAsksWhatAMessageWouldCostWithoutSendingIt() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" });
        stub.Enqueue(200, """{"to":"+4791234567","parts":2,"credits":2,"unicode":true,"characters":75}""");

        var quote = await provider.QuoteAsync("+4791234567", "a message with an en dash –");

        Assert.AreEqual(2, quote.Parts);
        Assert.AreEqual(2, quote.Credits);
        Assert.IsTrue(quote.Unicode, "the caller needs to know why a short message costs two parts");
        Assert.AreEqual(75, quote.Characters);
        Assert.AreEqual("/api/sms/quote", stub.Single().Path);
    }

    [TestMethod]
    public async Task ARefusalRepeatsTheServiceReasonAndIsNotRetried() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" });
        // 402 is the service saying the license is out of credits: a permanent answer for this message
        stub.Enqueue(402, """{"error":"Only 1 of 500 left this month on 'SMS'."}""");

        var error = await Assert.ThrowsExactlyAsync<Exception>(() => provider.SendAsync("+4791234567", "hi"));

        StringAssert.Contains(error.Message, "402");
        StringAssert.Contains(error.Message, "left this month");
        Assert.AreEqual(1, stub.Requests.Count, "a licensing refusal must not be retried");
    }

    /// <summary>
    /// The settings need no key: on a server the provider sends with the installation's license. A
    /// provider with no key anywhere is still built - the database opens - and the first send says
    /// what is missing, before anything goes on the wire.
    /// </summary>
    [TestMethod]
    public async Task WithoutAnyKeyTheProviderIsBuiltAndASendSaysWhatIsMissing() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl }, () => null);

        var send = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.SendAsync("+4791234567", "hi"));
        StringAssert.Contains(send.Message, "API key");
        StringAssert.Contains(send.Message, "License");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.QuoteAsync("+4791234567", "hi"));
        Assert.AreEqual(0, stub.Requests.Count, "nothing is sent without a key");
    }

    /// <summary>
    /// The license's key is read at every call, so a new license applies without the database
    /// reopening, and it comes before a key in the settings: that one is a copy the settings used to
    /// require, which would otherwise go stale unnoticed. Only without a license key is it used.
    /// </summary>
    [TestMethod]
    public async Task TheLicenseKeyIsReadAtEveryCallAndComesBeforeTheSettingsKey() {
        await using var stub = await AiServiceStub.StartAsync();
        string? licenseKey = "license-1";
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "from-settings" }, () => licenseKey);
        var sentWith = new List<string>();
        foreach (var key in new[] { "license-1", "license-2", null }) {
            licenseKey = key;
            stub.Requests.Clear();
            stub.Enqueue(200, """{"messageId":"a","to":"+4791234567","parts":1,"credits":1,"creditsLeft":9}""");
            await provider.SendAsync("+4791234567", "hi");
            sentWith.Add(stub.Single().Headers["Authorization"]);
        }
        CollectionAssert.AreEqual(new[] { "Bearer license-1", "Bearer license-2", "Bearer from-settings" }, sentWith);
    }

    [TestMethod]
    public void TheProviderIsRecognisedByEitherOfItsConfiguredNames() {
        Assert.IsTrue(RelatudeServicesSMSProvider.IsProviderName("RelatudeServices"));
        Assert.IsTrue(RelatudeServicesSMSProvider.IsProviderName(nameof(RelatudeServicesSMSProvider)));
        Assert.IsFalse(RelatudeServicesSMSProvider.IsProviderName("Twilio"));
        Assert.IsFalse(RelatudeServicesSMSProvider.IsProviderName(null));
    }
}
