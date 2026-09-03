using Foundation.Web.Controllers;
using Foundation.Web.Data;
using Foundation.Web.Hubs;
using Foundation.Web.Services;
using DevExpress.AspNetCore;
using DevExpress.AspNetCore.Reporting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Suppress noisy EF Core SQL logging
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);

// Display timezone (UTC -> user-local conversions for filters, ticket prints,
// date-range queries). Reads "Display:TimeZone" from appsettings.json; falls
// back to the host OS local TZ if unset or invalid.
AppTimeZone.Configure(builder.Configuration["Display:TimeZone"]);

// Database provider switching
var dbProvider = builder.Configuration["DatabaseProvider"] ?? "SQLite";
var connectionString = builder.Configuration.GetConnectionString(dbProvider)
    ?? "Data Source=Foundation.db";

builder.Services.AddDbContext<ScaleDbContext>(options =>
{
    if (dbProvider == "MariaDB")
    {
        // The Pomelo MySQL/MariaDB provider has no EF Core 10 release yet, so
        // it was parked in the .NET 10 migration. SQLite is the shipped
        // default; restore the Pomelo.EntityFrameworkCore.MySql package (and
        // the UseMySql call here) once Pomelo ships a 10.x.
        throw new NotSupportedException(
            "DatabaseProvider=MariaDB is not available in this build: the Pomelo EF Core " +
            "provider does not support EF Core 10 yet. Set DatabaseProvider to \"SQLite\" " +
            "in appsettings.json, or re-add Pomelo.EntityFrameworkCore.MySql when a " +
            "10.x release exists.");
    }
    options.UseSqlite(connectionString);
});

// Scale service (simulated for demo mode)
builder.Services.AddSingleton<SimulatedScaleService>();
builder.Services.AddSingleton<IScaleService>(sp => sp.GetRequiredService<SimulatedScaleService>());
// Multi-scale weight store (tracks all scales with timeout detection)
builder.Services.AddSingleton<ScaleWeightStore>();
builder.Services.AddSingleton<Foundation.Web.Services.SiteScales>();

builder.Services.AddSingleton<PrintQueueService>();
builder.Services.AddSignalR(options =>
{
    // Increase max message size for camera image transfer (base64 images can be 200KB+)
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
});
builder.Services.AddHostedService<ScaleBroadcastService>();

// Scheduled Excel reports + per-load emails (Setup → Email). The SMTP password
// is data-protected, and the key ring is pinned to the content root so it
// survives a restart on the Pi/Linux installs, where the default per-user key
// location isn't writable under systemd.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")))
    .SetApplicationName("Foundation");
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddScoped<ReportEmailRunner>();
builder.Services.AddHostedService<ReportScheduleService>();

// Cookie authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });

builder.Services.AddControllersWithViews();

// AppSetup cache — single DB read, invalidated on save
builder.Services.AddSingleton<Foundation.Web.Services.AppSetupCache>();

// Bilingual (EN/ES) UI. The translator resolves the request's language from
// ?lang= / the cookie / the site default, so it needs the HttpContext.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Foundation.Web.Services.Translator>();

// Swagger / OpenAPI — always enabled, protected by ApiDefinitionPin
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Foundation API Definitions",
        Version = "v1",
        Description = "API documentation for Foundation truck scale management system."
    });
    // Exclude DevExpress and non-API controllers from Swagger
    c.DocInclusionPredicate((docName, apiDesc) =>
    {
        var controllerName = apiDesc.ActionDescriptor.RouteValues["controller"] ?? "";
        // Only include our own API endpoints (routes starting with api/)
        var relativePath = apiDesc.RelativePath ?? "";
        return relativePath.StartsWith("api/", StringComparison.OrdinalIgnoreCase);
    });
});

// DevExpress Reporting
builder.Services.AddDevExpressControls();
builder.Services.ConfigureReportingServices(configurator =>
{
    if (builder.Environment.IsDevelopment())
    {
        configurator.UseDevelopmentMode();
    }
    configurator.ConfigureReportDesigner(designerConfigurator =>
    {
        designerConfigurator.RegisterDataSourceWizardConfigFileConnectionStringsProvider();
    });
    configurator.ConfigureWebDocumentViewer(viewerConfigurator =>
    {
        viewerConfigurator.UseCachedReportSourceBuilder();
    });
});
var app = builder.Build();

// Report storage needs the service provider to resolve a DB context per call
// (it injects custom-field parameters into served layouts), so it is created
// and registered after Build() rather than with the other DevExpress setup.
var reportStorage = new ReportStorageService(app.Services);
DevExpress.XtraReports.Web.Extensions.ReportStorageWebExtension.RegisterExtensionGlobal(reportStorage);

// Initialize database and seed data
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
    context.Database.Migrate();
    DbInitializer.Seed(context, builder.Configuration.GetValue<bool>("SeedDemoData", false));

    // Apply the saved display TZ from AppSetup. SetupController will re-Configure
    // on each save so a restart isn't needed.
    var savedTz = context.AppSetup.AsNoTracking().FirstOrDefault()?.TimeZoneId;
    if (!string.IsNullOrWhiteSpace(savedTz)) AppTimeZone.Configure(savedTz);
}

