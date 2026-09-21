using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Hmrc.Vat;
using TradeControl.Tax.UK.WebHarness.Controllers;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

PreparedApiRequest Request(string marker)
{
    var descriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.returns.submit");
    return new PreparedApiRequestPipeline().Prepare(HmrcPreparedApiContracts.From(descriptor),
        [new("vrn", "123456789")], serializeBody: () => Encoding.UTF8.GetBytes($"{{\"marker\":\"{marker}\"}}"));
}

var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
var store = new PreparedApiRequestStore(new(2, TimeSpan.FromMinutes(5)), time);
var originalFirst = Request("first");
var first = await store.SaveAsync(originalFirst);
var second = await store.SaveAsync(Request("second"));
Assert(store.TryGet(first, out var firstRequest) && ReferenceEquals(firstRequest, originalFirst),
    "A stored preparation could not be retrieved.");
var thirdRequest = Request("third");
var third = await store.SaveAsync(thirdRequest);
Assert(!store.TryGet(first, out _) && store.TryGet(second, out _) && store.TryGet(third, out var retrieved)
    && ReferenceEquals(retrieved, thirdRequest),
    "The bounded store did not evict only its oldest preparation.");
Assert(!store.TryGet("../../not-an-id", out _) && !store.TryGet(new string('z', 32), out _),
    "The store accepted a non-opaque preparation identifier.");
time.Advance(TimeSpan.FromMinutes(5));
Assert(!store.TryGet(second, out _) && !store.TryGet(third, out _),
    "Expired preparations remained available.");

var request = Request("exact-bytes");
var context = new DefaultHttpContext();
context.Response.Body = new MemoryStream();
await PreparedApiRequestHttp.WriteBodyAsync(context.Response, request);
Assert(context.Response.StatusCode == StatusCodes.Status200OK
    && context.Response.ContentType == "application/json"
    && context.Response.Headers["X-TaxHub-Preview"] == "true"
    && context.Response.ContentLength == request.BodyBytes!.Value.Length,
    "Raw response metadata is incorrect.");
Assert(context.Response.Body is MemoryStream stream
    && stream.ToArray().AsSpan().SequenceEqual(request.BodyBytes!.Value.AsSpan()),
    "Raw response bytes were wrapped, reformatted or reserialized.");

var inspectionNames = typeof(PreparedApiRequestInspection).GetProperties().Select(property => property.Name).ToArray();
var forbidden = new[] { "ConnectionString", "Credential", "Password", "Token", "Authorization", "Exception" };
Assert(forbidden.All(term => inspectionNames.All(name => !name.Contains(term, StringComparison.OrdinalIgnoreCase))),
    "The safe inspection DTO exposes a credential, connection or exception member.");
var inspectionJson = JsonSerializer.Serialize(PreparedApiRequestInspection.From(new string('a', 32), request));
Assert(!inspectionJson.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
    && !inspectionJson.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
    && !inspectionJson.Contains("exact-bytes", StringComparison.Ordinal),
    "Inspection serialization leaked source connection, credential or raw-body content.");
Assert(typeof(BodylessRequestDescription).GetProperties().All(property => property.Name != "Body"),
    "A bodyless description exposes a placeholder body.");

var objectiveThreeControllers = new[]
{
    typeof(VatPreparationController),
    typeof(CumulativePreparationController),
    typeof(BodylessRequestDescriptionController)
};
var dependencies = objectiveThreeControllers.SelectMany(type => type.GetFields(
    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)).Select(field => field.FieldType).ToArray();
Assert(dependencies.All(type => !type.FullName!.Contains("Adapters.Submission", StringComparison.Ordinal)
    && !type.Name.Contains("HmrcSubmissionRunner", StringComparison.Ordinal)),
    "An Objective 3 controller can resolve the legacy submission adapter or runner.");

var secret = "Server=private;Password=secret";
var problem = PreparedRequestProblem.FromException(new Exception(secret), "correlation-1");
var problemJson = JsonSerializer.Serialize(problem);
Assert(problem.Status == StatusCodes.Status500InternalServerError
    && problem.Extensions["correlationId"]?.ToString() == "correlation-1"
    && !problemJson.Contains(secret, StringComparison.Ordinal),
    "Unexpected failures are not converted to correlation-safe problem details.");

Console.WriteLine($"WebHarness hardening tests passed ({assertions} assertions).");

sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
{
    private DateTimeOffset _value = value;
    public override DateTimeOffset GetUtcNow() => _value;
    public void Advance(TimeSpan duration) => _value += duration;
}
