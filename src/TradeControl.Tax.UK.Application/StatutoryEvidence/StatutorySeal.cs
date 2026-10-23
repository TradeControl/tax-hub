using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeControl.Tax.UK.Application.StatutoryEvidence;

public sealed record StatutorySourceInventoryItem(string SourceObject, string CanonicalState);

public static class StatutorySourceInventory
{
    public static string Sha256(IEnumerable<StatutorySourceInventoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items.Select(item => new StatutorySourceInventoryItem(
                Required(item.SourceObject, nameof(item.SourceObject)), item.CanonicalState ?? string.Empty))
            .OrderBy(item => item.SourceObject, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0 || ordered.Select(item => item.SourceObject).Distinct(StringComparer.Ordinal).Count()
            != ordered.Length)
            throw new ArgumentException("A non-empty source inventory with unique object names is required.", nameof(items));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in ordered)
        {
            Append(hash, item.SourceObject);
            Append(hash, item.CanonicalState);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A source inventory value is required.", name)
        : value.Trim();
}

public sealed record VatSealAdjustment(DateOnly PeriodStart, decimal Amount, string? Explanation);

public sealed record VatSealBoxes(
    decimal VatDueSales,
    decimal VatDueAcquisitions,
    decimal TotalVatDue,
    decimal VatReclaimedCurrentPeriod,
    decimal NetVatDue,
    decimal TotalValueSalesExVat,
    decimal TotalValuePurchasesExVat,
    decimal TotalValueGoodsSuppliedExVat,
    decimal TotalAcquisitionsExVat,
    bool Finalised);

public sealed record VatSealInput(
    string TenantReference,
    string DatabaseInstanceReference,
    string ReportingSubjectReference,
    string VatRegistration,
    string ObligationReference,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string AuthorityEnvironment,
    long AccountingEventId,
    string SourceInventorySha256,
    string PayloadSha256,
    int PayloadLength,
    IReadOnlyList<VatSealAdjustment> Adjustments,
    VatSealBoxes Boxes,
    IReadOnlyDictionary<string, string> SoftwareVersions);

public sealed record StatutorySealCandidate(byte[] CanonicalBytes, string Sha256);

public static class VatStatutorySeal
{
    public const string Profile = "trade-control-statutory-seal-v1";

