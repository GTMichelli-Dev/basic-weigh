namespace RfidReaderService.Models;

/// <summary>
/// A partial reader update, from either the local REST API or a relayed edit
/// from the web app's Reader Management page.
///
/// Every property is nullable on purpose. Binding the entity itself would mean
/// (a) model validation demanding the ReaderId the route already carries, and
/// (b) an omitted bool arriving as <c>false</c> — so an update that only meant
/// to change the baud rate would quietly deactivate the reader.
/// </summary>
public class ReaderUpdateRequest
{
    public string? DisplayName { get; set; }
    public string? ReaderModel { get; set; }
    public string? SerialPortName { get; set; }
    public int? BaudRate { get; set; }
    public int? DataBits { get; set; }
    public string? Parity { get; set; }
    public int? StopBits { get; set; }
    public string? Format { get; set; }
    /// <summary>Empty string clears the regex; null leaves it alone.</summary>
    public string? CardNumberRegex { get; set; }
    public bool? StripLeadingZeros { get; set; }
    public bool? IncludeFacilityCode { get; set; }
    public int? MinLength { get; set; }
    public int? DebounceMs { get; set; }
    public int? IdleFrameMs { get; set; }
    public int? TimeoutMs { get; set; }
    public bool? Active { get; set; }

    /// <summary>Copy the properties that were actually sent onto a reader.</summary>
    public void ApplyTo(ReaderConfigEntity reader)
    {
        if (DisplayName != null) reader.DisplayName = DisplayName;
        if (ReaderModel != null) reader.ReaderModel = ReaderModel;
        if (SerialPortName != null) reader.SerialPortName = SerialPortName;
        if (BaudRate is > 0) reader.BaudRate = BaudRate.Value;
        if (DataBits is > 0) reader.DataBits = DataBits.Value;
        if (!string.IsNullOrWhiteSpace(Parity)) reader.Parity = Parity;
        if (StopBits is >= 0) reader.StopBits = StopBits.Value;
        if (!string.IsNullOrWhiteSpace(Format)) reader.Format = Format;
        if (CardNumberRegex != null)
            reader.CardNumberRegex = CardNumberRegex.Length == 0 ? null : CardNumberRegex;
        if (StripLeadingZeros.HasValue) reader.StripLeadingZeros = StripLeadingZeros.Value;
        if (IncludeFacilityCode.HasValue) reader.IncludeFacilityCode = IncludeFacilityCode.Value;
        if (MinLength is > 0) reader.MinLength = MinLength.Value;
        if (DebounceMs is >= 0) reader.DebounceMs = DebounceMs.Value;
        if (IdleFrameMs is > 0) reader.IdleFrameMs = IdleFrameMs.Value;
        if (TimeoutMs is > 0) reader.TimeoutMs = TimeoutMs.Value;
        if (Active.HasValue) reader.Active = Active.Value;
    }
}

/// <summary>Partial settings update — same reasoning as ReaderUpdateRequest.</summary>
public class SettingsUpdateRequest
{
    public string? ServiceId { get; set; }
    public string? ServerUrl { get; set; }
    public string? SignalRHub { get; set; }
}
