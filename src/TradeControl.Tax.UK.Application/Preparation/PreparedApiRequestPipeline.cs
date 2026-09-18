using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PreparedParameterContract(string Name, bool Required = true);

public sealed record PreparedApiContract(
    string OperationId,
    string ContractFamily,
    string ContractVersion,
    bool IsPreview,
    string Method,
    string PathTemplate,
    IReadOnlyList<PreparedParameterContract> PathParameters,
    IReadOnlyList<PreparedParameterContract> QueryParameters,
    string Accept,
    string? ContentType,
    bool HasBody);

public sealed record PreparedValidationStage(
    string Name,
    Func<IEnumerable<PreparedArtifactFinding>> Evaluate);

public sealed class PreparedApiRequestPipeline
{
    private static readonly Regex Placeholder = new("\\{(?<name>[A-Za-z][A-Za-z0-9_-]*)\\}", RegexOptions.Compiled);

    public PreparedApiRequest Prepare(
        PreparedApiContract contract,
        IEnumerable<PreparedNameValue> pathValues,
        IEnumerable<PreparedNameValue>? queryValues = null,
        Func<byte[]>? serializeBody = null,
        IEnumerable<PreparedSourceEvidence>? sourceEvidence = null,
        IEnumerable<PreparedValidationStage>? validationStages = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var findings = RunStages(validationStages);
        var path = ResolvePath(contract, pathValues);
        var query = ResolveQuery(contract.QueryParameters, queryValues ?? []);

        if (contract.HasBody != (serializeBody is not null))
            throw new ArgumentException(contract.HasBody
                ? "The operation requires a canonical body serializer."
                : "The operation is bodyless and cannot accept a body serializer.", nameof(serializeBody));

        byte[]? bytes = null;
        if (!findings.Any(finding => finding.Severity == PreparedFindingSeverity.Error))
            bytes = serializeBody?.Invoke();
        if (bytes is { Length: 0 })
            throw new InvalidOperationException("A body-bearing operation cannot serialize an empty body.");

        var headers = new List<PreparedNameValue> { new("Accept", contract.Accept) };
        if (contract.ContentType is not null) headers.Add(new("Content-Type", contract.ContentType));

        return new(contract.OperationId, contract.ContractFamily, contract.ContractVersion, contract.IsPreview,
            contract.Method, path, query, headers, contract.ContentType, bytes,
            sourceEvidence ?? [], findings);
    }

    private static ImmutableArray<PreparedArtifactFinding> RunStages(IEnumerable<PreparedValidationStage>? stages)
    {
        var findings = ImmutableArray.CreateBuilder<PreparedArtifactFinding>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in stages ?? [])
        {
            if (string.IsNullOrWhiteSpace(stage.Name) || !names.Add(stage.Name))
                throw new ArgumentException("Validation-stage names must be present and unique.", nameof(stages));
            findings.AddRange(stage.Evaluate() ?? throw new InvalidOperationException(
                $"Validation stage '{stage.Name}' returned null."));
        }
        return findings.ToImmutable();
    }

    private static string ResolvePath(PreparedApiContract contract, IEnumerable<PreparedNameValue> supplied)
    {
        var expected = contract.PathParameters.Select(item => item.Name).ToArray();
        var placeholders = Placeholder.Matches(contract.PathTemplate).Select(match => match.Groups["name"].Value).ToArray();
        if (!placeholders.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("The path-parameter contract does not match its template in order.");
        var values = Unique(supplied, "path");
        RejectUnknown(values.Keys, expected, "path");
        foreach (var parameter in contract.PathParameters.Where(item => item.Required))
            if (!values.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Required path value '{parameter.Name}' is missing.", nameof(supplied));
        var path = contract.PathTemplate;
        foreach (var parameter in contract.PathParameters)
            if (values.TryGetValue(parameter.Name, out var value))
                path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        if (path.Contains('{') || path.Contains('}'))
            throw new ArgumentException("The path template was not fully resolved.", nameof(supplied));
        return path;
    }

    private static ImmutableArray<PreparedNameValue> ResolveQuery(
        IReadOnlyList<PreparedParameterContract> contract, IEnumerable<PreparedNameValue> supplied)
    {
        var values = Unique(supplied, "query");
        var expected = contract.Select(item => item.Name).ToArray();
        RejectUnknown(values.Keys, expected, "query");
        var result = ImmutableArray.CreateBuilder<PreparedNameValue>();
        foreach (var parameter in contract)
        {
            if (!values.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Required) throw new ArgumentException(
                    $"Required query value '{parameter.Name}' is missing.", nameof(supplied));
                continue;
            }
            result.Add(new(parameter.Name, value));
        }
        return result.ToImmutable();
    }

    private static Dictionary<string, string> Unique(IEnumerable<PreparedNameValue> supplied, string kind)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in supplied)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || !values.TryAdd(item.Name, item.Value))
                throw new ArgumentException($"A {kind} value has an empty or duplicate name.", nameof(supplied));
        }
        return values;
    }

    private static void RejectUnknown(IEnumerable<string> supplied, IReadOnlyCollection<string> expected, string kind)
    {
        var unknown = supplied.Where(name => !expected.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0) throw new ArgumentException(
            $"Unknown {kind} value(s): {string.Join(", ", unknown)}.", nameof(supplied));
    }
}