// Seed the report .repx files on every startup so Visual Studio's DevExpress
// designer can open them directly without first running the web designer.
// GetData() is a no-op when the file already exists; preserves any edits.
{
    foreach (var report in new[] { "TicketReport", "KioskTicketReport" })
    {
        try { reportStorage.GetData(report); }
        catch (Exception ex) { Console.WriteLine($"[ReportStorage] seed failed for {report}: {ex.Message}"); }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseDevExpressControls();
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseStaticFiles();

// Language: ?lang=es pins a device — the on-screen toggle on the kiosk and
// phone links back with it, and a kiosk Pi can carry it in the install URL
// alongside ?pin= and ?scale-id=. Remembered in a cookie so the page's own
// AJAX calls, which carry no query string, answer in the same language.
// Runs before anything writes a response, so the Append always lands.
app.Use(async (context, next) =>
{
    var requested = Foundation.Web.Services.Lang.Normalize(
        context.Request.Query[Foundation.Web.Services.Lang.QueryName].FirstOrDefault());
    // Only consult the setup cache when a ?lang= is actually present, so the
    // common request pays nothing for a feature the site may not even use.
    if (requested != null
        && context.RequestServices.GetRequiredService<Foundation.Web.Services.AppSetupCache>()
                  .Get().EnableSpanish
        && context.Request.Cookies[Foundation.Web.Services.Lang.CookieName] != requested)
    {
        context.Response.Cookies.Append(
            Foundation.Web.Services.Lang.CookieName, requested, new CookieOptions
            {
                // A display preference, not a credential — readable like bw.siteId.
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddYears(1)
            });
    }
    await next();
});

// Swagger PIN protection middleware — must come before UseSwagger
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        var db = context.RequestServices.GetRequiredService<ScaleDbContext>();
        var setup = db.AppSetup.First();
        var pinFromQuery = context.Request.Query["pin"].FirstOrDefault();
        var pin = pinFromQuery ?? context.Request.Cookies["SwaggerPin"];
        if (pin != setup.ApiDefinitionPin)
        {
            context.Response.StatusCode = 401;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body style='font-family:sans-serif;text-align:center;padding:60px;'><h2>Not Authorized</h2><p>A valid API Definition PIN is required to access the Swagger documentation.</p><p><a href='/'>Return to Foundation</a></p></body></html>");
            return;
        }
        // Set cookie so subsequent requests don't need ?pin=
        context.Response.Cookies.Append("SwaggerPin", pin!, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddHours(24)
        });
        // If pin came via query string on the base /swagger path, redirect to
        // /swagger/index.html so the cookie is set before the page loads.
        // We must redirect (not rewrite) to preserve correct relative asset paths.
        if (pinFromQuery != null && (path.Equals("/swagger", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/swagger/", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.Redirect("/swagger/index.html");
            return;
        }
    }
    await next();
});

// Swagger — always enabled (not just development)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Foundation API v1");
    c.DocumentTitle = "Foundation API Definitions";
    c.InjectStylesheet("/css/swagger-custom.css");
    c.HeadContent = @"<link rel=""icon"" type=""image/x-icon"" href=""/api/setup/icon"" />";
});

app.UseRouting();
app.UseAuthentication();

