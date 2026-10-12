using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

internal static class CompaniesHouseMaterializerTests
{
    public static async Task<int> RunAsync(string root)
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        const string presenterId = "SYNTHETIC-PRESENTER";
        const string presenterAuthentication = "SYNTHETIC-AUTHENTICATION";
        const string companyAuthentication = "ABC123";
        var testAccountPath = Path.Combine(root, "companies-house-test-account.txt");
        await File.WriteAllTextAsync(testAccountPath, $"""
            Test Presenter ID: {presenterId}
            Authentication Value: {presenterAuthentication}
            Test Flag: 1
            Test Package Reference: 0012
            """);
        var companySettingsPath = Path.Combine(root, "companies-house-company.json");
        await File.WriteAllTextAsync(companySettingsPath, JsonSerializer.Serialize(new
        {
            companyAuthentication
        }));
        var testAccountProvider = new LabelledTextFileSecretProvider(testAccountPath,
            CompaniesHouseSecretReferences.TestAccountFileLabels);
        var companyProvider = new JsonFileSecretProvider(companySettingsPath,
            new Dictionary<SecretReference, string>
            {
                [CompaniesHouseSecretReferences.CompanyAuthentication] = "companyAuthentication"
            });
        var provider = new RoutedSecretProvider(new Dictionary<SecretReference, ISecretProvider>
        {
            [CompaniesHouseSecretReferences.TestPresenterId] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestPresenterAuthentication] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestFlag] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestPackageReference] = testAccountProvider,
            [CompaniesHouseSecretReferences.CompanyAuthentication] = companyProvider
        });
        var package = Package();
        var materialized = await new CompaniesHouseEnvelopeMaterializer(provider)
            .MaterializeTestAsync(package, "2026100600000001");

        Assert(materialized.EvidenceClass == CompaniesHouseMaterializedEnvelope.MaterializedEvidence
            && materialized.PreviewSha256 == package.Transmission.Sha256
            && materialized.WireSha256 == Convert.ToHexString(SHA256.HashData(materialized.Content.AsSpan()))
            && materialized.AccountsSha256 == package.Documents.Single().Artifact.Sha256,
            "The materialised envelope evidence does not preserve preview, wire and accounts identity.");
        Assert(package.Transmission.Content.AsSpan().SequenceEqual(Package().Transmission.Content.AsSpan()),
            "Credential materialisation mutated the immutable prepared preview.");

        var xmlText = Encoding.UTF8.GetString(materialized.Content.AsSpan());
        var document = XDocument.Parse(xmlText);
        XNamespace govTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
        XNamespace form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
        string Required(string localName, XNamespace xmlNamespace) => document.Descendants(xmlNamespace + localName)
            .Single().Value;
        Assert(Required("TransactionID", govTalk) == "2026100600000001"
            && Required("Class", govTalk) == "Accounts"
            && Required("GatewayTest", govTalk) == "1"
            && Required("Method", govTalk) == "clear"
            && Required("FormIdentifier", form) == "Accounts",
            "The official test transaction fields were not placed in the GovTalk header.");
        Assert(Required("SenderID", govTalk) == Md5(presenterId)
            && Required("Value", govTalk) == Md5(presenterAuthentication)
            && !xmlText.Contains(presenterId, StringComparison.Ordinal)
            && !xmlText.Contains(presenterAuthentication, StringComparison.Ordinal),
            "Presenter credentials were not MD5 encoded at the final transport boundary.");
        Assert(Required("CompanyAuthenticationCode", form) == companyAuthentication
            && Required("PackageReference", form) == "0012",
            "The protected company authentication or allocated package reference was not materialised.");

        var decodedAccounts = Convert.FromBase64String(Required("Data", form));
        Assert(decodedAccounts.AsSpan().SequenceEqual(package.Documents.Single().Artifact.Content.AsSpan()),
            "Materialisation changed the exact prepared iXBRL attachment bytes.");
        Assert(!xmlText.Contains("UNALLOCATED", StringComparison.Ordinal)
            && !typeof(CompaniesHouseMaterializedEnvelope).GetProperties().Any(property =>
                property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Presenter", StringComparison.OrdinalIgnoreCase)),
            "The safe materialisation result exposes unresolved or protected configuration fields.");
        Assert(typeof(CompaniesHouseEnvelopeSerializer).Assembly.GetType(
                "TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0.CompaniesHouseTransportInputs") is null,
            "Raw Companies House credentials remain available through an ordinary record type.");

        var reviewPreview = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(package.Transmission.Content.AsSpan())
            .Replace(">S00001<", ">000000<", StringComparison.Ordinal));
        var reviewPreviewSha = Convert.ToHexString(SHA256.HashData(reviewPreview));
        var retainedAccounts = package.Documents.Single().Artifact;
        var retainedMaterialized = await new CompaniesHouseEnvelopeMaterializer(provider)
            .MaterializeRetainedTestAsync(reviewPreview, reviewPreviewSha,
                retainedAccounts.Content.ToArray(), retainedAccounts.Sha256,
                "S00017", "2026101200000017");
        var retainedXml = XDocument.Parse(Encoding.UTF8.GetString(retainedMaterialized.Content.AsSpan()));
        Assert(retainedMaterialized.PreviewSha256 == reviewPreviewSha
            && retainedMaterialized.AccountsSha256 == retainedAccounts.Sha256
            && retainedXml.Descendants(form + "SubmissionNumber").Single().Value == "S00017"
            && retainedXml.Descendants(govTalk + "TransactionID").Single().Value == "2026101200000017"
            && Convert.FromBase64String(retainedXml.Descendants(form + "Data").Single().Value)
                .AsSpan().SequenceEqual(retainedAccounts.Content.AsSpan()),
            "Retained approved evidence did not materialise with only transport-owned identity and credentials changed.");

        var statusRequest = await new CompaniesHouseEnvelopeMaterializer(provider)
            .MaterializeStatusRequestTestAsync(CompaniesHouseStatusQueryScope.Submission,
                "2026100600000003", submissionNumber: "S00001");
        var statusXml = Encoding.UTF8.GetString(statusRequest.Content.AsSpan());
        var statusDocument = XDocument.Parse(statusXml);
        string StatusRequired(string localName, XNamespace xmlNamespace) => statusDocument
            .Descendants(xmlNamespace + localName).Single().Value;
        Assert(statusRequest.Operation == CompaniesHouseGatewayOperation.GetSubmissionStatus
            && statusRequest.EvidenceClass == CompaniesHouseMaterializedGatewayRequest.MaterializedEvidence
            && statusRequest.WireSha256 == Convert.ToHexString(SHA256.HashData(statusRequest.Content.AsSpan())),
            "The status request was not returned as safe materialised, unsent evidence.");
        Assert(StatusRequired("TransactionID", govTalk) == "2026100600000003"
            && StatusRequired("GatewayTest", govTalk) == "1"
            && StatusRequired("SenderID", govTalk) == Md5(presenterId)
            && StatusRequired("Value", govTalk) == Md5(presenterAuthentication),
            "The status request does not carry the official GovTalk test authentication header.");
        Assert(StatusRequired("SubmissionNumber", CompaniesHouseEnvelopeSerializer.StatusNamespace) == "S00001"
            && StatusRequired("PresenterID", CompaniesHouseEnvelopeSerializer.StatusNamespace) == presenterId
            && !statusDocument.Descendants(govTalk + "Function").Any()
            && !statusXml.Contains(presenterAuthentication, StringComparison.Ordinal),
            "The status request does not preserve the published control-request shape or exposes clear authentication.");

        var statusAcknowledgement = await new CompaniesHouseEnvelopeMaterializer(provider)
            .MaterializeStatusAcknowledgementTestAsync("2026100600000004");
        var acknowledgementXml = Encoding.UTF8.GetString(statusAcknowledgement.Content.AsSpan());
        var acknowledgementDocument = XDocument.Parse(acknowledgementXml);
        var acknowledgementBody = acknowledgementDocument.Descendants(
            XName.Get("StatusAck", CompaniesHouseEnvelopeSerializer.StatusNamespace)).Single();
        Assert(statusAcknowledgement.Operation == CompaniesHouseGatewayOperation.StatusAck
            && !acknowledgementBody.HasElements && string.IsNullOrWhiteSpace(acknowledgementBody.Value)
            && !acknowledgementDocument.Descendants(govTalk + "Function").Any(),
            "The protected StatusAck request does not preserve the published empty body.");
        Assert(acknowledgementDocument.Descendants(govTalk + "TransactionID").Single().Value == "2026100600000004"
            && acknowledgementDocument.Descendants(govTalk + "GatewayTest").Single().Value == "1"
            && acknowledgementDocument.Descendants(govTalk + "SenderID").Single().Value == Md5(presenterId)
            && acknowledgementDocument.Descendants(govTalk + "Value").Single().Value == Md5(presenterAuthentication)
            && !acknowledgementXml.Contains(presenterAuthentication, StringComparison.Ordinal),
            "The StatusAck request does not carry the protected GovTalk test header.");

        var incompleteProvider = new RoutedSecretProvider(new Dictionary<SecretReference, ISecretProvider>
        {
            [CompaniesHouseSecretReferences.TestPresenterId] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestPresenterAuthentication] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestFlag] = testAccountProvider,
            [CompaniesHouseSecretReferences.TestPackageReference] = testAccountProvider
        });
        try
        {
            await new CompaniesHouseEnvelopeMaterializer(incompleteProvider)
                .MaterializeTestAsync(package, "2026100600000002");
            throw new InvalidOperationException("A missing company authentication code was accepted.");
        }
        catch (KeyNotFoundException exception) when (exception.Message.Contains("no approved provider route",
                   StringComparison.Ordinal))
        {
            assertions++;
        }

        var statusWithoutCompanyAuthentication = await new CompaniesHouseEnvelopeMaterializer(incompleteProvider)
            .MaterializeStatusRequestTestAsync(CompaniesHouseStatusQueryScope.Presenter, "2026100600000005");
        Assert(statusWithoutCompanyAuthentication.Content.Length > 0,
            "Status polling incorrectly depends on company authentication or a package reference.");

        Assert(typeof(CompaniesHouseEnvelopeMaterializer).GetConstructors().All(constructor =>
                constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(HttpClient))),
            "Credential materialisation unexpectedly acquired a network client.");
        return assertions;
    }

    private static PreparedSubmissionPackage Package()
    {
        XNamespace govTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
        XNamespace form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
        var accountsBytes = Encoding.UTF8.GetBytes(
            "<html xmlns='http://www.w3.org/1999/xhtml'><body>EXACT PREPARED ACCOUNTS</body></html>");
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(govTalk + "GovTalkMessage",
                new XElement(govTalk + "EnvelopeVersion", "1.0"),
                new XElement(govTalk + "Header", new XElement(govTalk + "MessageDetails",
                    new XElement(govTalk + "Class", "Accounts"),
                    new XElement(govTalk + "Qualifier", "request"),
                    new XElement(govTalk + "Function", "submit"))),
                new XElement(govTalk + "GovTalkDetails"),
                new XElement(govTalk + "Body", new XElement(form + "FormSubmission",
                    new XElement(form + "FormHeader",
                        new XElement(form + "CompanyNumber", "01234567"),
                        new XElement(form + "CompanyType", "EW"),
                        new XElement(form + "CompanyName", "MATERIALISER TEST LIMITED"),
                        new XElement(form + "PackageReference", "UNALLOCATED"),
                        new XElement(form + "Language", "EN"),
                        new XElement(form + "FormIdentifier", "Accounts"),
                        new XElement(form + "SubmissionNumber", "S00001")),
                    new XElement(form + "DateSigned", "2026-10-06"),
                    new XElement(form + "Form"),
                    new XElement(form + "Document",
                        new XElement(form + "Data", Convert.ToBase64String(accountsBytes)),
                        new XElement(form + "Date", "2026-10-06"),
                        new XElement(form + "Filename", "accounts.xhtml"),
                        new XElement(form + "ContentType", "application/xml"),
                        new XElement(form + "Category", "ACCOUNTS"))))));
        byte[] transmissionBytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                   { Encoding = new UTF8Encoding(false), Indent = false }))
                xml.Save(writer);
            transmissionBytes = stream.ToArray();
        }
        var findings = new[]
        {
            new PreparedArtifactFinding(PreparedFindingSeverity.Error, "CH-PENDING-CREDENTIALS",
                "Transport-time inputs remain pending."),
            new PreparedArtifactFinding(PreparedFindingSeverity.Error, "CH-PENDING-DEVELOPER-TEST",
                "Developer-test evidence remains pending.")
        };
        var transmission = PreparedStatutoryArtifact.Create("GB", "COMPANIES-HOUSE",
            "FILE-COMPANY-ACCOUNTS", "TIS-6.0", PreparedArtifactStatus.Preview,
            "application/xml", transmissionBytes, findings: findings);
        var accounts = PreparedStatutoryArtifact.Create("GB", "COMPANIES-HOUSE",
            "STATUTORY-ACCOUNTS", "FRC-2026", PreparedArtifactStatus.Preview,
            "application/xhtml+xml", accountsBytes);
        return new("COMPANIES-HOUSE-ACCOUNTS", transmission,
            [new("accounts.xhtml", accounts)],
            new(SubmissionPollingMode.CompaniesHouseXmlGateway, "GetSubmissionStatus"));
    }

    private static string Md5(string value) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
