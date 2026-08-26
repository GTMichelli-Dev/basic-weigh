using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using GateControllerService.Data;
using GateControllerService.Models;
using GateControllerService.Services;

namespace GateControllerService;

/// <summary>
/// Connects to the web app over SignalR, opens a gate when a ticket finishes on
/// the scale that gate serves, and closes it again when the truck drives off or
/// the cycle times out.
///
/// The gate open command is addressed to this box's group, the way print and
/// camera commands are. Scale weights are a plain broadcast, so this listens to
/// the same ScaleWeight feed every kiosk sees rather than asking for readings.
/// </summary>
public class GateWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<GateWorker> _log;
    private readonly RestartSignal _restart;
    private readonly GateCycleManager _cycles;
    private readonly GpioOutputs _outputs;

    private HubConnection? _connection;
    private string _serviceId = "default";

    /// <summary>How often expired cycles are swept. Well under any sane gate
    /// timeout, and cheap — it is a dictionary scan.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private static readonly string ServiceVersion =
        typeof(GateWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public GateWorker(IServiceProvider sp, ILogger<GateWorker> log, RestartSignal restart,
        GateCycleManager cycles, GpioOutputs outputs)
    {
        _sp = sp;
        _log = log;
        _restart = restart;
        _cycles = cycles;
        _outputs = outputs;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // The sweep runs whether or not the hub is reachable. A gate that is
        // already open must still time out if the connection drops underneath
        // it — losing the server is exactly when a stuck-open gate is worst.
        _ = Task.Run(() => SweepLoop(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _restart.Reset();
                var (serverUrl, hubPath) = await LoadSettings();

                _log.LogInformation("Connecting to {Url}{Hub}", serverUrl, hubPath);

                _connection = new HubConnectionBuilder()
                    .WithUrl($"{serverUrl}{hubPath}")
                    .WithAutomaticReconnect(new ForeverRetryPolicy())
                    .Build();

                _connection.Reconnecting += _ =>
                {
                    _log.LogWarning("Connection lost. Reconnecting...");
                    return Task.CompletedTask;
                };

                _connection.Reconnected += async _ =>
                {
                    _log.LogInformation("Reconnected. Rejoining gate group...");
                    await JoinGroups();
                    await AnnounceGates();
                };

                RegisterHandlers();

                await _connection.StartAsync(ct);
                _log.LogInformation("Connected. Joining gate group (ServiceId={ServiceId})...", _serviceId);
                await JoinGroups();
                await AnnounceGates();

                await Task.Run(() => _restart.WaitForRestart(Timeout.InfiniteTimeSpan), ct);
                _log.LogInformation("Restart triggered. Reconnecting...");

                try { await _connection.DisposeAsync(); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("Connection error: {Msg}. Retrying in 5s...", ex.Message);
                try { if (_connection != null) await _connection.DisposeAsync(); } catch { }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task SweepLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _cycles.CloseExpired();
                await Task.Delay(SweepInterval, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let the sweep die — it is the failsafe.
                _log.LogError(ex, "Gate sweep failed; continuing");
                try { await Task.Delay(SweepInterval, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<(string serverUrl, string hubPath)> LoadSettings()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();
        var settings = await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings != null)
        {
            _serviceId = settings.ServiceId;
            return (settings.ServerUrl, settings.SignalRHub);
        }
        return ("http://localhost:5110", "/scaleHub");
    }

    private async Task JoinGroups() => await _connection!.InvokeAsync("JoinGateGroup", _serviceId);

    private void RegisterHandlers()
    {
        // A ticket finished on a scale. Payload: { gateId, ticket, direction }.
        _connection!.On<JsonElement>("OpenGate", data =>
        {
            try
            {
                var gateId = Str(data, "gateId");
                var ticket = Str(data, "ticket") ?? "";
                var direction = Str(data, "direction") ?? "weighout";
                if (string.IsNullOrEmpty(gateId)) return;

                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();
                var gate = db.Gates.FirstOrDefault(g => g.GateId == gateId);

                if (gate == null) { _log.LogWarning("OpenGate: no gate '{Gate}' configured here", gateId); return; }
                if (!gate.Active) { _log.LogInformation("OpenGate: gate '{Gate}' is inactive, ignoring", gateId); return; }
                if (!GateCycleManager.TriggerMatches(gate, direction))
                {
                    _log.LogInformation("OpenGate: gate '{Gate}' is set to {Trigger}, ignoring a {Dir}",
                        gateId, gate.TriggerOn, direction);
                    return;
                }

                _cycles.Open(gate, ticket);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "OpenGate handler failed");
            }
        });

        // Every scale reading the site produces. Only the ones a gate is
        // watching do anything, and the match is on the full "serviceId:scaleId".
        _connection!.On<JsonElement>("ScaleWeight", data =>
        {
            try
            {
                if (_cycles.OpenGateIds.Count == 0) return;   // nothing to release
                var serviceId = Str(data, "serviceId") ?? "";
                var scaleId = Str(data, "scaleId") ?? "";
                if (scaleId.Length == 0) return;
                if (!data.TryGetProperty("weight", out var w) || !w.TryGetInt32(out var weight)) return;

                _cycles.OnWeight($"{serviceId}:{scaleId}", weight);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ScaleWeight handler failed");
            }
        });

        // Let the office close a gate by hand — a truck that will not move, or
        // a test from the setup screen.
        _connection!.On<string>("CloseGate", gateId =>
        {
            _cycles.Close(gateId, "closed from the web app");
        });
    }

    /// <summary>Tell the web app which gates live on this box, so they can be
    /// picked on the scale setup screen.</summary>
    private async Task AnnounceGates()
    {
        if (_connection?.State != HubConnectionState.Connected) return;

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GateDbContext>();
            var gates = await db.Gates.OrderBy(g => g.GateId).ToListAsync();

            await _connection.InvokeAsync("GateServiceReady", new
            {
                serviceId = _serviceId,
                version = ServiceVersion,
                // Worth surfacing: a service running without GPIO looks healthy
                // from the web app but cannot actually move anything.
                gpio = _outputs.HardwareAvailable,
                gates = gates.Select(g => new
                {
                    g.GateId, g.DisplayName, g.GatePin, g.LightPin, g.InvertOutputs,
                    g.ScaleHardwareId, g.ReleaseWeightThreshold, g.MaxOpenSeconds,
                    g.TriggerOn, g.Active
                })
            });

            _log.LogInformation("Announced {Count} gate(s) to the web app.", gates.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to announce gates: {Msg}", ex.Message);
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Drop every barrier before going away. A gate left energised by a
        // stopped service is the one failure mode with no way to recover.
        // CloseAll drives each gate's own pins with that gate's inversion —
        // there is deliberately no blanket "all pins low" here, because on an
        // active-low board low IS energised.
        _cycles.CloseAll("service stopping");

        if (_connection != null)
        {
            try { await _connection.DisposeAsync(); } catch { }
        }
        await base.StopAsync(cancellationToken);
    }
}

public class ForeverRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var idx = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
        return Delays[idx];
    }
}
