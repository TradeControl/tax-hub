using System.Text.RegularExpressions;
using System.Xml.Linq;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

var root = FindRoot(AppContext.BaseDirectory);
var source = Path.Combine(root, "src");
var projects = Directory.GetFiles(source, "*.csproj", SearchOption.AllDirectories);

foreach (var contract in projects.Where(path => Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.Ordinal)))
{
    var references = ProjectReferences(contract);
    Assert(references.Count == 0,
        $"Contract project '{Path.GetFileName(contract)}' must not reference Application, adapters, SQL or ASP.NET Core projects.");
}

var application = projects.Single(path => Path.GetFileName(path) == "TradeControl.Tax.UK.Application.csproj");
Assert(ProjectReferences(application).All(reference => reference.Contains(".Contracts", StringComparison.Ordinal)),
    "Application may reference only contract projects.");
Assert(!SourceContains(Path.GetDirectoryName(application)!,
        "Microsoft.Data.SqlClient", "System.Data.SqlClient", "Microsoft.AspNetCore"),
    "Application contains a prohibited SQL or ASP.NET Core dependency.");

var preparation = Path.Combine(Path.GetDirectoryName(application)!, "Preparation");
Assert(!SourceContains(preparation, "HttpClient", "Bearer ", "AccessToken", "ClientSecret",
        "Adapters.Submission", "Microsoft.AspNetCore"),
    "Objective 3 preparation code contains a transport, authentication or submission-adapter concern.");

var controllers = Path.Combine(source, "TradeControl.Tax.UK.WebHarness", "Controllers");
foreach (var name in new[]
{
    "VatPreparationController.cs", "CumulativePreparationController.cs", "BodylessRequestDescriptionController.cs"
})
{
    var text = File.ReadAllText(Path.Combine(controllers, name));
    Assert(!Regex.IsMatch(text, @"\b(SqlConnection|SqlCommand|Tc[A-Z][A-Za-z0-9_]*)\b")
        && !text.Contains("SaJson.Serialize", StringComparison.Ordinal)
        && !text.Contains("VatJson.Serialize", StringComparison.Ordinal)
        && !text.Contains("Adapters.Submission", StringComparison.Ordinal),
        $"Objective 3 controller '{name}' contains SQL/Tc rows, contract serialization or submission transport.");
}

var web = projects.Single(path => Path.GetFileName(path) == "TradeControl.Tax.UK.WebHarness.csproj");
Assert(ProjectReferences(web).Any(reference => reference.Contains("Adapters.Submission", StringComparison.Ordinal)),
    "The retained legacy submission dependency changed without an explicit deprecation review.");
var objectiveThreeControllers = new[]
{
    "VatPreparationController.cs", "CumulativePreparationController.cs", "BodylessRequestDescriptionController.cs"
};
Assert(objectiveThreeControllers.All(name => !File.ReadAllText(Path.Combine(controllers, name))
        .Contains("HmrcSubmissionRunner", StringComparison.Ordinal)),
    "An Objective 3 controller can invoke the retained legacy submission runner.");

var submission = projects.Single(path => Path.GetFileName(path) == "TradeControl.Tax.UK.Adapters.Submission.csproj");
Assert(ProjectReferences(submission).Count == 1
    && ProjectReferences(submission).Single().Contains(".Application", StringComparison.Ordinal),
    "The submission adapter must depend only on the Application boundary.");
var submissionDirectory = Path.GetDirectoryName(submission)!;
Assert(!SourceContains(Path.Combine(submissionDirectory, "Audit"), "HttpClient", "AuthorizationCode", "HttpRequestMessage")
    && !SourceContains(Path.Combine(submissionDirectory, "Configuration"), "HttpClient", "AuthorizationCode", "HttpRequestMessage"),
    "OAuth transport escaped the dedicated submission OAuth boundary.");
Assert(!SourceContains(Path.Combine(submissionDirectory, "OAuth"), "/organisations/vat/", "VatJson.Serialize", "Fraud-Prevention"),
    "Phase 5.2 introduced a VAT resource call, VAT body or fraud-header behaviour.");
var fraudPrevention = Path.Combine(submissionDirectory, "FraudPrevention");
Assert(!SourceContains(fraudPrevention, "Microsoft.AspNetCore", "X-Forwarded-For", "VatJson.Serialize",
        "SaJson.SerializeCanonical", "/organisations/vat/", "Authorization: Bearer"),
    "The fraud-prevention boundary trusts HTTP headers directly or contains submission/body concerns.");
var diagnosticFactoryConsumers = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.StartsWith(fraudPrevention, StringComparison.OrdinalIgnoreCase))
    .Where(path => File.ReadAllText(path).Contains("CreateFileBackedSandboxValidatorDiagnostic",
        StringComparison.Ordinal))
    .ToArray();
Assert(diagnosticFactoryConsumers.Length == 1
    && diagnosticFactoryConsumers[0].Contains("TradeControl.Tax.UK.WebHarness", StringComparison.Ordinal),
    "The relaxed sandbox-validator formatter escaped the development WebHarness diagnostic.");

Console.WriteLine($"Tax Hub architecture tests passed ({assertions} assertions).");

static string FindRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "src", "TaxHub.slnx"))) return directory.FullName;
    throw new DirectoryNotFoundException("Could not locate the Tax Hub repository root.");
}

static IReadOnlyList<string> ProjectReferences(string project)
{
    var document = XDocument.Load(project);
    return document.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
        .Select(element => (string?)element.Attribute("Include") ?? string.Empty).ToArray();
}

static bool SourceContains(string directory, params string[] terms) => Directory
    .GetFiles(directory, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    .Select(File.ReadAllText)
    .Any(text => terms.Any(term => text.Contains(term, StringComparison.Ordinal)));
