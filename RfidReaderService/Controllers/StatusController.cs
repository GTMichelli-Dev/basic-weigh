using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RfidReaderService.Data;
using RfidReaderService.Models;
using RfidReaderService.Services;

namespace RfidReaderService.Controllers;

/// <summary>
/// Local REST API for the service. Everything here is also reachable from the
/// web app's Reader Management page over SignalR — this exists for the case
/// where the hub connection is the thing that's broken, and someone needs to
/// look at the reader from the machine it's plugged into.
/// </summary>
[ApiController]
[Route("api")]
public class StatusController : ControllerBase
{
    private readonly RfidDbContext _db;
    private readonly CardReadStore _reads;
    private readonly RestartSignal _restart;
    private readonly AnnounceSignal _announce;

    public StatusController(RfidDbContext db, CardReadStore reads, RestartSignal restart, AnnounceSignal announce)
    {
        _db = db;
        _reads = reads;
        _restart = restart;
        _announce = announce;
    }

    /// <summary>Liveness check.</summary>
    [HttpGet("health")]
    public IActionResult Health() => Ok(new
    {
        status = "ok",
        service = "RFID Reader Service",
        version = typeof(RfidWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        utc = DateTime.UtcNow
    });

    /// <summary>Per-reader state: last card seen, frame counts, recent frames.</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var settings = _db.Settings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefault();
        var readers = _db.Readers.AsNoTracking().OrderBy(r => r.ReaderId).ToList();

        return Ok(new
        {
            serviceId = settings?.ServiceId ?? "default",
            serverUrl = settings?.ServerUrl,
            readers = readers.Select(r =>
            {
                var state = _reads.Get(r.ReaderId);
                return new
                {
                    r.ReaderId,
                    r.DisplayName,
                    r.SerialPortName,
                    r.Active,
                    status = state?.Status ?? "Not started",
                    lastCardNumber = state?.LastCardNumber,
                    lastCardAt = state?.LastCardAt,
                    lastFrameAt = state?.LastFrameAt,
                    totalFrames = state?.TotalFrames ?? 0,
                    totalCards = state?.TotalCards ?? 0
                };
            })
        });
    }

    /// <summary>
    /// The last frames a reader produced, parsed or not. This is the tool for
    /// commissioning an undocumented reader: present a card, read the hex, set
    /// Format or CardNumberRegex to match.
    /// </summary>
    [HttpGet("readers/{readerId}/frames")]
    public IActionResult Frames(string readerId)
    {
        var state = _reads.Get(readerId);
        if (state == null) return NotFound(new { message = $"No reads recorded for '{readerId}'." });
        lock (state) { return Ok(state.Recent.ToList()); }
    }

    /// <summary>Serial ports this machine offers.</summary>
    [HttpGet("serialports")]
    public IActionResult SerialPorts() => Ok(SerialCardReaderClient.ListPorts());

    // ===== SETTINGS =====

    [HttpGet("settings")]
    public IActionResult GetSettings() =>
        Ok(_db.Settings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefault());

    /// <summary>Change the service id / server URL. Restarts the worker so the
    /// new values take effect without a service restart.</summary>
    [HttpPut("settings")]
    public IActionResult UpdateSettings([FromBody] SettingsUpdateRequest body)
    {
        var settings = _db.Settings.OrderBy(s => s.Id).FirstOrDefault();
        if (settings == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(body.ServiceId)) settings.ServiceId = body.ServiceId.Trim();
        if (!string.IsNullOrWhiteSpace(body.ServerUrl)) settings.ServerUrl = body.ServerUrl.Trim();
        if (!string.IsNullOrWhiteSpace(body.SignalRHub)) settings.SignalRHub = body.SignalRHub.Trim();
        _db.SaveChanges();

        _restart.TriggerRestart();
        return Ok(settings);
    }

    // ===== READERS =====

    [HttpGet("readers")]
    public IActionResult GetReaders() =>
        Ok(_db.Readers.AsNoTracking().OrderBy(r => r.ReaderId).ToList());

    [HttpPost("readers")]
    public IActionResult AddReader([FromBody] ReaderConfigEntity reader)
    {
        if (string.IsNullOrWhiteSpace(reader.ReaderId))
            return BadRequest(new { message = "readerId is required." });
        if (_db.Readers.Any(r => r.ReaderId == reader.ReaderId))
            return BadRequest(new { message = $"Reader '{reader.ReaderId}' already exists." });

        reader.Id = 0;
        _db.Readers.Add(reader);
        _db.SaveChanges();
        _restart.TriggerRestart();
        _announce.TriggerAnnounce();
        return Ok(reader);
    }

    /// <summary>
    /// Partial update — the reader is identified by the route, and only the
    /// properties present in the body are changed. Bound to a nullable request
    /// type rather than the entity: the entity's non-nullable ReaderId would
    /// make model validation reject every body that (correctly) omits it.
    /// </summary>
    [HttpPut("readers/{readerId}")]
    public IActionResult UpdateReader(string readerId, [FromBody] ReaderUpdateRequest body)
    {
        var reader = _db.Readers.FirstOrDefault(r => r.ReaderId == readerId);
        if (reader == null) return NotFound();

        body.ApplyTo(reader);

        _db.SaveChanges();
        _restart.TriggerRestart();
        _announce.TriggerAnnounce();
        return Ok(reader);
    }

    [HttpDelete("readers/{readerId}")]
    public IActionResult DeleteReader(string readerId)
    {
        var reader = _db.Readers.FirstOrDefault(r => r.ReaderId == readerId);
        if (reader == null) return NotFound();

        _db.Readers.Remove(reader);
        _db.SaveChanges();
        _restart.TriggerRestart();
        _announce.TriggerAnnounce();
        return Ok(new { deleted = readerId });
    }

    /// <summary>
    /// Parse a frame you captured by hand against a reader's current settings,
    /// without presenting a card. Takes either hex ("02-31-32-03") or text.
    /// </summary>
    [HttpPost("readers/{readerId}/testparse")]
    public IActionResult TestParse(string readerId, [FromBody] TestParseRequest body)
    {
        var reader = _db.Readers.AsNoTracking().FirstOrDefault(r => r.ReaderId == readerId);
        if (reader == null) return NotFound();

        byte[] bytes;
        if (!string.IsNullOrWhiteSpace(body.Hex))
        {
            try
            {
                bytes = body.Hex.Split(new[] { '-', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => Convert.ToByte(h, 16)).ToArray();
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "Could not read that as hex: " + ex.Message });
            }
        }
        else
        {
            bytes = System.Text.Encoding.ASCII.GetBytes(body.Text ?? "");
        }

        return Ok(SerialCardReaderClient.Parse(bytes, reader));
    }

    public class TestParseRequest
    {
        /// <summary>Hex bytes, e.g. "02-30-31-32-03". Wins over Text.</summary>
        public string? Hex { get; set; }
        public string? Text { get; set; }
    }
}
