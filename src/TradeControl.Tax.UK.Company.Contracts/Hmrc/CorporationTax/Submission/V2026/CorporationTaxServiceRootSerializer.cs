using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using TradeControl.Tax.UK.Company.Xbrl;
using Ct = TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;
using CompanyTaxComputation = TradeControl.Tax.UK.Company.Statutory.CorporationTaxComputation;
using Rim = TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026.Generated;

namespace TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

/// <summary>
/// Projects the reviewed Corporation Tax package into the CT600 V3 (2026)
/// RIM 1.994 service root. GovTalk wrapping and IRmark calculation are transport
/// concerns and deliberately remain outside this serializer.
/// </summary>
public sealed class CorporationTaxServiceRootSerializer
{
    public const string FileName = "corporation-tax-service-root.xml";
    public const string OperationCode = "HMRC-CT600-SERVICE-ROOT";
    public const string ContractVersion = "CT600-V3-2026-RIM-1.994+CT-COMP-2025-V1.0.0";
    public const string EmptyIrmarkSlotXPath = "/ct:IRenvelope/ct:IRheader/ct:IRmark[@Type='generic' and not(text())]";

    public DocumentArtifact Serialize(CorporationTaxReturnPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var result = new Rim.IRenvelope
        {
            IRheader = Header(package),
            CompanyTaxReturn = Return(package)
        };

        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, Ct.Ct600Contract.Namespace);

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None
        };
        using (var writer = XmlWriter.Create(stream, settings))
            new XmlSerializer(typeof(Rim.IRenvelope)).Serialize(writer, result, namespaces);

        var bytes = stream.ToArray();
        return new(FileName, "application/xml", bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static Rim.IRheader Header(CorporationTaxReturnPackage package) => new()
    {
        Keys = [new Rim.IRheaderKey { Type = "UTR", Value = package.Return.Utr }],
        PeriodEnd = package.Return.Period.End.ToDateTime(TimeOnly.MinValue),
        DefaultCurrency = Rim.IRheaderDefaultCurrency.GBP,
        DefaultCurrencySpecified = true,
        Manifest = new Rim.IRheaderManifest
        {
            Contains =
            [
                new Rim.ContainsReference
                {
                    Namespace = Ct.Ct600Contract.Namespace,
                    SchemaVersion = "2014-v1.994",
                    TopElementName = "IRenvelope"
                }
            ]
        },
        IRmark = new Rim.IRheaderIRmark { Type = Rim.IRheaderIRmarkType.generic, Value = string.Empty },
        Sender = Rim.IRheaderSender.Company
    };

    private static Rim.IRenvelopeCompanyTaxReturn Return(CorporationTaxReturnPackage package)
    {
        var taxReturn = package.Return;
        var computation = package.Computation;
        var loans = taxReturn.SupplementaryPageA;
        var loansTaxOutstanding = loans is null ? 0m : Math.Max(0m, loans.TaxChargeable - loans.TaxPaid);
        var deductions = computation.Losses.Used > 0m
            ? new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationDeductionsAndReliefs
            {
                TradingLosses = Whole(computation.Losses.Used),
                Total = Whole(computation.Losses.Used)
            }
            : null;

        return new Rim.IRenvelopeCompanyTaxReturn
        {
            ReturnType = Rim.IRenvelopeCompanyTaxReturnReturnType.@new,
            CompanyInformation = new Rim.IRenvelopeCompanyTaxReturnCompanyInformation
            {
                CompanyName = taxReturn.CompanyName,
                RegistrationNumber = taxReturn.CompanyRegistrationNumber,
                Reference = taxReturn.Utr,
                CompanyType = "0",
                PeriodCovered = new Rim.IRenvelopeCompanyTaxReturnCompanyInformationPeriodCovered
                {
                    From = taxReturn.Period.Start.ToDateTime(TimeOnly.MinValue),
                    To = taxReturn.Period.End.ToDateTime(TimeOnly.MinValue)
                }
            },
            ReturnInfoSummary = Summary(loans is not null),
            Turnover = new Rim.IRenvelopeCompanyTaxReturnTurnover
            {
                Total = new Rim.IRenvelopeCompanyTaxReturnTurnoverTotal { Value = WholePounds(taxReturn.Turnover) }
            },
            CompanyTaxCalculation = new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculation
            {
                Income = TradingIncome(computation),
                ChargeableGains = computation.ChargeableGains > 0m
                    ? new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationChargeableGains
                    {
                        GrossGains = Whole(computation.ChargeableGains),
                        NetChargeableGains = Whole(computation.ChargeableGains)
                    }
                    : null,
                ProfitsBeforeOtherDeductions = Whole(computation.TaxableTotalProfits + computation.Losses.Used),
                DeductionsAndReliefs = deductions,
                ChargesAndReliefs = new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationChargesAndReliefs
                {
                    ProfitsBeforeDonationsAndGroupRelief = Whole(computation.TaxableTotalProfits)
                },
                ChargeableProfits = Whole(computation.TaxableTotalProfits),
                CorporationTaxChargeable = TaxChargeable(computation),
                CorporationTax = Pence(computation.CorporationTaxChargeable),
                NetCorporationTaxChargeable = Pence(computation.CorporationTaxChargeable)
            },
            CalculationOfTaxOutstandingOrOverpaid = new Rim.IRenvelopeCompanyTaxReturnCalculationOfTaxOutstandingOrOverpaid
            {
                NetCorporationTaxLiability = Pence(computation.TaxPayable),
                LoansToParticipators = loans is not null ? Pence(loansTaxOutstanding) : null,
                TaxChargeable = Pence(computation.TaxPayable + loansTaxOutstanding),
                TaxPayable = Pence(computation.TaxPayable + loansTaxOutstanding)
            },
            Declaration = new Rim.IRenvelopeCompanyTaxReturnDeclaration
            {
                AcceptDeclaration = Rim.IRenvelopeCompanyTaxReturnDeclarationAcceptDeclaration.yes,
                Name = taxReturn.DeclarantName,
                Status = "Director"
            },
            LoansByCloseCompanies = loans is null ? null : SupplementaryA(loans),
            AttachedFiles = new Rim.IRenvelopeCompanyTaxReturnAttachedFiles
            {
                Items = [Attachments(package)]
            }
        };
    }

    private static Rim.IRenvelopeCompanyTaxReturnReturnInfoSummary Summary(bool hasCt600A) => new()
    {
        Accounts = new Rim.IRenvelopeCompanyTaxReturnReturnInfoSummaryAccounts
        {
            ThisPeriodAccounts = Rim.CT_YesType.yes,
            ThisPeriodAccountsSpecified = true
        },
        Computations = new Rim.IRenvelopeCompanyTaxReturnReturnInfoSummaryComputations
        {
            Item = Rim.CT_YesType.yes,
            ItemElementName = Rim.ItemChoiceType.ThisPeriodComputations
        },
        SupplementaryPages = hasCt600A
            ? new Rim.IRenvelopeCompanyTaxReturnReturnInfoSummarySupplementaryPages
            {
                CT600A = Rim.CT_YesType.yes,
                CT600ASpecified = true
            }
            : null
    };

    private static Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationIncome TradingIncome(
        CompanyTaxComputation computation)
    {
        var trading = new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationIncomeTrading
        {
            Profits = computation.AdjustedTradingProfit > 0m ? Whole(computation.AdjustedTradingProfit) : null,
            LossesBroughtForward = computation.Losses.Used > 0m ? Whole(computation.Losses.Used) : null,
            NetProfits = computation.TaxableTotalProfits > 0m ? Whole(computation.TaxableTotalProfits) : null
        };
        return new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationIncome { Trading = trading };
    }

    private static Rim.CTA SupplementaryA(Ct.Ct600A loans)
    {
        var taxOutstanding = Math.Max(0m, loans.TaxChargeable - loans.TaxPaid);
        return new Rim.CTA
        {
            BeforeEndPeriod = Rim.YesNoType.yes,
            TotalLoansOutstanding = loans.LoansOutstandingAtPeriodEnd > 0m
                ? new Rim.CT_CTnonZeroWholePoundStructure { Value = WholePounds(loans.LoansOutstandingAtPeriodEnd) }
                : null,
            TaxPayable = Pence(taxOutstanding)
        };
    }

    private static Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeable TaxChargeable(
        CompanyTaxComputation computation)
    {
        var period = computation.CorporationTaxPeriod;
        var firstYear = FinancialYear(period.Start);
        var firstEnd = new DateOnly(firstYear + 1, 3, 31);
        var firstDays = Math.Min(period.InclusiveDays,
            (period.End < firstEnd ? period.End : firstEnd).DayNumber - period.Start.DayNumber + 1);
        var totalProfit = WholePounds(computation.TaxableTotalProfits);
        var firstProfit = firstDays == period.InclusiveDays
            ? totalProfit
            : WholePounds(decimal.Round(
                totalProfit * firstDays / period.InclusiveDays,
                0,
                MidpointRounding.AwayFromZero));
        var secondProfit = WholePounds(totalProfit - firstProfit);
        var firstTax = PoundPence(firstProfit * computation.MainRate);
        var totalTax = PoundPence(computation.CorporationTaxChargeable);
        var secondTax = PoundPence(totalTax - firstTax);
        var taxRate = WithTwoDecimalPlaces(computation.MainRate * 100m);

        return new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeable
        {
            AssociatedCompanies = computation.MainRate == 0.19m && firstYear >= 2023
                ? new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeableAssociatedCompanies
                {
                    ThisPeriod = "0",
                    StartingOrSmallCompaniesRate = Rim.CT_YesType.yes,
                    StartingOrSmallCompaniesRateSpecified = true
                }
                : null,
            FinancialYearOne = new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeableFinancialYearOne
            {
                Year = firstYear.ToString(CultureInfo.InvariantCulture),
                Details =
                [
                    new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeableFinancialYearOneDetails
                    {
                        Profit = Whole(firstProfit),
                        TaxRate = taxRate,
                        Tax = Pence(firstTax)
                    }
                ]
            },
            FinancialYearTwo = secondProfit > 0m
                ? new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeableFinancialYearTwo
                {
                    Year = (firstYear + 1).ToString(CultureInfo.InvariantCulture),
                    Details =
                    [
                        new Rim.IRenvelopeCompanyTaxReturnCompanyTaxCalculationCorporationTaxChargeableFinancialYearTwoDetails
                        {
                            Profit = Whole(secondProfit),
                            TaxRate = taxRate,
                            Tax = Pence(secondTax)
                        }
                    ]
                }
                : null
        };
    }

    private static Rim.XBRLsubmission Attachments(CorporationTaxReturnPackage package) => new()
    {
        Items =
        [
            InlineDocument(package.ComputationDocument, new DateOnly(2025, 1, 1)),
            InlineDocument(package.AccountsDocument, new DateOnly(2026, 1, 1))
        ],
        ItemsElementName = [Rim.ItemsChoiceType.Computation, Rim.ItemsChoiceType.Accounts]
    };

    private static Rim.XBRLsubmissionDetails InlineDocument(DocumentArtifact artifact, DateOnly taxonomyDate) => new()
    {
        Instance = new Rim.XBRLsubmissionDetailsInstance
        {
            Filename = artifact.FileName,
            BaseTaxonomyVersionDate = taxonomyDate.ToDateTime(TimeOnly.MinValue),
            BaseTaxonomyVersionDateSpecified = true,
            Items =
            [
                new Rim.XBRLsubmissionDetailsInstanceInlineXBRLDocument
                {
                    Any = ReadRoot(artifact.Content),
                    Filename = artifact.FileName,
                    entryPoint = Rim.CT_YesType.yes,
                    entryPointSpecified = true
                }
            ]
        }
    };

    private static XmlElement ReadRoot(byte[] bytes)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20_000_000
        });
        document.Load(reader);
        return document.DocumentElement ?? throw new InvalidOperationException("An iXBRL attachment has no document element.");
    }

    private static Rim.CTwholePoundStructure Whole(decimal value) => new() { Value = WholePounds(value) };
    private static Rim.CTpoundPenceStructure Pence(decimal value) => new() { Value = PoundPence(value) };
    private static decimal WholePounds(decimal value) => WithTwoDecimalPlaces(decimal.Truncate(value));
    private static decimal PoundPence(decimal value) => WithTwoDecimalPlaces(decimal.Truncate(value * 100m) / 100m);
    private static decimal WithTwoDecimalPlaces(decimal value) =>
        decimal.Parse(value.ToString("0.00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static int FinancialYear(DateOnly date) => date.Month >= 4 ? date.Year : date.Year - 1;
}
