using GateControllerService;
using GateControllerService.Data;
using GateControllerService.Models;
using GateControllerService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var version = typeof(GateWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

try { Console.Title = $"Gate Controller Service v{version}"; } catch { /* not a real console */ }
Console.WriteLine();
Console.WriteLine("============================================");
Console.WriteLine($"  Gate Controller Service v{version}");
Console.WriteLine("============================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BasicWeigh Gate Controller Service";
});

var dbPath = Path.Combine(AppContext.BaseDirectory, "gatecontrollerservice.db");
builder.Services.AddDbContext<GateDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<RestartSignal>();
builder.Services.AddSingleton<GpioOutputs>();
builder.Services.AddSingleton<GateCycleManager>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Gate Controller Service API",
        Version = "v1",
        Description = "Configure the gates, lights and release rules on this box."
    });
});

builder.Services.AddHostedService<GateWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();
    db.Database.EnsureCreated();

    // Columns added after the initial schema go here — EnsureCreated() does not
    // migrate existing tables. Same approach the other Pi services take.
    // (none yet)

    // Seed gates from appsettings.json on a fresh install, so an image can ship
    // with the site's wiring already described.
    if (!db.Gates.Any())
    {
        var configured = builder.Configuration.GetSection("Gates").Get<List<GateConfigEntity>>();
        if (configured is { Count: > 0 })
        {
            foreach (var g in configured) db.Gates.Add(g);
            db.SaveChanges();
        }
    }

    // Let appsettings.json override the seeded defaults on first run.
    var settings = db.Settings.OrderBy(s => s.Id).FirstOrDefault();
    if (settings != null)
    {
        var configServerUrl = builder.Configuration["Gate:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configServerUrl) && settings.ServerUrl == "http://localhost:5110")
            settings.ServerUrl = configServerUrl;

        var configServiceId = builder.Configuration["Gate:ServiceId"];
        if (!string.IsNullOrWhiteSpace(configServiceId) && settings.ServiceId == "default")
            settings.ServiceId = configServiceId;

        db.SaveChanges();
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

app.Run();
