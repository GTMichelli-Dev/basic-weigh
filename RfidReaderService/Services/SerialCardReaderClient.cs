using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using RfidReaderService.Models;

namespace RfidReaderService.Services;

/// <summary>One card presentation as it came off the wire.</summary>
public class CardFrame
{
    /// <summary>Parsed card number, or empty when the frame didn't yield one.</summary>
    public string CardNumber { get; set; } = "";
    /// <summary>Frame bytes as printable text, control codes escaped.</summary>
    public string RawText { get; set; } = "";
    /// <summary>Frame bytes as hex — the only reliable way to work out an
    /// undocumented reader's format in the field.</summary>
    public string RawHex { get; set; } = "";
    public bool Ok => CardNumber.Length > 0;
    /// <summary>Why a frame was rejected, for the diagnostics panel.</summary>
    public string? Reason { get; set; }
    /// <summary>Wiegand formats only: the facility (site) code decoded alongside
    /// the card number. Always reported so the Readers page can show it, even
    /// when it isn't part of the card number itself.</summary>
    public string? FacilityCode { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Reads card presentations from an RS-232 prox reader.
///
/// Framing is deliberately permissive because these readers disagree with each
/// other: some terminate with CR, some CR+LF, some wrap the payload in
/// STX/ETX, and some just emit the digits and go quiet. Bytes are accumulated
/// and a frame is closed by a terminator byte OR by the line falling silent
/// for IdleFrameMs — so a reader that terminates nothing still produces one
/// frame per card, and a reader that terminates properly is never delayed.
/// </summary>
public sealed class SerialCardReaderClient
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Cr = 0x0D;
    private const byte Lf = 0x0A;

    private readonly ILogger<SerialCardReaderClient> _log;

    public SerialCardReaderClient(ILogger<SerialCardReaderClient> log)
    {
        _log = log;
    }

    /// <summary>
    /// Holds the port open and invokes <paramref name="onFrame"/> for every
    /// frame — including frames that failed to parse, so the management page
    /// can show what the reader actually sent. Returns when cancelled; throws
    /// on port failure so the caller can reopen.
    /// </summary>
    public async Task ReadStreamAsync(
        ReaderConfigEntity reader,
        Func<CardFrame, Task> onFrame,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reader.SerialPortName))
            throw new InvalidOperationException($"Reader '{reader.ReaderId}' has no serial port configured.");

        using var port = new SerialPort(
            reader.SerialPortName,
            reader.BaudRate > 0 ? reader.BaudRate : 9600,
            ParseParity(reader.Parity),
            reader.DataBits > 0 ? reader.DataBits : 8,
            ParseStopBits(reader.StopBits))
        {
            ReadTimeout = 100,   // short: the read loop uses timeouts as its clock
            WriteTimeout = reader.TimeoutMs > 0 ? reader.TimeoutMs : 500,
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            // Many readers are bus-powered from the handshake lines or simply
            // refuse to talk until DTR/RTS are asserted.
            DtrEnable = true,
            RtsEnable = true
        };

        port.Open();
        _log.LogInformation(
            "Serial port {Port} opened ({Baud},{Bits},{Par},{Stop}) for reader '{ReaderId}'",
            reader.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, reader.ReaderId);

        var regex = CompileRegex(reader);
        var buffer = new List<byte>(64);
        var idleFrameMs = reader.IdleFrameMs > 0 ? reader.IdleFrameMs : 60;
        var chunk = new byte[256];
        DateTime lastByteAt = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = 0;
                try
                {
                    // The blocking read is bounded by ReadTimeout (100ms), so
                    // cancellation is honoured promptly without needing to
                    // interrupt the underlying syscall.
                    read = await Task.Run(() => port.Read(chunk, 0, chunk.Length), ct);
                }
                catch (TimeoutException)
                {
                    read = 0;
                }

