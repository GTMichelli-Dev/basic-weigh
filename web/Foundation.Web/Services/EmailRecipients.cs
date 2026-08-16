using System.Text.RegularExpressions;

namespace Foundation.Web.Services;

/// <summary>
/// Parsing for the free-text address and name lists on the Email tab. Operators
/// paste from wherever they keep addresses, so commas, semicolons and newlines
/// are all accepted as separators.
/// </summary>
public static partial class EmailRecipients
{
    [GeneratedRegex(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$")]
    private static partial Regex AddressPattern();

    /// <summary>Split an address list into trimmed, de-duplicated addresses.</summary>
    public static List<string> Parse(string? list) =>
        Split(list)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Split a customer/commodity filter into names. Case-insensitive
    /// matching is the caller's job; names are stored on tickets as typed.</summary>
    public static List<string> SplitNames(string? list) =>
        Split(list)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> Split(string? list) =>
        (list ?? "")
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => v.Length > 0);

    public static bool IsValidAddress(string address) => AddressPattern().IsMatch(address);

    /// <summary>Returns the first malformed address in the list, or null when
    /// every entry looks like an address. Used to reject a typo at save time
    /// rather than at 6am when the schedule fires.</summary>
    public static string? FirstInvalid(string? list) =>
        Parse(list).FirstOrDefault(a => !IsValidAddress(a));
}
