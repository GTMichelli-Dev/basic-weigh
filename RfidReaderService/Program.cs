using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.OpenApi.Models;
using RfidReaderService;
using RfidReaderService.Data;
using RfidReaderService.Models;
using RfidReaderService.Services;

var version = typeof(RfidWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

// Version first, before framework startup logs flood the console.
try { Console.Title = $"RFID Reader Service v{version}"; } catch { /* not a real console */ }
Console.WriteLine();
Console.WriteLine("============================================");
Console.WriteLine($"  RFID Reader Service v{version}");
Console.WriteLine("============================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BasicWeigh RFID Reader Service";
});

var dbPath = Path.Combine(AppContext.BaseDirectory, "rfidreaderservice.db");
builder.Services.AddDbContext<RfidDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<SerialCardReaderClient>();
builder.Services.AddSingleton<RestartSignal>();
builder.Services.AddSingleton<AnnounceSignal>();
builder.Services.AddSingleton<CardReadStore>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "RFID Reader Service API",
        Version = "v1",
        Description = "Health, status, settings, reader configuration, and live card-read diagnostics."
    });
});

builder.Services.AddHostedService<RfidWorker>();

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(new EventLogSettings
    {
        SourceName = "RfidReaderService"
    });
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated() does not migrate existing tables, so columns added after
    // the first release are patched in by hand — same approach the Scale
    // Reader Service uses, and for the same reason: these service databases
    // are tiny and live on machines nobody wants to run migrations on.
    AddColumnIfMissing(db, "Readers", "Format", "TEXT NOT NULL DEFAULT 'Auto'");
    AddColumnIfMissing(db, "Readers", "CardNumberRegex", "TEXT NULL");
    AddColumnIfMissing(db, "Readers", "StripLeadingZeros", "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing(db, "Readers", "MinLength", "INTEGER NOT NULL DEFAULT 4");
    AddColumnIfMissing(db, "Readers", "DebounceMs", "INTEGER NOT NULL DEFAULT 3000");
    AddColumnIfMissing(db, "Readers", "IdleFrameMs", "INTEGER NOT NULL DEFAULT 60");
    AddColumnIfMissing(db, "Readers", "IncludeFacilityCode", "INTEGER NOT NULL DEFAULT 0");

    // Seed a reader from appsettings.json on a fresh install so a Pi that was
    // imaged with a known port comes up reading without any API calls.
    if (!db.Readers.Any())
    {
        var seed = builder.Configuration.GetSection("Readers").Get<List<ReaderConfigEntity>>();
        if (seed != null && seed.Count > 0)
        {
            foreach (var r in seed)
            {
                r.Id = 0;
                if (string.IsNullOrWhiteSpace(r.ReaderId)) continue;
                db.Readers.Add(r);
            }
            db.SaveChanges();
        }
    }

    // Self-heal the settings row from configuration: an install script writes
    // the real server URL into appsettings.json, but the seeded row still says
    // localhost until something overwrites it.
    var settings = db.Settings.OrderBy(s => s.Id).FirstOrDefault();
    if (settings != null)
    {
        var configServerUrl = builder.Configuration["Rfid:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configServerUrl) && settings.ServerUrl == "http://localhost:5110")
        {
            settings.ServerUrl = configServerUrl;
        }

        var configServiceId = builder.Configuration["Rfid:ServiceId"];
        if (!string.IsNullOrWhiteSpace(configServiceId) && settings.ServiceId == "default")
        {
            settings.ServiceId = configServiceId;
        }
        db.SaveChanges();
    }
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "RFID Reader Service API v1");
});

app.MapControllers();

var urls = builder.Configuration["Urls"] ?? "http://localhost:5250";
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RfidReaderService");
logger.LogInformation("============================================");
logger.LogInformation("  RFID Reader Service v{Version}", version);
logger.LogInformation("  Swagger: {Urls}/swagger", urls);
logger.LogInformation("============================================");

await app.RunAsync();

static void AddColumnIfMissing(RfidDbContext db, string table, string column, string definition)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();

    using (var check = conn.CreateCommand())
    {
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA table_info: cid, name, type, notnull, dflt_value, pk
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
    }

    using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
    alter.ExecuteNonQuery();
}
