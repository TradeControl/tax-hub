using System.Reflection;
using System.Text.Json;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Application.Preparation;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

async Task AssertRejectedAsync(Func<Task> action, string message)
{
    assertions++;
    try { await action(); }
    catch (ArgumentException) { return; }
    catch (InvalidOperationException) { return; }
    catch (KeyNotFoundException) { return; }
    throw new InvalidOperationException(message);
}

var sandbox = EnvironmentSelector.Sandbox();
Assert(sandbox.Selected == HmrcEnvironmentProfiles.Sandbox
    && sandbox.Selected.LiveRequestsEnabled
    && sandbox.ResolveApiPath("/organisations/vat/123456789/obligations").AbsoluteUri
        == "https://test-api.service.hmrc.gov.uk/organisations/vat/123456789/obligations",
    "The closed HMRC sandbox profile is incorrect.");
Assert(HmrcEnvironmentProfiles.Production.ApiBaseUri.AbsoluteUri == "https://api.service.hmrc.gov.uk/"
    && !HmrcEnvironmentProfiles.Production.LiveRequestsEnabled,
    "The production profile must remain closed and disabled.");
Assert(typeof(EnvironmentSelector).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        .All(method => method.Name != "Production" && method.GetParameters().All(parameter =>
            parameter.ParameterType != typeof(AuthorityEnvironment))),
    "A public request path can select the production environment.");
Assert(typeof(HmrcOAuthTokenEndpoint).GetConstructors().All(constructor => constructor.GetParameters()
        .All(parameter => parameter.ParameterType != typeof(HttpClient))),
    "Public composition can inject an auto-redirecting OAuth HTTP client.");
await AssertRejectedAsync(() => Task.Run(() => sandbox.ResolveApiPath("https://evil.example/steal")),
    "An absolute request-supplied host was accepted.");
await AssertRejectedAsync(() => Task.Run(() => sandbox.ResolveApiPath("//evil.example/steal")),
    "A scheme-relative request-supplied host was accepted.");

