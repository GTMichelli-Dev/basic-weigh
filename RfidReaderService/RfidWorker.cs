using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using RfidReaderService.Data;
using RfidReaderService.Models;
using RfidReaderService.Services;

namespace RfidReaderService;

/// <summary>
/// Keeps a SignalR connection to the BasicWeigh hub and one read loop per
/// active reader. Card presentations go up as "CardRead"; the web app decides
/// what they mean. Reader configuration is edited from the web app's Reader
/// Management page and relayed back down here as CRUD commands, so nobody has
/// to SSH into a scale house to change a baud rate.
/// </summary>
public class RfidWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<RfidWorker> _log;
    private readonly RestartSignal _restart;
    private readonly AnnounceSignal _announce;
    private readonly SerialCardReaderClient _serial;
    private readonly CardReadStore _reads;
    private HubConnection? _connection;
    private string _serviceId = "default";

    public RfidWorker(
        IServiceProvider sp,
        ILogger<RfidWorker> log,
        RestartSignal restart,
        AnnounceSignal announce,
        SerialCardReaderClient serial,
        CardReadStore reads)
    {
        _sp = sp;
        _log = log;
        _restart = restart;
        _announce = announce;
        _serial = serial;
        _reads = reads;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _announce.OnAnnounceRequested += async () =>
        {
            try { await AnnounceReaders(); }
            catch (Exception ex) { _log.LogWarning("Announce failed: {Msg}", ex.Message); }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string serverUrl, hubPath;
                using (var scope = _sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
                    var settings = db.Settings.OrderBy(s => s.Id).FirstOrDefault();
                    serverUrl = settings?.ServerUrl ?? "http://localhost:5110";
                    hubPath = settings?.SignalRHub ?? "/scaleHub";
                    _serviceId = settings?.ServiceId ?? "default";
                }

                _log.LogInformation("Loaded settings: ServiceId={ServiceId}, ServerUrl={Url}", _serviceId, serverUrl);

                await ConnectAndRun(serverUrl, hubPath, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Connection lost. Reconnecting in 5 seconds... Error: {Msg}", ex.Message);
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (_connection != null)
                {
                    try { await _connection.DisposeAsync(); }
                    catch { /* ignore cleanup errors */ }
                    _connection = null;
                }
            }
        }
    }

    private async Task ConnectAndRun(string serverUrl, string hubPath, CancellationToken stoppingToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl.TrimEnd('/')}{hubPath}")
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.Reconnecting += _ =>
        {
            _log.LogWarning("Connection lost. Reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _log.LogInformation("Reconnected. Rejoining reader groups...");
            await JoinGroups();
            await AnnounceReaders();
        };

        RegisterHandlers();

        _log.LogInformation("Connecting to {Url}{Hub}", serverUrl, hubPath);
        await _connection.StartAsync(stoppingToken);
        _log.LogInformation("Connected. Joining reader groups (ServiceId={ServiceId})...", _serviceId);

        await JoinGroups();
        await AnnounceReaders();

        // Reader edits and settings changes cancel this token so the loops
        // restart against the new configuration without dropping the hub
        // connection any longer than necessary.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _restart.Token);
        await RunReaders(linked.Token);
    }

    private void RegisterHandlers()
    {
        _connection!.On("GetReaderList", async () =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
                var readers = await db.Readers.OrderBy(r => r.ReaderId).ToListAsync();
                await _connection!.InvokeAsync("ReaderListResponse", new
                {
                    serviceId = _serviceId,
                    readers = readers.Select(Describe)
                });
            }
            catch (Exception ex) { _log.LogWarning("GetReaderList failed: {Msg}", ex.Message); }
        });

        _connection!.On("GetSerialPorts", async () =>
        {
            try
            {
                await _connection!.InvokeAsync("SerialPortsResponse", new
                {
                    serviceId = _serviceId,
                    ports = SerialCardReaderClient.ListPorts()
                });
            }
            catch (Exception ex) { _log.LogWarning("GetSerialPorts failed: {Msg}", ex.Message); }
        });

        _connection!.On("ReloadConfig", () =>
        {
            _log.LogInformation("Received ReloadConfig. Restarting read loops...");
            _restart.TriggerRestart();
        });

        _connection!.On<System.Text.Json.JsonElement>("AddReader", async config =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
                var entity = Deserialize(config);
                if (entity == null || string.IsNullOrWhiteSpace(entity.ReaderId))
                {
                    await CrudResult(false, "add", "", "Invalid reader data");
                    return;
                }
                if (db.Readers.Any(r => r.ReaderId == entity.ReaderId))
                {
                    await CrudResult(false, "add", entity.ReaderId, "A reader with that ID already exists");
                    return;
                }
                entity.Id = 0;
                db.Readers.Add(entity);
                await db.SaveChangesAsync();
                await CrudResult(true, "add", entity.ReaderId, null);
                _restart.TriggerRestart();
            }
            catch (Exception ex) { await CrudResult(false, "add", "", ex.Message); }
        });

        _connection!.On<string, System.Text.Json.JsonElement>("UpdateReader", async (readerId, config) =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
                var existing = db.Readers.FirstOrDefault(r => r.ReaderId == readerId);
                if (existing == null)
                {
                    await CrudResult(false, "update", readerId, "Reader not found");
                    return;
                }
                DeserializeUpdate(config)?.ApplyTo(existing);
                await db.SaveChangesAsync();
                await CrudResult(true, "update", readerId, null);
                _restart.TriggerRestart();
            }
            catch (Exception ex) { await CrudResult(false, "update", readerId, ex.Message); }
        });

        _connection!.On<string>("DeleteReader", async readerId =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
                var existing = db.Readers.FirstOrDefault(r => r.ReaderId == readerId);
                if (existing == null)
                {
                    await CrudResult(false, "delete", readerId, "Reader not found");
                    return;
                }
                db.Readers.Remove(existing);
                await db.SaveChangesAsync();
                await CrudResult(true, "delete", readerId, null);
                _restart.TriggerRestart();
            }
            catch (Exception ex) { await CrudResult(false, "delete", readerId, ex.Message); }
        });
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static ReaderConfigEntity? Deserialize(System.Text.Json.JsonElement config) =>
        System.Text.Json.JsonSerializer.Deserialize<ReaderConfigEntity>(config.GetRawText(), JsonOptions);

    private static ReaderUpdateRequest? DeserializeUpdate(System.Text.Json.JsonElement config) =>
        System.Text.Json.JsonSerializer.Deserialize<ReaderUpdateRequest>(config.GetRawText(), JsonOptions);

    private async Task CrudResult(bool success, string operation, string readerId, string? error)
    {
        if (_connection?.State != HubConnectionState.Connected) return;
        await _connection.InvokeAsync("ReaderCrudResult",
            new { serviceId = _serviceId, success, operation, readerId, error });
    }

    private async Task JoinGroups()
    {
        await _connection!.InvokeAsync("JoinReaderGroup", _serviceId);
    }

    private async Task AnnounceReaders()
    {
        if (_connection?.State != HubConnectionState.Connected) return;

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
            var readers = await db.Readers.OrderBy(r => r.ReaderId).ToListAsync();

            await _connection.InvokeAsync("ReaderServiceReady", new
            {
                serviceId = _serviceId,
                readerCount = readers.Count,
                readers = readers.Select(Describe)
            });

            _log.LogInformation("Announced {Count} reader(s) to the web app.", readers.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to announce readers: {Msg}", ex.Message);
        }
    }

    private static object Describe(ReaderConfigEntity r) => new
    {
        r.ReaderId, r.DisplayName, r.ReaderModel,
        r.SerialPortName, r.BaudRate, r.DataBits, r.Parity, r.StopBits,
        r.Format, r.CardNumberRegex, r.StripLeadingZeros, r.IncludeFacilityCode,
        r.MinLength, r.DebounceMs, r.IdleFrameMs, r.TimeoutMs,
        r.Active
    };

    private async Task RunReaders(CancellationToken ct)
    {
        List<ReaderConfigEntity> readers;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RfidDbContext>();
            readers = await db.Readers.Where(r => r.Active).ToListAsync(ct);
        }

        if (readers.Count == 0)
        {
            _log.LogWarning("No active readers configured. Waiting for changes...");
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return;
        }

        _log.LogInformation("Starting {Count} reader loop(s).", readers.Count);
        await Task.WhenAll(readers.Select(r => Task.Run(() => RunReader(r, ct), ct)));
    }

    private async Task RunReader(ReaderConfigEntity reader, CancellationToken ct)
    {
        var backoff = 2000;
        const int maxBackoff = 15000;

        // Debounce state lives with the loop, not the reader row, so a config
        // edit doesn't resurrect a stale suppression.
        string? lastCard = null;
        DateTime lastCardAt = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _reads.SetStatus(reader.ReaderId, "Connected");

                await _serial.ReadStreamAsync(reader, async frame =>
                {
                    var debounced = frame.Ok
                        && frame.CardNumber == lastCard
                        && (DateTime.UtcNow - lastCardAt).TotalMilliseconds < Math.Max(0, reader.DebounceMs);

                    _reads.RecordFrame(reader.ReaderId, frame, debounced);

                    // Every frame goes up as a diagnostic so the Reader page can
                    // show raw bytes while someone is commissioning a reader.
                    if (_connection?.State == HubConnectionState.Connected)
                    {
                        try
                        {
                            await _connection.InvokeAsync("ReaderDiagnostic", new
                            {
                                serviceId = _serviceId,
                                readerId = reader.ReaderId,
                                cardNumber = frame.CardNumber,
                                facilityCode = frame.FacilityCode,
                                rawText = frame.RawText,
                                rawHex = frame.RawHex,
                                ok = frame.Ok,
                                debounced,
                                reason = frame.Reason,
                                readAt = frame.ReadAt
                            }, ct);
                        }
                        catch (Exception ex)
                        {
                            _log.LogDebug("Diagnostic push failed: {Msg}", ex.Message);
                        }
                    }

                    if (!frame.Ok)
                    {
                        _log.LogDebug("Reader '{ReaderId}' rejected frame raw='{Raw}' hex={Hex}: {Reason}",
                            reader.ReaderId, frame.RawText, frame.RawHex, frame.Reason);
                        return;
                    }
                    if (debounced) return;

                    lastCard = frame.CardNumber;
                    lastCardAt = DateTime.UtcNow;

                    _log.LogInformation("Reader '{ReaderId}' card {Card} (raw='{Raw}')",
                        reader.ReaderId, frame.CardNumber, frame.RawText);

                    if (_connection?.State == HubConnectionState.Connected)
                    {
                        await _connection.InvokeAsync("CardRead", new
                        {
                            serviceId = _serviceId,
                            readerId = reader.ReaderId,
                            displayName = reader.DisplayName,
                            cardNumber = frame.CardNumber,
                            rawText = frame.RawText,
                            readAt = frame.ReadAt
                        }, ct);
                    }
                }, ct);

                backoff = 2000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _reads.SetStatus(reader.ReaderId, "Disconnected: " + ex.Message);
                _log.LogWarning("Reader '{ReaderId}' on {Port} failed: {Msg}. Reopening in {Backoff}ms.",
                    reader.ReaderId, reader.SerialPortName, ex.Message, backoff);

                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, maxBackoff);
            }
        }

        _reads.SetStatus(reader.ReaderId, "Stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            try { await _connection.DisposeAsync(); }
            catch { /* ignore */ }
        }
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Retry policy that never gives up — backs off 2s, 5s, 10s, then stays at 30s.
/// A scale house that loses its uplink must reconnect on its own.
/// </summary>
public class ForeverRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var idx = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
        return Delays[idx];
    }
}
