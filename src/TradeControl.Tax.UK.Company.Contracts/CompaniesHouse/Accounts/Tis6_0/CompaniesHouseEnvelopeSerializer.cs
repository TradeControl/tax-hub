using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

/// <summary>Produces credential-free structural previews of the published XML contracts.</summary>
public sealed class CompaniesHouseEnvelopeSerializer
{
    public const string GovTalkNamespace = "http://www.govtalk.gov.uk/CM/envelope";
    public const string FormSubmissionNamespace = "http://xmlgw.companieshouse.gov.uk/Header";
    public const string StatusNamespace = "http://xmlgw.companieshouse.gov.uk";
    private static readonly XNamespace GovTalk = GovTalkNamespace;
    private static readonly XNamespace FormSubmission = FormSubmissionNamespace;
    private static readonly XNamespace Status = StatusNamespace;

    public DocumentArtifact Serialize(CompaniesHouseFilingPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var filing = package.Filing;
        var form = new XElement(FormSubmission + "FormSubmission",
            new XElement(FormSubmission + "FormHeader",
                new XElement(FormSubmission + "CompanyNumber", filing.Accounts.Company.CompanyNumber),
                new XElement(FormSubmission + "CompanyType", "EW"),
                new XElement(FormSubmission + "CompanyName", filing.Accounts.Company.CompanyName),
                new XElement(FormSubmission + "PackageReference", package.PackageReference),
                new XElement(FormSubmission + "Language", "EN"),
                new XElement(FormSubmission + "FormIdentifier", "Accounts"),
                new XElement(FormSubmission + "SubmissionNumber", package.SubmissionNumber)),
            new XElement(FormSubmission + "DateSigned", filing.Accounts.Approval.ApprovedOn.ToString("yyyy-MM-dd")),
            new XElement(FormSubmission + "Form"),
            new XElement(FormSubmission + "Document",
                new XElement(FormSubmission + "Data", Convert.ToBase64String(filing.AccountsDocument.Content)),
                new XElement(FormSubmission + "Date", filing.Accounts.Approval.ApprovedOn.ToString("yyyy-MM-dd")),
                new XElement(FormSubmission + "Filename", filing.AccountsDocument.FileName),
                new XElement(FormSubmission + "ContentType", "application/xml"),
                new XElement(FormSubmission + "Category", "ACCOUNTS")));
        return Artifact("companies-house-accounts-preview.xml", Envelope("Accounts", form, includeSubmitFunction: true));
    }

