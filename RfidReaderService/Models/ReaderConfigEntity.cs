using System.ComponentModel.DataAnnotations;

namespace RfidReaderService.Models;

/// <summary>
/// A configured card reader stored in the local SQLite database.
///
/// The wire format of RS-232 prox readers is not standardised — AWID does not
/// publish the SP-6820's serial frame — so everything about how a frame is
/// framed and parsed is configuration rather than code. The Reader Management
/// page shows the raw bytes of every read, which is enough to fill these in
/// against a reader nobody has seen before.
/// </summary>
public class ReaderConfigEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>Unique string identifier for this reader (e.g. "kiosk-1-reader").</summary>
    [Required]
    [StringLength(50)]
    public string ReaderId { get; set; } = "";

    /// <summary>Human-friendly name (e.g. "Inbound Kiosk Reader").</summary>
    [StringLength(100)]
    public string DisplayName { get; set; } = "";

    /// <summary>Reader model, informational only — parsing is driven by the
    /// fields below, not by the model name.</summary>
    [StringLength(50)]
    public string ReaderModel { get; set; } = "AWID Sentinel-Prox SP-6820";

    // ---- Serial port ----

    /// <summary>Port name ("COM3", "/dev/ttyUSB0").</summary>
    [StringLength(50)]
    public string? SerialPortName { get; set; }

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    /// <summary>"None", "Even", "Odd", "Mark", "Space".</summary>
    [StringLength(10)]
    public string Parity { get; set; } = "None";

    /// <summary>1, 2, or 0 for OnePointFive.</summary>
    public int StopBits { get; set; } = 1;

    // ---- Framing / parsing ----

    /// <summary>
    /// How a card number is pulled out of a frame:
    ///   "Auto"       — regex if one is set, else the frame's hex/digit payload.
    ///   "Digits"     — the longest run of decimal digits.
    ///   "Hex"        — hex characters only, upper-cased.
    ///   "Raw"        — the whole printable frame, trimmed.
    ///   "Wiegand26"  — decode 7 ASCII hex characters as a 26-bit Wiegand
    ///                  credential and report the card number in decimal. This
    ///                  is what an AWID Sentinel-Prox sends over RS-232.
    ///
    /// The first four extract text; Wiegand26 decodes bits, so MinLength and
    /// StripLeadingZeros don't apply to it.
    /// </summary>
    [StringLength(20)]
    public string Format { get; set; } = "Auto";

    /// <summary>
    /// Wiegand formats only: report the card number as "facility-card" instead
    /// of the bare card number. Needed only where card numbers repeat across
    /// facility codes — the printed number is normally the card number alone.
    /// </summary>
    public bool IncludeFacilityCode { get; set; }

    /// <summary>
    /// Optional regex; group 1 is the card number. Wins over Format when set —
    /// this is how a reader with an unusual frame ("F1234,C0056789\r") is
    /// onboarded without a code change.
    /// </summary>
    [StringLength(200)]
    public string? CardNumberRegex { get; set; }

    /// <summary>
    /// Drop leading zeros from the parsed number. Off by default: leading
    /// zeros are part of the number printed on most cards, and enrollment
    /// matches the reader's output exactly.
    /// </summary>
    public bool StripLeadingZeros { get; set; }

    /// <summary>
    /// A frame shorter than this is treated as line noise rather than a card.
    /// Guards against a floating RS-232 line producing phantom reads.
    /// </summary>
    public int MinLength { get; set; } = 4;

    /// <summary>
    /// A prox reader repeats the number for as long as the card is held near
    /// it. Repeats of the same card inside this window are swallowed so the
    /// kiosk sees one presentation.
    /// </summary>
    public int DebounceMs { get; set; } = 3000;

    /// <summary>
    /// A reader that sends no terminator is framed by silence instead: bytes
    /// that stop arriving for this long are treated as a complete frame.
    /// </summary>
    public int IdleFrameMs { get; set; } = 60;

    /// <summary>Serial read timeout in milliseconds.</summary>
    public int TimeoutMs { get; set; } = 500;

    public bool Active { get; set; } = true;
}
