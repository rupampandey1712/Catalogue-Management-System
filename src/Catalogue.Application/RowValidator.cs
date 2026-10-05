using Catalogue.Contracts;

namespace Catalogue.Application;

public static class RowValidator
{
    private static readonly HashSet<string> SupportedCurrencies = ["GBP", "USD", "EUR"];

    public static IReadOnlyList<(string Field, string Message)> Validate(CanonicalProductImport row)
    {
        var errors = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(row.Sku))
            errors.Add(("Sku", "SKU is required."));
        else if (row.Sku.Length > 64)
            errors.Add(("Sku", "SKU must be 64 characters or fewer."));
        if (string.IsNullOrWhiteSpace(row.Name))
            errors.Add(("Name", "Name is required."));
        if (row.Price is < 0)
            errors.Add(("Price", "Price cannot be negative."));
        if (row.Currency is not null && !SupportedCurrencies.Contains(row.Currency.ToUpperInvariant()))
            errors.Add(("Currency", $"Unsupported currency '{row.Currency}'."));
        if (row.Quantity is < 0)
            errors.Add(("Quantity", "Quantity cannot be negative."));
        return errors;
    }
}
