using TradeControl.Tax.UK.WebHarness.Requests.Payloads;
using TradeControl.Tax.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;

public sealed class TagMapper
{
    public IReadOnlyList<PayloadHarnessItem> MapBusinessTaxItems(
        IEnumerable<BusinessIncomeFact> facts,
        IReadOnlyList<string> expectedTags)
    {
        var valuesByTag = facts
            .Where(x => x.Amount.HasValue)
            .GroupBy(x => x.Key.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Amount.Value),
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