var root = Path.Combine(Path.GetTempPath(), $"tax-hub-submission-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    const string syntheticClientId = "synthetic-client-id-7afed6";
    const string syntheticClientSecret = "synthetic-client-secret-b3c10a";
    var secretPath = Path.Combine(root, "clientsettings.json");
    await File.WriteAllTextAsync(secretPath, JsonSerializer.Serialize(new
    {
        uri = "https://evil.example/ignored",
        clientId = syntheticClientId,
        clientSecret = syntheticClientSecret
    }));
    var provider = new JsonFileSecretProvider(secretPath, HmrcSecretReferences.LegacyVatSandboxJsonProperties);
    using (var secret = await provider.ResolveAsync(HmrcSecretReferences.ClientSecret))
    {
        Assert(secret.Length == syntheticClientSecret.Length && secret.ToString() == "[PROTECTED]",
            "The secret provider did not return a protected value.");
        var copy = new char[secret.Length];
        secret.CopyTo(copy);
        Assert(new string(copy) == syntheticClientSecret, "The approved secret was not resolved exactly.");
        Array.Clear(copy);
    }
    await AssertRejectedAsync(async () =>
    {
        using var _ = await provider.ResolveAsync(new("unapproved-secret"));
    }, "An unapproved secret reference was resolved.");
    Assert(sandbox.Selected.ApiBaseUri.Host == "test-api.service.hmrc.gov.uk",
        "The secret file's uri field changed the trusted environment profile.");

    var metadataPath = Path.Combine(root, "audit", "submission-attempts.json");
    var options = SubmissionAttemptStoreOptions.SevenYearMetadata(metadataPath);
    var store = new FileSubmissionAttemptStore(options);
    var reservation = new SubmissionAttemptReservation(
        "vat:123456789:26A1", "vat.returns.submit", "POST", "tenant-a", "principal-a",
        "subject-period-a", new string('A', 64), "approval-a", AuthorityEnvironment.Sandbox);

    var concurrent = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
    {
        try { return await store.ReserveAsync(reservation); }
        catch (ActiveSubmissionAttemptException) { return null; }
    }));
    var reserved = concurrent.Single(item => item is not null)!;
    Assert(concurrent.Count(item => item is not null) == 1,
        "Concurrent write reservations created more than one active attempt.");
    await AssertRejectedAsync(() => store.ReserveAsync(reservation with { PrincipalReference = "principal-b" }),
        "Changing principal allowed a duplicate tenant/logical write reservation.");
    var otherTenant = await store.ReserveAsync(reservation with
    {
        TenantReference = "tenant-b",
        PrincipalReference = "principal-b"
    });
    Assert(otherTenant.TenantReference == "tenant-b",
        "An active attempt leaked across tenant isolation.");

    var restarted = new FileSubmissionAttemptStore(options);
    var reloaded = await restarted.GetAsync("tenant-a", "principal-a", reserved.AttemptReference);
    Assert(reloaded == reserved, "The durable attempt was not retrievable after store restart.");
    Assert(await restarted.GetAsync("tenant-b", "principal-a", reserved.AttemptReference) is null
        && await restarted.GetAsync("tenant-a", "principal-b", reserved.AttemptReference) is null,
        "Attempt retrieval crossed a tenant or principal boundary.");

    var unknown = await restarted.RecordOutcomeAsync("tenant-a", "principal-a", reserved.AttemptReference,
        new(SubmissionAttemptState.Unknown, "NETWORK-OUTCOME-UNKNOWN", CorrelationReference: "correlation-a",
            SafeResponseReference: "response/0001"));
    Assert(unknown.IsActive && unknown.State == SubmissionAttemptState.Unknown,
        "An unknown write outcome did not remain active against duplicate submission.");
    await AssertRejectedAsync(() => restarted.ReserveAsync(reservation),
        "An unknown write outcome allowed a duplicate reservation.");
    await AssertRejectedAsync(() => restarted.RecordOutcomeAsync("tenant-a", "principal-a",
        reserved.AttemptReference, new(SubmissionAttemptState.Succeeded, "OK",
            SafeResponseReference: "https://authority.example/untrusted")),
        "An arbitrary authority-returned URL was persisted.");
    await AssertRejectedAsync(() => restarted.RecordOutcomeAsync("tenant-a", "principal-a",
        reserved.AttemptReference, new(SubmissionAttemptState.Failed, new string('X', 129))),
        "An oversized outcome was persisted.");
    var reconciled = await restarted.RecordOutcomeAsync("tenant-a", "principal-a", reserved.AttemptReference,
        new(SubmissionAttemptState.Succeeded, "ACCEPTED", 201, "correlation-a", "response/0001"));
    Assert(!reconciled.IsActive && reconciled.State == SubmissionAttemptState.Succeeded,
        "An unknown attempt could not be reconciled to a terminal outcome.");
    await AssertRejectedAsync(() => restarted.RecordOutcomeAsync("tenant-a", "principal-a",
        reserved.AttemptReference, new(SubmissionAttemptState.Failed, "LATE-MUTATION")),
        "A terminal attempt outcome was mutated.");

    var read = new SubmissionAttemptReservation(
        "vat:obligations:2026", "vat.obligations.list", "GET", "tenant-a", "principal-a",
        "subject-period-a", null, null, AuthorityEnvironment.Sandbox);
    var readOne = await restarted.ReserveAsync(read);
    var readTwo = await restarted.ReserveAsync(read);
    Assert(readOne.AttemptReference != readTwo.AttemptReference,
        "Bounded REST read metadata was incorrectly treated as a duplicate write.");

    var contentStore = new FileSubmissionContentStore(new(Path.Combine(root, "protected-content"),
        MaximumPayloadBytes: 1024, MaximumResponseBytes: 64));
    var responseBytes = "{\"code\":\"OK\"}"u8.ToArray();
    var responseReference = await contentStore.StoreAsync("tenant-a", "principal-a",
        SubmissionContentKind.Response, responseBytes);
    var responseAgain = await contentStore.ReadAsync("tenant-a", "principal-a", responseReference);
    Assert(responseAgain is not null && responseAgain.SequenceEqual(responseBytes),
        "Bounded protected response content did not survive storage.");
    Assert(await contentStore.ReadAsync("tenant-a", "principal-b", responseReference) is null,
        "Protected response content crossed a principal boundary.");
    await AssertRejectedAsync(() => contentStore.StoreAsync("tenant-a", "principal-a",
        SubmissionContentKind.Response, new byte[65]),
        "An oversized authority response was stored.");

    var log = new StringWriter();
    var logger = new SubmissionLogger(log);
    await logger.LogAsync(new("vat.returns.submit", "Succeeded", reserved.AttemptReference,
        "tenant-a", "principal-a", syntheticClientSecret, "Bearer token fraud credential"));
    var logged = log.ToString();
    Assert(!logged.Contains("tenant-a", StringComparison.Ordinal)
        && !logged.Contains("principal-a", StringComparison.Ordinal)
        && !logged.Contains(reserved.AttemptReference, StringComparison.Ordinal)
        && !logged.Contains(syntheticClientSecret, StringComparison.Ordinal)
        && !logged.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
        && logged.Contains("[REDACTED]", StringComparison.Ordinal),
        "Routine diagnostics leaked an identifier, credential, token or fraud value.");

    var stored = await File.ReadAllTextAsync(metadataPath);
    Assert(!stored.Contains(syntheticClientId, StringComparison.Ordinal)
        && !stored.Contains(syntheticClientSecret, StringComparison.Ordinal)
        && !stored.Contains("evil.example", StringComparison.Ordinal),
        "The attempt metadata store captured secret-provider content or an untrusted host.");
    Assert(typeof(PreparedApiRequest).GetProperties().All(property =>
            property.PropertyType != typeof(AuthorityEnvironment)
            && !property.Name.Contains("Environment", StringComparison.OrdinalIgnoreCase)),
        "Prepared content can activate an authority environment.");
    assertions += await OAuthTests.RunAsync(root, provider);
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

Console.WriteLine($"Submission adapter foundation tests passed ({assertions} assertions).");
