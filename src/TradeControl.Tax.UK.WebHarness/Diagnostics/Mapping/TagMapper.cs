using TradeControl.Tax.UK.WebHarness.Requests.Payloads;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;

public sealed class TagMapper
{
    public IReadOnlyList<PayloadHarnessItem> MapBusinessTaxItems(
        IEnumerable<TcBusinessTaxView> rows,
        IReadOnlyList<string> expectedTags)
    {
        var valuesByTag = rows
            .GroupBy(x => x.TagCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.TaxableAmount),
                StringComparer.OrdinalIgnoreCase);

        var items = new List<PayloadHarnessItem>(expectedTags.Count);

        foreach (var tag in expectedTags)
        {
            valuesByTag.TryGetValue(tag, out var value);
            if (value < 0)
            {
                value = 0;
            }

            items.Add(new PayloadHarnessItem
            {
                Tag = tag,
                Value = decimal.Round(value, 2, MidpointRounding.AwayFromZero)
            });
        }

        return items;
    }
}
