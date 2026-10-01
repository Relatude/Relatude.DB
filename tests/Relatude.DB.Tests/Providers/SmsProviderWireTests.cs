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

    /// <summary>
    /// On a server the provider asks the license server whether a sender is approved for the license
    /// before it posts anything. A refusal is thrown with the license server's own words, and the
    /// service never hears of the message, so nothing is sent or charged.
    /// </summary>
    [TestMethod]
    public async Task ASenderTheCheckRefusesIsThrownWithItsReasonAndNothingIsSent() {
        await using var stub = await AiServiceStub.StartAsync();
        var asked = new List<string>();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key", From = "MyShop" }, null,
            (sender, _) => {
                asked.Add(sender);
                return Task.FromResult<string?>(sender == "Acme" ? null : $"The sender {sender} is not approved for this license.");
            });
        stub.Enqueue(200, """{"messageId":"a","to":"+4791234567","parts":1,"credits":1,"creditsLeft":9}""");

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.SendAsync("+4791234567", "hi"));
        Assert.AreEqual("The sender MyShop is not approved for this license.", error.Message, "the configured sender is checked, and the reason passed on as it is");
        Assert.AreEqual(0, stub.Requests.Count, "a refused sender sends nothing");

        await provider.SendAsync("+4791234567", "hi", from: "Acme");
        Assert.AreEqual("Acme", stub.Single().Json.GetProperty("from").GetString(), "an approved sender goes on the wire");
        CollectionAssert.AreEqual(new[] { "MyShop", "Acme" }, asked, "the sender on the call is the one checked");
    }

    /// <summary>The service's own sender needs no approval, so a message without a sender is not checked.</summary>
    [TestMethod]
    public async Task NoSenderIsNotChecked() {
        await using var stub = await AiServiceStub.StartAsync();
        var checks = 0;
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" }, null,
            (_, _) => {
                checks++;
                return Task.FromResult<string?>("Nothing may be sent.");
            });
        stub.Enqueue(200, """{"messageId":"a","to":"+4791234567","parts":1,"credits":1,"creditsLeft":9}""");

        await provider.SendAsync("+4791234567", "hi", from: " ");

        Assert.AreEqual(0, checks);
        Assert.IsFalse(stub.Single().Json.TryGetProperty("from", out _), "the service picks its own");
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
    /// The service takes a message's credits before it hands the message to the gateway, and cannot
    /// give them back. So a send is repeated only on the two answers given before any money moves,
    /// 429 and 503, and not on the others the default policy repeats: a 502 or a 500 comes after the
    /// charge, and a 504 while the service may still be sending, so a repeat could be charged again
    /// and reach the phone twice.
    /// </summary>
    [TestMethod]
    public async Task ASendIsRepeatedOnlyOnTheAnswersGivenBeforeAnythingIsCharged() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" });
        foreach (var status in new[] { 408, 500, 502, 504 }) {
            stub.Requests.Clear();
            // nothing else is scripted: a repeat would be answered too, and counted
            stub.Enqueue(status, """{"error":"The message could not be sent right now. Try again shortly."}""");
            var error = await Assert.ThrowsExactlyAsync<Exception>(() => provider.SendAsync("+4791234567", "hi"));
            Assert.AreEqual(1, stub.Requests.Count, $"a {status} may come after the message was charged or sent, so it must not be repeated");
            StringAssert.Contains(error.Message, $"returned {status}");
        }
        foreach (var status in new[] { 429, 503 }) {
            stub.Requests.Clear();
            stub.Enqueue(status, """{"error":"Try again shortly."}""", retryAfter: status == 429 ? "0" : null);
            stub.Enqueue(200, """{"messageId":"a","to":"+4791234567","parts":1,"credits":1,"creditsLeft":9}""");
            var receipt = await provider.SendAsync("+4791234567", "hi");
            Assert.AreEqual("a", receipt.MessageId);
            Assert.AreEqual(2, stub.Requests.Count, $"a {status} comes before anything is charged, so it is repeated");
        }
    }

    /// <summary>
    /// A connection that drops once the service has the request looks to the caller like one that
    /// never connected, and by then the message may have been charged and sent. So a send does not
    /// repeat a connection error, where a quote, which costs nothing, does. The send goes first, on
    /// the provider's first connection, so that any repeat seen is the provider's own.
    /// </summary>
    [TestMethod]
    public async Task ASendDoesNotRepeatADroppedConnectionButAQuoteDoes() {
        await using var stub = await AiServiceStub.StartAsync();
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ServiceUrl = stub.BaseUrl, ApiKey = "key" });
        stub.EnqueueDroppedConnection(); // and nothing after it: a repeat would be answered too, and counted
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.SendAsync("+4791234567", "hi"));
        Assert.AreEqual(1, stub.Requests.Count, "the service had the request, and may have charged and sent it");

        stub.Requests.Clear();
        stub.EnqueueDroppedConnection();
        stub.Enqueue(200, """{"to":"+4791234567","parts":1,"credits":1,"unicode":false,"characters":2}""");
        var quote = await provider.QuoteAsync("+4791234567", "hi");
        Assert.AreEqual(1, quote.Credits);
        Assert.AreEqual(2, stub.Requests.Count, "a quote is free to ask again");
        Assert.IsTrue(stub.Requests.All(r => r.Path == "/api/sms/quote"));
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
