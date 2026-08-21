using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Bilingual (English / Spanish) UI text for the driver-facing screens — the
/// kiosk at the scale, the phone page, the signature pad and the ticket views.
///
/// Strings are keyed by their English source text rather than by a symbolic id,
/// so an untranslated string still renders as readable English instead of a
/// missing-resource marker, and a Razor view reads the same as it did before
/// the wrapper went round it. <see cref="LangCatalog"/> holds the Spanish side.
///
/// Which language a request gets, in order: an explicit <c>?lang=</c> (the
/// on-screen toggle, and the kiosk Pi's install URL, which pins one device to
/// one language), then the per-browser cookie the toggle leaves behind, then
/// the site default from Setup.
/// </summary>
public static class Lang
{
    public const string English = "en";
    public const string Spanish = "es";

    /// <summary>Per-browser language choice, left behind by the toggle. Carries
    /// no credential, so it is a plain readable cookie like bw.siteId.</summary>
    public const string CookieName = "bw.lang";
    public const string QueryName = "lang";

    public static readonly string[] Supported = { English, Spanish };

    private static readonly IReadOnlyDictionary<string, string> NoTranslations =
        new Dictionary<string, string>();

    /// <summary>Two-letter label for the compact kiosk / phone toggle.</summary>
    public static string ShortName(string code) => code == Spanish ? "ES" : "EN";

    /// <summary>The language a toggle should switch to from <paramref name="code"/>.</summary>
    public static string Other(string code) => code == Spanish ? English : Spanish;

    /// <summary>
    /// A supported language code, or null. Accepts a region-qualified tag
    /// ("es-MX", "es-US") since that is what a browser or a hand-typed kiosk
    /// URL is likely to carry.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        if (c.Length > 2) c = c.Substring(0, 2);
        return Array.IndexOf(Supported, c) >= 0 ? c : null;
    }

    /// <summary>Language for this request. Falls back to English with no
    /// HttpContext (background services, scheduled report runs).</summary>
    public static string Resolve(HttpContext? http, AppSetup? setup)
    {
        if (http != null)
        {
            var fromQuery = Normalize(http.Request.Query[QueryName].FirstOrDefault());
            if (fromQuery != null) return fromQuery;

            var fromCookie = Normalize(http.Request.Cookies[CookieName]);
            if (fromCookie != null) return fromCookie;
        }
        return Normalize(setup?.Language) ?? English;
    }

    /// <summary>
    /// This same page in the other language. Every other query parameter is
    /// carried over, which matters most on the kiosk: its URL holds the PIN,
    /// service-id, printer-id, scale-id and reader-id, and a toggle that
    /// dropped them would unmap the kiosk from its scale and printer.
    /// </summary>
    public static string SwitchUrl(HttpContext? http, string toCode)
    {
        if (http == null) return "?" + QueryName + "=" + toCode;

        var parts = new List<string>();
        foreach (var pair in http.Request.Query)
        {
            if (string.Equals(pair.Key, QueryName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var value in pair.Value)
                parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(value ?? ""));
        }
        parts.Add(QueryName + "=" + toCode);

        var path = http.Request.Path.HasValue ? http.Request.Path.Value! : "/";
        return path + "?" + string.Join("&", parts);
    }

    /// <summary>
    /// Translate one string. An entry missing from the catalog returns the
    /// English source, so a half-translated screen degrades to English rather
    /// than to blanks.
    /// </summary>
    public static string T(string code, string english)
    {
        if (code == Spanish && LangCatalog.Spanish.TryGetValue(english, out var translated))
            return translated;
        return english;
    }

    /// <summary>
    /// Translate, then fill <c>{0}</c>-style placeholders. The placeholders are
    /// part of the catalog key, so a translation can reorder them.
    /// </summary>
    public static string T(string code, string english, params object?[] args) =>
        string.Format(T(code, english), args);

    /// <summary>
    /// The catalog a browser needs, as { english: translated }. Empty for
    /// English — the page's T() helper falls through to its own argument, so
    /// an English kiosk ships no translation payload at all.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CatalogFor(string code) =>
        code == Spanish ? LangCatalog.Spanish : NoTranslations;
}
