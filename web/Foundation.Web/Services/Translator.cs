namespace Foundation.Web.Services;

/// <summary>
/// Per-request translator. Injected into the views through _ViewImports as
/// <c>L</c> (<c>@L["Place Truck on Scale"]</c>) and into the kiosk / mobile
/// controllers so the messages their AJAX endpoints return land on the driver's
/// screen in the same language the page is already showing.
/// </summary>
public class Translator
{
    private readonly IHttpContextAccessor _http;
    private readonly AppSetupCache _setupCache;
    private string? _code;

    public Translator(IHttpContextAccessor http, AppSetupCache setupCache)
    {
        _http = http;
        _setupCache = setupCache;
    }

    /// <summary>Resolved once per request — the AppSetup read behind it is
    /// cached, but the cookie/query check is pure and cheap to memoize.</summary>
    public string Code => _code ??= Lang.Resolve(_http.HttpContext, _setupCache.Get());

    /// <summary>Whether this site offers Spanish at all. False means no
    /// toggle is rendered and every screen is English.</summary>
    public bool SpanishEnabled => _setupCache.Get().EnableSpanish;

    /// <summary>Language the on-screen toggle switches to.</summary>
    public string OtherCode => Lang.Other(Code);

    /// <summary>Razor shorthand: <c>@L["Cancel"]</c>.</summary>
    public string this[string english] => Lang.T(Code, english);

    public string T(string english) => Lang.T(Code, english);

    /// <summary><c>L.T("Ticket #{0}", ticket)</c>.</summary>
    public string T(string english, params object?[] args) => Lang.T(Code, english, args);

    /// <summary>Href for the on-screen toggle: this page, other language,
    /// every other query parameter intact.</summary>
    public string SwitchUrl => Lang.SwitchUrl(_http.HttpContext, OtherCode);

    /// <summary>Label for the toggle — the language it switches TO.</summary>
    public string SwitchLabel => Lang.ShortName(OtherCode);

    /// <summary>Accessible title for the toggle, in the current language.</summary>
    public string SwitchTitle => T(OtherCode == Lang.Spanish ? "Switch to Spanish" : "Switch to English");

    /// <summary>The { english: translated } map the page's own JS needs.</summary>
    public IReadOnlyDictionary<string, string> Catalog => Lang.CatalogFor(Code);
}
