using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;

/// <summary>
/// Deterministic logical filing-package serialization for offline review.
/// This is deliberately not marked submission-ready: TIS 5.9 refers envelope
/// implementers to the separate Filing TIS schemas, which are not provisioned here.
/// </summary>
public sealed class CompaniesHouseEnvelopeSerializer
{
    private static readonly XNamespace GovTalk = "http://www.govtalk.gov.uk/CM/envelope";
    private static readonly XNamespace Accounts = "http://xmlgw.companieshouse.gov.uk/v1-0/schema/forms/CompanyAccounts-v1-0";

    public DocumentArtifact Serialize(CompaniesHouseFilingPackage package)
    {
        var filing = package.Filing;
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(GovTalk + "GovTalkMessage",
                new XElement(GovTalk + "EnvelopeVersion", "2.0"),
                new XElement(GovTalk + "Header", new XElement(GovTalk + "MessageDetails",
                    new XElement(GovTalk + "Class", "CompanyAccounts"),
                    new XElement(GovTalk + "Qualifier", "request"),
                    new XElement(GovTalk + "Function", "submit"),
                    new XElement(GovTalk + "CorrelationID", package.EnvelopeNumber))),
                new XElement(GovTalk + "Body", new XElement(Accounts + "CompanyAccounts",
                    new XElement(Accounts + "CompanyNumber", filing.Accounts.Company.CompanyNumber),
                    new XElement(Accounts + "AccountsPeriodStart", filing.Accounts.Period.Start.ToString("yyyy-MM-dd")),
                    new XElement(Accounts + "AccountsPeriodEnd", filing.Accounts.Period.End.ToString("yyyy-MM-dd")),
                    new XElement(Accounts + "AccountsType", "MicroEntity"),
                    new XElement(Accounts + "Delivery", filing.Delivery.ToString()),
                    new XElement(Accounts + "AccountsData", Convert.ToBase64String(filing.AccountsDocument.Content))))));

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        return new("companies-house-accounts.xml", "application/xml", bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