// Middleware: if UseLogin is off, skip auth. If on, enforce it.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Always allow: login page, static files, kiosk, scale API, SignalR, Swagger
    if (path.StartsWith("/Account/") ||
        path.StartsWith("/css/") || path.StartsWith("/js/") || path.StartsWith("/images/") ||
        path.StartsWith("/_content/") || path.StartsWith("/favicon") ||
        path.StartsWith("/api/scale/") || path.StartsWith("/scaleHub") ||
        path.StartsWith("/api/setup/icon") ||
        path.StartsWith("/swagger"))
    {
        await next();
        return;
    }

    // Mobile weighing: the driver's own phone, which has no way to be handed a
    // PIN. Its session is a cookie holding one open ticket, and the page can
    // only ever touch the load it opened — so it stays outside the PIN gate.
    // /api/mobile/ticket/{id}/pdf is the ticket download (TicketController).
    if (path.StartsWith("/Mobile") || path.StartsWith("/api/mobile/"))
    {
        await next();
        return;
    }

    // The print service fetches the ticket PDF over plain HTTP with no cookie
    // jar — it is a daemon on the yard network, not a browser session. Gated,
    // its GET follows the 302 to /Account/Login and it prints the login page's
    // HTML source on receipt stock instead of the ticket. Same action already
    // serves anonymously as /api/mobile/ticket/{id}/pdf above, so this exposes
    // nothing new: a ticket PDF to whoever already knows the ticket number.
    if (path.StartsWith("/api/ticket/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith("/pdf", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // Kiosk / signature pad access: check PIN if UseLogin is on. The signature
    // pad is an unattended tablet like the kiosk, so it shares the kiosk PIN
    // (and cookie). /api/signature/ is included so the pad can upload.
    // The kiosk's own PIN screen has to be reachable without a PIN — it is how
    // an unattended display gets one. Everything else under /Kiosk stays gated.
    if (path.StartsWith("/Kiosk/Pin", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    // "/Kiosk" is matched exactly (not as a prefix) so the admin-only Kiosks
    // management page at /Kiosks does not inherit the kiosk's PIN gate and
    // become reachable with a driver-facing PIN.
    // The printable ticket layouts. A kiosk set to Browser printing opens these
    // in a new tab to print, and its Reprint button does the same — but the
    // operator grids' "Print to Browser" opens the very same URLs. So they
    // belong to both audiences: an authenticated operator passes here, and a
    // kiosk passes on its PIN through the block below. Gating them on the PIN
    // alone would send logged-in operators to the kiosk numpad; leaving them
    // out entirely — which is what shipped — put a login form on the driver's
    // touchscreen instead of their ticket, on a display with no keyboard.
    var isTicketView = path.StartsWith("/Ticket/KioskView/", StringComparison.OrdinalIgnoreCase)
                       || path.StartsWith("/Ticket/View/", StringComparison.OrdinalIgnoreCase);
    if (isTicketView && context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    // /api/retainedtares/lookup is the kiosk's stored-tare check. Only the
    // lookup: the rest of that controller sets and clears tares and stays
    // admin-only. Left out, the kiosk's GET redirected to /Account/Login, the
    // page's getJSON got the login HTML back, and resolveTarePrompt's .fail
    // handler dropped the prompt — so "Allow Tare Reset at the Kiosk" looked
    // like it did nothing at all, with nothing on screen to say why.
    if (path.Equals("/Kiosk", StringComparison.Ordinal) || path.StartsWith("/Kiosk/", StringComparison.Ordinal) ||
        path.StartsWith("/api/kiosk/") ||
        path.StartsWith("/api/retainedtares/lookup", StringComparison.OrdinalIgnoreCase) ||
        isTicketView ||
        path.StartsWith("/SignaturePad") || path.StartsWith("/api/signature/"))
    {
        var db = context.RequestServices.GetRequiredService<ScaleDbContext>();
        var setup = db.AppSetup.First();
        if (setup.UseLogin)
        {
            var pin = context.Request.Query["pin"].FirstOrDefault()
                      ?? context.Request.Cookies[KioskController.PinCookie];
            if (pin != setup.KioskCode)
            {
                // A kiosk is a touchscreen in a yard: it has no keyboard and
                // cannot use the operator login page. Send the display to its
                // own numpad instead. API calls get a status code — there is
                // no one there to read a redirect.
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var target = context.Request.Path + context.Request.QueryString;
                context.Response.Redirect("/Kiosk/Pin?returnUrl=" + Uri.EscapeDataString(target));
                return;
            }

            // Re-issued on every load rather than only when missing, so a
            // kiosk that stays in service is asked for its PIN exactly once.
            // The cookie lives in the display's browser profile, so a replaced
            // Pi asks again — as does one whose profile has been wiped.
            KioskController.WritePinCookie(context, pin!);
        }
        await next();
        return;
    }

    // Check if login is required
    var dbCheck = context.RequestServices.GetRequiredService<ScaleDbContext>();
    var appSetup = dbCheck.AppSetup.First();
    if (appSetup.UseLogin && !context.User.Identity!.IsAuthenticated)
    {
        context.Response.Redirect($"/Account/Login?returnUrl={Uri.EscapeDataString(path)}");
        return;
    }

    // Role-based access
    if (appSetup.UseLogin && context.User.Identity!.IsAuthenticated)
    {
        var role = context.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? "User";

        // Setup page: Admin only
        if (path.StartsWith("/Setup") && role != "Admin")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Access denied. Admin role required.");
            return;
        }

        // User management: Admin only
        if (path.StartsWith("/Account/Users") || path.StartsWith("/Account/CreateUser") ||
            path.StartsWith("/Account/EditUser") || path.StartsWith("/Account/ResetPassword") ||
            path.StartsWith("/Account/DeleteUser"))
        {
            if (role != "Admin")
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied. Admin role required.");
                return;
            }
        }

        // Edit Tables: Manager or Admin only
        if (path.StartsWith("/MasterData") && role == "User")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Access denied. Manager or Admin role required.");
            return;
        }

        // Card readers are device configuration, like the Setup page itself.
        if ((path.StartsWith("/Reader") || path.StartsWith("/api/readers")) && role != "Admin")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Access denied. Admin role required.");
            return;
        }

        // Card enrollment (registering physical cards) is Manager or Admin.
        // Issuing a card — /Card/Setup and the /api/cards/ endpoints it uses —
        // is the loader operator's job, so it stays open to the User role.
        var isCardAdmin = path.Equals("/Card", StringComparison.OrdinalIgnoreCase)
                          || path.Equals("/Card/", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/Card/Index", StringComparison.OrdinalIgnoreCase)
                          || path.StartsWith("/api/cardadmin", StringComparison.OrdinalIgnoreCase);
        if (isCardAdmin && role == "User")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Access denied. Manager or Admin role required.");
            return;
        }
    }

    await next();
});

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapHub<ScaleHub>("/scaleHub");

app.Run();
