using Microsoft.AspNetCore.SignalR;

namespace Foundation.Web.Hubs;

public class ScaleHub : Hub
{
    // Track connected QB sync services
    private static readonly HashSet<string> _qbSyncConnections = new();
    private static readonly object _qbLock = new();

    // Track connected camera services: connectionId -> serviceId
    private static readonly Dictionary<string, string> _cameraConnections = new();
    private static readonly object _cameraLock = new();

    // Track connected print services: connectionId -> serviceId
    private static readonly Dictionary<string, string> _printConnections = new();
    private static readonly object _printLock = new();

    /// <summary>
    /// Called by Web Print Service to join the PrintClients group.
    /// </summary>
    public async Task JoinPrintGroup(string serviceId = "default")
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "PrintClients");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Print_{serviceId}");
        lock (_printLock) { _printConnections[Context.ConnectionId] = serviceId; }
        await Clients.All.SendAsync("PrintServiceStatusChanged", GetConnectedPrintServiceIds());
    }

    public Task<bool> CheckPrintServiceConnected()
    {
        lock (_printLock) { return Task.FromResult(_printConnections.Count > 0); }
    }

    public async Task PrintServiceReady(object announcement)
    {
        await Clients.All.SendAsync("PrintServiceReady", announcement);
    }

    public async Task PrinterListResponse(object printers)
    {
        await Clients.All.SendAsync("PrinterListReceived", printers);
    }

    public async Task RequestPrinterList()
    {
        await Clients.Group("PrintClients").SendAsync("GetPrinterList");
    }

    public async Task PrintResult(object result)
    {
        await Clients.All.SendAsync("PrintResult", result);
    }

    public async Task TestPrintResult(object result)
    {
        await Clients.All.SendAsync("TestPrintResult", result);
    }

    public async Task TestPrint(string serviceId, string printerId)
    {
        await Clients.Group($"Print_{serviceId}").SendAsync("TestPrint", printerId);
    }

    /// <summary>
    /// Called by the web app to print a ticket.
    /// Routes to a specific print service by serviceId, or broadcasts to all if not specified.
    /// </summary>
    public async Task PrintTicket(string? serviceId, object ticketData)
    {
        if (!string.IsNullOrEmpty(serviceId))
        {
            await Clients.Group($"Print_{serviceId}").SendAsync("PrintTicket", ticketData);
        }
        else
        {
            await Clients.Group("PrintClients").SendAsync("PrintTicket", ticketData);
        }
    }

    // ===== QB SYNC SERVICE =====

    public async Task JoinQBSyncGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "QBSyncClients");
        lock (_qbLock) { _qbSyncConnections.Add(Context.ConnectionId); }
        await Clients.All.SendAsync("QBServiceStatusChanged", true);
    }

    public Task<bool> CheckQBServiceConnected()
    {
        lock (_qbLock) { return Task.FromResult(_qbSyncConnections.Count > 0); }
    }

    public async Task QBNotRunning(string message)
    {
        await Clients.All.SendAsync("QBNotRunning", message);
    }

    public async Task TriggerQBSync()
    {
        await Clients.Group("QBSyncClients").SendAsync("SyncQuickBooks");
    }

    public async Task SyncStatus(string message)
    {
        await Clients.All.SendAsync("QBSyncStatus", message);
    }

    public async Task SyncComplete(string summary)
    {
        await Clients.All.SendAsync("QBSyncComplete", summary);
    }

    public async Task SendTicketsToQB(object tickets)
    {
        await Clients.Group("QBSyncClients").SendAsync("SendTicketsToQB", tickets);
    }

    // ===== CAMERA SERVICE =====

    public async Task JoinCameraGroup(string serviceId = "default")
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "CameraClients");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Camera_{serviceId}");
        lock (_cameraLock) { _cameraConnections[Context.ConnectionId] = serviceId; }
        await Clients.All.SendAsync("CameraServiceStatusChanged", GetConnectedCameraServiceIds());
    }

    public Task<bool> CheckCameraServiceConnected()
    {
        lock (_cameraLock) { return Task.FromResult(_cameraConnections.Count > 0); }
    }

    public Task<List<string>> GetConnectedCameraServices()
    {
        return Task.FromResult(GetConnectedCameraServiceIds());
    }

    public async Task CaptureImage(string ticket, string direction, string? cameraId = null, string? serviceId = null)
    {
        var payload = new { ticket, direction, cameraId };
        if (!string.IsNullOrEmpty(serviceId))
        {
            await Clients.Group($"Camera_{serviceId}").SendAsync("CaptureImage", payload);
        }
        else
        {
            await Clients.Group("CameraClients").SendAsync("CaptureImage", payload);
        }
    }

    public async Task ImageCaptured(string ticket, string direction)
    {
        await Clients.All.SendAsync("ImageCaptured", new { ticket, direction });
    }

    public async Task CameraServiceReady(object announcement)
    {
        await Clients.All.SendAsync("CameraServiceReady", announcement);
    }

    public async Task CameraServiceDisconnected(string serviceId)
    {
        await Clients.All.SendAsync("CameraServiceDisconnected", serviceId);
    }

    public async Task ReloadCameraConfig()
    {
        await Clients.Group("CameraClients").SendAsync("ReloadConfig");
    }

    public async Task RequestCameraList()
    {
        await Clients.Group("CameraClients").SendAsync("GetCameraList");
    }

    public async Task CameraListResponse(object cameras)
    {
        await Clients.All.SendAsync("CameraListReceived", cameras);
    }

    public async Task RequestCameraBrands()
    {
        await Clients.Group("CameraClients").SendAsync("GetCameraBrands");
    }

    public async Task CameraBrandsResponse(object brands)
    {
        await Clients.All.SendAsync("CameraBrandsReceived", brands);
    }

    // ===== CAMERA CRUD RELAY (Web UI -> Camera Service) =====

    public async Task AddCameraToService(string serviceId, object cameraConfig)
    {
        await Clients.Group($"Camera_{serviceId}").SendAsync("AddCamera", cameraConfig);
    }

    public async Task UpdateCameraOnService(string serviceId, string cameraId, object cameraConfig)
    {
        await Clients.Group($"Camera_{serviceId}").SendAsync("UpdateCamera", cameraId, cameraConfig);
    }

    public async Task DeleteCameraFromService(string serviceId, string cameraId)
    {
        await Clients.Group($"Camera_{serviceId}").SendAsync("DeleteCamera", cameraId);
    }

    public async Task TestCameraCapture(string serviceId, string cameraId)
    {
        await Clients.Group($"Camera_{serviceId}").SendAsync("TestCapture", cameraId);
    }

    // Camera service -> all web clients: CRUD result
    public async Task CameraCrudResult(object result)
    {
        await Clients.All.SendAsync("CameraCrudResult", result);
    }

    // Camera service -> all web clients: test capture result (base64 image)
    public async Task TestCaptureResult(object result)
    {
        await Clients.All.SendAsync("TestCaptureResult", result);
    }

    // ===== SIGNATURE PAD =====

    // Track connected signature pads: connectionId -> padId
    private static readonly Dictionary<string, string> _signaturePadConnections = new();
    private static readonly object _signaturePadLock = new();

    /// <summary>
    /// Called by the /SignaturePad standby page to register itself as a pad.
    /// </summary>
    public async Task JoinSignaturePadGroup(string padId = "default")
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "SignaturePadClients");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"SignaturePad_{padId}");
        lock (_signaturePadLock) { _signaturePadConnections[Context.ConnectionId] = padId; }
        await Clients.All.SendAsync("SignaturePadStatusChanged", GetConnectedSignaturePadIds());
    }

    public Task<List<string>> GetConnectedSignaturePads()
    {
        return Task.FromResult(GetConnectedSignaturePadIds());
    }

    /// <summary>
    /// Called by the Weigh Out page to wake the pad and show the capture view.
    /// requestData carries the ticket context shown to the driver (ticket, truck,
    /// carrier, net weight).
    /// </summary>
    public async Task RequestSignature(string padId, object requestData)
    {
        await Clients.Group($"SignaturePad_{padId}").SendAsync("RequestSignature", requestData);
    }

    /// <summary>
    /// Called by the Weigh Out page to send the pad back to standby (operator
    /// cancelled, or navigated away while a request was outstanding).
    /// </summary>
    public async Task CancelSignature(string padId, string ticket)
    {
        await Clients.Group($"SignaturePad_{padId}").SendAsync("CancelSignature", ticket);
    }

    /// <summary>
    /// Called by the pad when the driver cancels or the request idles out, so the
    /// operator's Weigh Out page can drop its "waiting for driver" state.
    /// </summary>
    public async Task SignatureDeclined(string ticket)
    {
        await Clients.All.SendAsync("SignatureDeclined", new { ticket });
    }

    // ===== DISCONNECT HANDLING =====

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Check QB
        bool wasQB;
        lock (_qbLock) { wasQB = _qbSyncConnections.Remove(Context.ConnectionId); }
        if (wasQB)
        {
            bool anyLeft;
            lock (_qbLock) { anyLeft = _qbSyncConnections.Count > 0; }
            await Clients.All.SendAsync("QBServiceStatusChanged", anyLeft);
        }

        // Check Camera
        string? disconnectedServiceId = null;
        bool wasCamera;
        lock (_cameraLock)
        {
            wasCamera = _cameraConnections.Remove(Context.ConnectionId, out disconnectedServiceId);
        }
        if (wasCamera)
        {
            await Clients.All.SendAsync("CameraServiceStatusChanged", GetConnectedCameraServiceIds());
            if (disconnectedServiceId != null)
                await Clients.All.SendAsync("CameraServiceDisconnected", disconnectedServiceId);
        }

        // Check Scale
        string? disconnectedScaleServiceId = null;
        bool wasScale;
        lock (_scaleLock)
        {
            wasScale = _scaleConnections.Remove(Context.ConnectionId, out disconnectedScaleServiceId);
        }
        if (wasScale)
        {
            await Clients.All.SendAsync("ScaleServiceStatusChanged", GetConnectedScaleServiceIds());
        }

        // Check Print
        bool wasPrint;
        lock (_printLock)
        {
            wasPrint = _printConnections.Remove(Context.ConnectionId, out _);
        }
        if (wasPrint)
        {
            await Clients.All.SendAsync("PrintServiceStatusChanged", GetConnectedPrintServiceIds());
        }

        // Check Reader
        string? disconnectedReaderServiceId = null;
        bool wasReader;
        lock (_readerLock)
        {
            wasReader = _readerConnections.Remove(Context.ConnectionId, out disconnectedReaderServiceId);
        }
        if (wasReader)
        {
            await Clients.All.SendAsync("ReaderServiceStatusChanged", GetConnectedReaderServiceIds());
            if (disconnectedReaderServiceId != null)
                await Clients.All.SendAsync("ReaderServiceDisconnected", disconnectedReaderServiceId);
        }

        // Check Signature Pad
        bool wasSignaturePad;
        lock (_signaturePadLock)
        {
            wasSignaturePad = _signaturePadConnections.Remove(Context.ConnectionId, out _);
        }
        if (wasSignaturePad)
        {
            await Clients.All.SendAsync("SignaturePadStatusChanged", GetConnectedSignaturePadIds());
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ===== SCALE READER SERVICE =====

    // Track connected scale services
    private static readonly Dictionary<string, string> _scaleConnections = new();
    private static readonly object _scaleLock = new();

    public async Task JoinScaleGroup(string serviceId = "default")
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "ScaleClients");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Scale_{serviceId}");
        lock (_scaleLock) { _scaleConnections[Context.ConnectionId] = serviceId; }
        await Clients.All.SendAsync("ScaleServiceStatusChanged", GetConnectedScaleServiceIds());
    }

    public Task<bool> CheckScaleServiceConnected()
    {
        lock (_scaleLock) { return Task.FromResult(_scaleConnections.Count > 0); }
    }

    /// <summary>
    /// Called by ScaleReaderService to send weight data to all web clients.
    /// Also updates the IScaleService singleton so the polling API (dashboard) gets fresh data.
    /// </summary>
    public async Task ScaleWeight(object weightData)
    {
        await Clients.All.SendAsync("ScaleWeight", weightData);

        // Update the ScaleWeightStore so /api/scale/weight (dashboard polling) returns current data
        try
        {
            if (weightData is System.Text.Json.JsonElement json)
            {
                var weightStore = Context.GetHttpContext()?.RequestServices.GetService<Services.ScaleWeightStore>();
                if (weightStore != null)
                {
                    string scaleId = json.TryGetProperty("scaleId", out var sid) ? sid.GetString() ?? "" : "";
                    string serviceId = json.TryGetProperty("serviceId", out var svcid) ? svcid.GetString() ?? "" : "";
                    int weight = json.TryGetProperty("weight", out var w) ? w.GetInt32() : 0;
                    bool motion = json.TryGetProperty("motion", out var m) && m.GetBoolean();
                    bool ok = json.TryGetProperty("ok", out var o) && o.GetBoolean();
                    string status = json.TryGetProperty("status", out var st) ? st.GetString() ?? "Unknown" : "Unknown";

                    weightStore.Update(scaleId, serviceId, weight, motion, ok, status);
                }
            }
        }
        catch { /* don't let parsing errors break the broadcast */ }
    }

    /// <summary>
    /// Called by ScaleReaderService when it connects/reconnects to announce its scales.
    /// </summary>
    public async Task ScaleServiceReady(object announcement)
    {
        await Clients.All.SendAsync("ScaleServiceReady", announcement);
    }

    public async Task ScaleListResponse(object scales)
    {
        await Clients.All.SendAsync("ScaleListReceived", scales);
    }

    public async Task RequestScaleList()
    {
        await Clients.Group("ScaleClients").SendAsync("GetScaleList");
    }

    public async Task RequestScaleBrands()
    {
        await Clients.Group("ScaleClients").SendAsync("GetScaleBrands");
    }

    public async Task ScaleBrandsResponse(object brands)
    {
        await Clients.All.SendAsync("ScaleBrandsReceived", brands);
    }

    // ===== SCALE CRUD RELAY (Web UI -> Scale Service) =====

    public async Task AddScaleToService(string serviceId, object scaleConfig)
    {
        await Clients.Group($"Scale_{serviceId}").SendAsync("AddScale", scaleConfig);
    }

    public async Task UpdateScaleOnService(string serviceId, string scaleId, object scaleConfig)
    {
        await Clients.Group($"Scale_{serviceId}").SendAsync("UpdateScale", scaleId, scaleConfig);
    }

    public async Task DeleteScaleFromService(string serviceId, string scaleId)
    {
        await Clients.Group($"Scale_{serviceId}").SendAsync("DeleteScale", scaleId);
    }

    // Scale service -> all web clients: CRUD result
    public async Task ScaleCrudResult(object result)
    {
        await Clients.All.SendAsync("ScaleCrudResult", result);
    }

    // ===== RFID CARD READER SERVICE =====

    // Track connected reader services: connectionId -> serviceId
    private static readonly Dictionary<string, string> _readerConnections = new();
    private static readonly object _readerLock = new();

    public async Task JoinReaderGroup(string serviceId = "default")
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "ReaderClients");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Reader_{serviceId}");
        lock (_readerLock) { _readerConnections[Context.ConnectionId] = serviceId; }
        await Clients.All.SendAsync("ReaderServiceStatusChanged", GetConnectedReaderServiceIds());
    }

    public Task<bool> CheckReaderServiceConnected()
    {
        lock (_readerLock) { return Task.FromResult(_readerConnections.Count > 0); }
    }

    public Task<List<string>> GetConnectedReaderServices()
    {
        return Task.FromResult(GetConnectedReaderServiceIds());
    }

    /// <summary>
    /// Called by the RFID Reader Service every time a card is presented.
    /// Broadcast to all web clients; a kiosk keeps only reads from the reader
    /// it is mapped to, and the Card Setup / Reader pages use them to capture
    /// a card number without typing.
    /// </summary>
    public async Task CardRead(object readData)
    {
        await Clients.All.SendAsync("CardRead", readData);
    }

    /// <summary>Called by the reader service on connect/reconnect to announce its readers.</summary>
    public async Task ReaderServiceReady(object announcement)
    {
        await Clients.All.SendAsync("ReaderServiceReady", announcement);
    }

    public async Task ReaderServiceDisconnected(string serviceId)
    {
        await Clients.All.SendAsync("ReaderServiceDisconnected", serviceId);
    }

    public async Task RequestReaderList()
    {
        await Clients.Group("ReaderClients").SendAsync("GetReaderList");
    }

    public async Task ReaderListResponse(object readers)
    {
        await Clients.All.SendAsync("ReaderListReceived", readers);
    }

    /// <summary>Ask a reader service which serial ports the host actually has,
    /// so the management page can offer a list instead of a free-text box.</summary>
    public async Task RequestSerialPorts(string serviceId)
    {
        await Clients.Group($"Reader_{serviceId}").SendAsync("GetSerialPorts");
    }

    public async Task SerialPortsResponse(object ports)
    {
        await Clients.All.SendAsync("SerialPortsReceived", ports);
    }

    // ===== READER CRUD RELAY (Web UI -> Reader Service) =====

    public async Task AddReaderToService(string serviceId, object readerConfig)
    {
        await Clients.Group($"Reader_{serviceId}").SendAsync("AddReader", readerConfig);
    }

    public async Task UpdateReaderOnService(string serviceId, string readerId, object readerConfig)
    {
        await Clients.Group($"Reader_{serviceId}").SendAsync("UpdateReader", readerId, readerConfig);
    }

    public async Task DeleteReaderFromService(string serviceId, string readerId)
    {
        await Clients.Group($"Reader_{serviceId}").SendAsync("DeleteReader", readerId);
    }

    public async Task ReaderCrudResult(object result)
    {
        await Clients.All.SendAsync("ReaderCrudResult", result);
    }

    /// <summary>Reader service -> web clients: raw frames captured while the
    /// management page has diagnostics open, so an unknown wire format can be
    /// worked out in the field.</summary>
    public async Task ReaderDiagnostic(object frame)
    {
        await Clients.All.SendAsync("ReaderDiagnostic", frame);
    }

    // ===== HELPERS =====

    private static List<string> GetConnectedCameraServiceIds()
    {
        lock (_cameraLock) { return _cameraConnections.Values.Distinct().ToList(); }
    }

    private static List<string> GetConnectedScaleServiceIds()
    {
        lock (_scaleLock) { return _scaleConnections.Values.Distinct().ToList(); }
    }

    private static List<string> GetConnectedPrintServiceIds()
    {
        lock (_printLock) { return _printConnections.Values.Distinct().ToList(); }
    }

    private static List<string> GetConnectedSignaturePadIds()
    {
        lock (_signaturePadLock) { return _signaturePadConnections.Values.Distinct().ToList(); }
    }

    private static List<string> GetConnectedReaderServiceIds()
    {
        lock (_readerLock) { return _readerConnections.Values.Distinct().ToList(); }
    }
}
