using System.Xml;
using System.Xml.Linq;

namespace TradeControl.Tax.UK.Company.ContractInfrastructure;

public static class SecureXml
{
    public static XDocument Parse(Stream input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}
