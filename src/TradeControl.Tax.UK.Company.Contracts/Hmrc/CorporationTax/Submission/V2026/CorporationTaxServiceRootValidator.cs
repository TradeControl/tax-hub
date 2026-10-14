using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TradeControl.Tax.UK.Company.Validation;

namespace TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

public sealed class CorporationTaxServiceRootValidator
{
    private const string SchemaResourceSuffix = ".Hmrc.CorporationTax.Ct600.V2026.Schemas.CT-2014-v1-994.xsd";
    private static readonly XNamespace Ct = Ct600.V2026.Ct600Contract.Namespace;

    public ContractValidationResult Validate(ReadOnlyMemory<byte> content)
    {
        var findings = new List<ValidationFinding>();
        try
        {
            var schemas = LoadSchemas();
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                ValidationType = ValidationType.Schema,
                Schemas = schemas,
                MaxCharactersInDocument = 50_000_000
            };
            settings.ValidationEventHandler += (_, args) => findings.Add(new(
                ValidationSeverity.Error, "CT600-XSD", args.Message,
                args.Exception?.LineNumber > 0 ? $"line {args.Exception.LineNumber}" : "/",
                "HMRC-CT600-V3-2026-RIM-1.994-XSD"));
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read()) { }

            var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(content.Span), LoadOptions.PreserveWhitespace);
            var irmarks = document.Descendants(Ct + "IRmark").ToArray();
            if (irmarks.Length != 1 || (string?)irmarks[0].Attribute("Type") != "generic" || !string.IsNullOrEmpty(irmarks[0].Value))
                findings.Add(new(ValidationSeverity.Error, "CT600-SERVICE-IRMARK",
                    "The service root must contain exactly one empty generic IRmark slot.",
                    "/IRenvelope/IRheader/IRmark", "HMRC-CT600-V3-2026-RIM-1.994"));

            var submission = document.Descendants(Ct + "XBRLsubmission").SingleOrDefault();
            if (submission is null || !submission.Elements().Select(x => x.Name.LocalName)
                    .SequenceEqual(["Computation", "Accounts"]))
                findings.Add(new(ValidationSeverity.Error, "CT600-SERVICE-XBRL",
                    "The service root must embed computation followed by accounts.",
                    "/IRenvelope/CompanyTaxReturn/AttachedFiles/XBRLsubmission", "HMRC-CT600-V3-2026-RIM-1.994"));
        }
        catch (Exception ex) when (ex is XmlException or XmlSchemaException or InvalidOperationException)
        {
            findings.Add(new(ValidationSeverity.Error, "CT600-SERVICE-XML", ex.Message, "/",
                "HMRC-CT600-V3-2026-RIM-1.994"));
        }

        return new(findings);
    }

    private static XmlSchemaSet LoadSchemas()
    {
        var assembly = typeof(CorporationTaxServiceRootValidator).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(SchemaResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The pinned CT600 RIM schema resource is unavailable.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var schemas = new XmlSchemaSet { XmlResolver = null };
        schemas.Add(Ct600.V2026.Ct600Contract.Namespace, reader);
        schemas.Compile();
        return schemas;
    }
}