                if (read > 0)
                {
                    lastByteAt = DateTime.UtcNow;
                    for (int i = 0; i < read; i++)
                    {
                        var b = chunk[i];
                        if (b == Stx)
                        {
                            // Start of a new frame — anything buffered was noise.
                            buffer.Clear();
                        }
                        else if (b == Cr || b == Lf || b == Etx || b == 0x00)
                        {
                            if (buffer.Count > 0) await Emit(buffer, reader, regex, onFrame, ct);
                        }
                        else
                        {
                            buffer.Add(b);
                            // Runaway guard: a stuck line must not grow forever.
                            if (buffer.Count > 256) buffer.Clear();
                        }
                    }
                }
                else if (buffer.Count > 0 && (DateTime.UtcNow - lastByteAt).TotalMilliseconds >= idleFrameMs)
                {
                    // Unterminated reader: silence ends the frame.
                    await Emit(buffer, reader, regex, onFrame, ct);
                }
            }
        }
        finally
        {
            try { if (port.IsOpen) port.Close(); }
            catch { /* ignore */ }
        }
    }

    private async Task Emit(List<byte> buffer, ReaderConfigEntity reader, Regex? regex,
        Func<CardFrame, Task> onFrame, CancellationToken ct)
    {
        var bytes = buffer.ToArray();
        buffer.Clear();

        var frame = Parse(bytes, reader, regex);

        // A downstream hiccup (SignalR blip) must never kill the read loop —
        // the next card presentation should still be delivered.
        try
        {
            await onFrame(frame);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Reader '{ReaderId}' frame handler failed: {Msg}", reader.ReaderId, ex.Message);
        }
    }

    /// <summary>
    /// Turn raw frame bytes into a card number. Exposed (and static) so the
    /// parsing rules can be exercised without a serial port.
    /// </summary>
    public static CardFrame Parse(byte[] bytes, ReaderConfigEntity reader, Regex? regex = null)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var frame = new CardFrame
        {
            RawText = Escape(text),
            RawHex = BitConverter.ToString(bytes)
        };

        regex ??= CompileRegex(reader);

        // Printable characters only — a stray parity glitch shouldn't end up
        // inside a card number.
        var clean = new string(text.Where(c => c >= 32 && c < 127).ToArray()).Trim();
        if (clean.Length == 0)
        {
            frame.Reason = "empty frame";
            return frame;
        }

        // Wiegand is a decode, not a text extraction, so it runs before the
        // string-shaped formats below and returns directly.
        if (reader.Format == "Wiegand26")
            return ParseWiegand26(clean, reader, frame);

        string candidate;
        if (regex != null)
        {
            var m = regex.Match(clean);
            if (!m.Success)
            {
                frame.Reason = "regex did not match";
                return frame;
            }
            candidate = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
        }
        else
        {
            candidate = reader.Format switch
            {
                "Digits" => LongestRun(clean, char.IsDigit),
                "Hex" => LongestRun(clean, IsHex).ToUpperInvariant(),
                "Raw" => clean,
                // Auto: an all-hex frame is taken whole (that covers readers
                // that emit the raw card data as hex, where a digits-only
                // reading would silently truncate at the first letter);
                // anything else falls back to its longest digit run.
                _ => clean.All(IsHex) ? clean.ToUpperInvariant() : LongestRun(clean, char.IsDigit)
            };
        }

        if (reader.StripLeadingZeros && candidate.Length > 1)
            candidate = candidate.TrimStart('0');

        if (candidate.Length == 0)
        {
            frame.Reason = "no card number in frame";
            return frame;
        }
        if (candidate.Length < Math.Max(1, reader.MinLength))
        {
            frame.Reason = $"shorter than minimum length ({reader.MinLength})";
            return frame;
        }
        if (candidate.Length > 50) candidate = candidate[..50];

        frame.CardNumber = candidate;
        return frame;
    }

    /// <summary>
    /// Decode a 26-bit Wiegand (HID H10301) credential that the reader sends as
    /// ASCII hex. This is the format an AWID Sentinel-Prox emits over RS-232.
    ///
    /// Seven hex characters carry 28 bits; the last two are padding and are
    /// dropped, leaving the 26-bit frame:
    ///
    ///     bit  0        even parity
    ///     bits 1..8     facility (site) code, 8 bits
    ///     bits 9..24    card number, 16 bits
    ///     bit  25       odd parity
    ///
    /// e.g. "3DD9370" -> facility 123, card 45678.
    ///
    /// The card number alone is reported by default, matching the numbers
    /// printed on the cards. Parity is not verified: a card that reads fine on
    /// existing systems must not be rejected here over a parity rule.
    /// </summary>
    private static CardFrame ParseWiegand26(string clean, ReaderConfigEntity reader, CardFrame frame)
    {
        const int HexChars = 7;   // 28 bits
        const int FrameBits = 26; // after dropping the 2 pad bits

        var hex = LongestRun(clean, IsHex);
        if (hex.Length < HexChars)
        {
            frame.Reason = $"need {HexChars} hex characters for 26-bit Wiegand, got {hex.Length}";
            return frame;
        }

        var bits = new StringBuilder(HexChars * 4);
        foreach (var c in hex[..HexChars])
            bits.Append(Convert.ToString(Convert.ToInt32(c.ToString(), 16), 2).PadLeft(4, '0'));

        var wiegand = bits.ToString()[..FrameBits];
        var facility = Convert.ToUInt64(wiegand.Substring(1, 8), 2);
        var card = Convert.ToUInt64(wiegand.Substring(9, 16), 2);

        frame.FacilityCode = facility.ToString();
        // A site that reuses card numbers across facility codes needs both to
        // stay unique; most don't, so the bare card number is the default.
        frame.CardNumber = reader.IncludeFacilityCode
            ? $"{facility}-{card}"
            : card.ToString();
        return frame;
    }

    private static Regex? CompileRegex(ReaderConfigEntity reader)
    {
        if (string.IsNullOrWhiteSpace(reader.CardNumberRegex)) return null;
        try
        {
            return new Regex(reader.CardNumberRegex, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // A malformed regex falls back to format-based parsing rather than
            // taking the reader out of service.
            return null;
        }
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Longest consecutive run of characters matching a predicate.</summary>
    private static string LongestRun(string value, Func<char, bool> predicate)
    {
        int bestStart = 0, bestLen = 0, start = -1;
        for (int i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && predicate(value[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                var len = i - start;
                if (len > bestLen) { bestLen = len; bestStart = start; }
                start = -1;
            }
        }
        return bestLen == 0 ? "" : value.Substring(bestStart, bestLen);
    }

    private static string Escape(string text) => text
        .Replace("\x02", "<STX>").Replace("\x03", "<ETX>")
        .Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\0", "<NUL>");

    private static Parity ParseParity(string? parity) => (parity ?? "None").ToLowerInvariant() switch
    {
        "even" => System.IO.Ports.Parity.Even,
        "odd" => System.IO.Ports.Parity.Odd,
        "mark" => System.IO.Ports.Parity.Mark,
        "space" => System.IO.Ports.Parity.Space,
        _ => System.IO.Ports.Parity.None
    };

    private static StopBits ParseStopBits(int stopBits) => stopBits switch
    {
        0 => System.IO.Ports.StopBits.OnePointFive,
        2 => System.IO.Ports.StopBits.Two,
        _ => System.IO.Ports.StopBits.One
    };

    /// <summary>
    /// Serial ports the host offers. SerialPort.GetPortNames misses some
    /// USB-serial adapters on Linux, so the /dev entries are globbed too.
    /// </summary>
    public static List<string> ListPorts()
    {
        var ports = new HashSet<string>(SerialPort.GetPortNames());

        if (!OperatingSystem.IsWindows())
        {
            foreach (var pattern in new[] { "ttyUSB*", "ttyACM*", "ttyAMA*", "ttyS*" })
            {
                try
                {
                    foreach (var path in Directory.GetFiles("/dev", pattern))
                        ports.Add(path);
                }
                catch { /* /dev unreadable — nothing to add */ }
            }

            // /dev/serial/by-id names carry the adapter's own vendor/model/serial,
            // so they keep pointing at the same physical cable after a replug that
            // renumbers ttyUSB*. Offered alongside the raw devices and sorted ahead
            // of them, so the picker's first suggestion is the one that survives.
            try
            {
                foreach (var link in Directory.GetFiles("/dev/serial/by-id"))
                    ports.Add(link);
            }
            catch { /* no USB serial adapters attached */ }
        }

        return ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