    public DocumentArtifact SerializeStatusRequest(CompaniesHouseSubmissionStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PresenterId)
            || request.Scope == CompaniesHouseStatusQueryScope.Submission && request.SubmissionNumber?.Length != 6
            || request.Scope == CompaniesHouseStatusQueryScope.Company && string.IsNullOrWhiteSpace(request.CompanyNumber))
            throw new ArgumentException("The selected status-query scope requires its published identifier fields.", nameof(request));
        var body = new XElement(Status + "GetSubmissionStatus");
        if (request.Scope == CompaniesHouseStatusQueryScope.Submission)
            body.Add(new XElement(Status + "SubmissionNumber", request.SubmissionNumber));
        else if (request.Scope == CompaniesHouseStatusQueryScope.Company)
            body.Add(new XElement(Status + "CompanyNumber", request.CompanyNumber));
        body.Add(new XElement(Status + "PresenterID", request.PresenterId));
        return Artifact("companies-house-status-preview.xml", Envelope("GetSubmissionStatus", body));
    }

    public DocumentArtifact SerializeStatusAcknowledgement() =>
        Artifact("companies-house-status-ack-preview.xml", Envelope("StatusAck", new XElement(Status + "StatusAck")));

    public IReadOnlyList<CompaniesHouseSubmissionStatus> ParseSubmissionStatuses(ReadOnlySpan<byte> xml)
    {
        using var stream = new MemoryStream(xml.ToArray());
        var document = SecureXml.Parse(stream);
        return document.Descendants(Status + "Status").Select(element => new CompaniesHouseSubmissionStatus(
            Required(element, "SubmissionNumber"), ParseStatusCode(Required(element, "StatusCode")),
            Optional(element, "CompanyNumber"), Optional(element, "CustomerReference"),
            element.Descendants(Status + "Reject").Select(reject => new CompaniesHouseError(
                Required(reject, "RejectCode"), Required(reject, "Description"), Optional(reject, "InstanceNumber"))).ToArray())).ToArray();
    }

    public CompaniesHouseGatewayAcknowledgement ParseGatewayAcknowledgement(ReadOnlySpan<byte> xml)
    {
        using var stream = new MemoryStream(xml.ToArray());
        var document = SecureXml.Parse(stream);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "GovTalkMessage")
            throw new InvalidDataException("The Companies House response is not a GovTalk message.");
        var qualifier = root.Descendants().SingleOrDefault(element => element.Name.LocalName == "Qualifier")?.Value;
        if (qualifier is not ("acknowledgement" or "error" or "response"))
            throw new InvalidDataException("The Companies House response qualifier is unsupported.");
        var transactionId = LocalValue(root, "TransactionID");
        var timestampText = LocalValue(root, "GatewayTimestamp");
        DateTimeOffset? timestamp = null;
        if (!string.IsNullOrWhiteSpace(timestampText))
        {
            if (!DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new InvalidDataException("The Companies House gateway timestamp is invalid.");
            timestamp = parsed;
        }
        var errors = root.Descendants().Where(element => element.Name.LocalName == "Error")
            .Select(error => new CompaniesHouseError(
                LocalRequired(error, "Number"),
                LocalRequired(error, "Text"),
                LocalValue(error, "Location")))
            .ToArray();
        if (qualifier == "error" && errors.Length == 0)
            throw new InvalidDataException("The Companies House error response contains no error detail.");
        return new(transactionId, timestamp, null, errors);
    }

    private static XDocument Envelope(string messageClass, XElement body, bool includeSubmitFunction = false)
    {
        var messageDetails = new XElement(GovTalk + "MessageDetails",
            new XElement(GovTalk + "Class", messageClass),
            new XElement(GovTalk + "Qualifier", "request"));
        if (includeSubmitFunction)
            messageDetails.Add(new XElement(GovTalk + "Function", "submit"));
        return new(new XDeclaration("1.0", "utf-8", null),
            new XElement(GovTalk + "GovTalkMessage",
                new XElement(GovTalk + "EnvelopeVersion", "1.0"),
                new XElement(GovTalk + "Header", messageDetails),
                new XElement(GovTalk + "GovTalkDetails"), new XElement(GovTalk + "Body", body)));
    }

    private static DocumentArtifact Artifact(string fileName, XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        return new(fileName, "application/xml", bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string Required(XElement parent, string name) => parent.Element(Status + name)?.Value
        ?? throw new InvalidDataException($"Missing {name}.");
    private static string? Optional(XElement parent, string name) => parent.Element(Status + name)?.Value;
    private static string? LocalValue(XElement parent, string name) => parent.Descendants()
        .FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    private static string LocalRequired(XElement parent, string name) => LocalValue(parent, name)
        ?? throw new InvalidDataException($"Missing {name}.");
    private static CompaniesHouseSubmissionStatusCode ParseStatusCode(string value) => value switch
    {
        "ACCEPT" => CompaniesHouseSubmissionStatusCode.Accept,
        "REJECT" => CompaniesHouseSubmissionStatusCode.Reject,
        "PENDING" => CompaniesHouseSubmissionStatusCode.Pending,
        "PARKED" => CompaniesHouseSubmissionStatusCode.Parked,
        "INTERNAL_FAILURE" => CompaniesHouseSubmissionStatusCode.InternalFailure,
        _ => throw new InvalidDataException($"Unknown Companies House submission status '{value}'.")
    };
}
