using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

/// <summary>
/// Deterministic semantic package serialization used by offline reconciliation tests.
/// The submission adapter must project through the generated RIM 1.994 graph and
/// official computation-taxonomy assets; this diagnostic form is not a gateway payload.
/// </summary>
public sealed class CorporationTaxPackageSerializer
{
    private static readonly XNamespace Ct = "http://www.govtalk.gov.uk/taxation/CT/5";

    public DocumentArtifact Serialize(CorporationTaxReturnPackage package, string correlationId)
    {
        var r = package.Return;
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(Ct + "IRenvelope",
                new XElement(Ct + "IRheader", new XElement(Ct + "Keys",
                    new XElement(Ct + "Key", new XAttribute("Type", "UTR"), r.Utr)),
                    new XElement(Ct + "PeriodEnd", r.Period.End.ToString("yyyy-MM-dd")),
                    new XElement(Ct + "CorrelationID", correlationId)),
                new XElement(Ct + "CompanyTaxReturn",
                    new XElement(Ct + "CompanyInformation",
                        new XElement(Ct + "CompanyName", r.CompanyName),
                        new XElement(Ct + "RegistrationNumber", r.CompanyRegistrationNumber),
                        new XElement(Ct + "PeriodCovered", new XElement(Ct + "From", r.Period.Start.ToString("yyyy-MM-dd")), new XElement(Ct + "To", r.Period.End.ToString("yyyy-MM-dd")))),
                    new XElement(Ct + "ReturnInfoSummary",
                        new XElement(Ct + "Turnover", r.Turnover),
                        new XElement(Ct + "ProfitBeforeTax", r.ProfitBeforeTax),
                        new XElement(Ct + "TaxableTotalProfits", r.TaxableTotalProfits),
                        new XElement(Ct + "CorporationTaxChargeable", r.CorporationTaxChargeable),
                        new XElement(Ct + "TaxPayable", r.TaxPayable),
                        r.SupplementaryPageA is null ? null : new XElement(Ct + "CT600A",
                            new XElement(Ct + "LoansOutstanding", r.SupplementaryPageA.LoansOutstandingAtPeriodEnd),
                            new XElement(Ct + "TaxChargeable", r.SupplementaryPageA.TaxChargeable),
                            new XElement(Ct + "TaxPaid", r.SupplementaryPageA.TaxPaid))),
                    new XElement(Ct + "Accounts", Convert.ToBase64String(package.AccountsDocument.Content)),
                    new XElement(Ct + "Computations", Convert.ToBase64String(package.ComputationDocument.Content)),
                    new XElement(Ct + "Declaration",
                        new XElement(Ct + "Name", r.DeclarantName),
                        new XElement(Ct + "Date", r.DeclarationDate.ToString("yyyy-MM-dd"))))));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        return new("corporation-tax-return.xml", "application/xml", bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