    public static StatutorySealCandidate Create(VatSealInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Validate(input);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("profile", Profile);
            writer.WriteString("authority", "HMRC");
            writer.WriteString("taxType", "VAT");
            writer.WriteString("tenantReference", input.TenantReference.Trim());
            writer.WriteString("databaseInstanceReference", input.DatabaseInstanceReference.Trim());
            writer.WriteString("reportingSubjectReference", input.ReportingSubjectReference.Trim());
            writer.WriteString("vatRegistration", input.VatRegistration.Trim());
            writer.WriteString("obligationReference", input.ObligationReference.Trim());
            writer.WriteString("periodStart", Date(input.PeriodStart));
            writer.WriteString("periodEnd", Date(input.PeriodEnd));
            writer.WriteString("authorityEnvironment", input.AuthorityEnvironment.Trim().ToUpperInvariant());
            writer.WriteNumber("accountingEventId", input.AccountingEventId);
            writer.WriteString("sourceInventorySha256", input.SourceInventorySha256.ToUpperInvariant());
            writer.WriteString("payloadSha256", input.PayloadSha256.ToUpperInvariant());
            writer.WriteNumber("payloadLength", input.PayloadLength);
            writer.WritePropertyName("adjustments");
            writer.WriteStartArray();
            foreach (var adjustment in input.Adjustments.OrderBy(item => item.PeriodStart))
            {
                writer.WriteStartObject();
                writer.WriteString("periodStart", Date(adjustment.PeriodStart));
                Decimal(writer, "amount", adjustment.Amount);
                writer.WriteString("explanation", NormaliseExplanation(adjustment));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("boxes");
            writer.WriteStartObject();
            Decimal(writer, "vatDueSales", input.Boxes.VatDueSales);
            Decimal(writer, "vatDueAcquisitions", input.Boxes.VatDueAcquisitions);
            Decimal(writer, "totalVatDue", input.Boxes.TotalVatDue);
            Decimal(writer, "vatReclaimedCurrentPeriod", input.Boxes.VatReclaimedCurrentPeriod);
            Decimal(writer, "netVatDue", input.Boxes.NetVatDue);
            Decimal(writer, "totalValueSalesExVat", input.Boxes.TotalValueSalesExVat);
            Decimal(writer, "totalValuePurchasesExVat", input.Boxes.TotalValuePurchasesExVat);
            Decimal(writer, "totalValueGoodsSuppliedExVat", input.Boxes.TotalValueGoodsSuppliedExVat);
            Decimal(writer, "totalAcquisitionsExVat", input.Boxes.TotalAcquisitionsExVat);
            writer.WriteBoolean("finalised", input.Boxes.Finalised);
            writer.WriteEndObject();
            writer.WritePropertyName("softwareVersions");
            writer.WriteStartObject();
            foreach (var version in input.SoftwareVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
                writer.WriteString(version.Key, version.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        return new(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static bool Verify(ReadOnlySpan<byte> canonicalBytes, string expectedSha256) =>
        IsSha256(expectedSha256)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(canonicalBytes), Convert.FromHexString(expectedSha256));

    private static void Validate(VatSealInput input)
    {
        Required(input.TenantReference, nameof(input.TenantReference));
        Required(input.DatabaseInstanceReference, nameof(input.DatabaseInstanceReference));
        Required(input.ReportingSubjectReference, nameof(input.ReportingSubjectReference));
        var vatRegistration = Required(input.VatRegistration, nameof(input.VatRegistration));
        if (vatRegistration.Length != 9 || vatRegistration.Any(character => !char.IsDigit(character)))
            throw new ArgumentException("A nine-digit VAT registration is required.", nameof(input));
        Required(input.ObligationReference, nameof(input.ObligationReference));
        Required(input.AuthorityEnvironment, nameof(input.AuthorityEnvironment));
        if (input.PeriodEnd < input.PeriodStart) throw new ArgumentException("The VAT period is invalid.", nameof(input));
        if (input.AccountingEventId < 0) throw new ArgumentException("The accounting boundary is invalid.", nameof(input));
        if (!IsSha256(input.SourceInventorySha256) || !IsSha256(input.PayloadSha256))
            throw new ArgumentException("The source and payload digests must be SHA-256 values.", nameof(input));
        if (input.PayloadLength < 1) throw new ArgumentException("The exact payload length is required.", nameof(input));
        if (input.Adjustments is null || input.Adjustments.GroupBy(item => item.PeriodStart).Any(group => group.Count() > 1))
            throw new ArgumentException("VAT adjustment periods must be unique.", nameof(input));
        foreach (var adjustment in input.Adjustments)
        {
            if (adjustment.PeriodStart < input.PeriodStart || adjustment.PeriodStart > input.PeriodEnd)
                throw new ArgumentException("A VAT adjustment falls outside the obligation period.", nameof(input));
            _ = NormaliseExplanation(adjustment);
        }
        if (input.SoftwareVersions is null || input.SoftwareVersions.Count == 0
            || input.SoftwareVersions.Any(item => string.IsNullOrWhiteSpace(item.Key)
                || string.IsNullOrWhiteSpace(item.Value)))
            throw new ArgumentException("At least one named software version is required.", nameof(input));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A statutory seal value is required.", name);
        return value.Trim();
    }

    private static string NormaliseExplanation(VatSealAdjustment adjustment)
    {
        var explanation = adjustment.Explanation?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
        if (adjustment.Amount != 0m && explanation.Length == 0)
            throw new ArgumentException("Every non-zero VAT adjustment requires an owner explanation.", nameof(adjustment));
        return explanation;
    }

    private static void Decimal(Utf8JsonWriter writer, string property, decimal value)
    {
        writer.WritePropertyName(property);
        writer.WriteRawValue(value.ToString("0.00000", CultureInfo.InvariantCulture), true);
    }

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
