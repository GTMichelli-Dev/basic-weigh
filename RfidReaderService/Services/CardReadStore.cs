using System.Collections.Concurrent;

namespace RfidReaderService.Services;

/// <summary>
/// In-memory record of what each reader has seen. Written by the read loops,
/// read by the REST API — this is what a technician looks at when a reader is
/// wired up but the kiosk sees nothing, since it shows rejected frames too.
/// </summary>
public class CardReadStore
{
    private const int HistoryLimit = 25;

    private readonly ConcurrentDictionary<string, ReaderState> _states = new();

    public void RecordFrame(string readerId, CardFrame frame, bool debounced)
    {
        var state = _states.GetOrAdd(readerId, _ => new ReaderState { ReaderId = readerId });
        lock (state)
        {
            state.LastFrameAt = DateTime.UtcNow;
            state.TotalFrames++;
            if (frame.Ok && !debounced)
            {
                state.LastCardNumber = frame.CardNumber;
                state.LastCardAt = DateTime.UtcNow;
                state.TotalCards++;
            }
            state.Recent.Insert(0, new RecentFrame
            {
                CardNumber = frame.CardNumber,
                RawText = frame.RawText,
                RawHex = frame.RawHex,
                Ok = frame.Ok,
                Debounced = debounced,
                Reason = frame.Reason,
                ReadAt = frame.ReadAt
            });
            if (state.Recent.Count > HistoryLimit)
                state.Recent.RemoveRange(HistoryLimit, state.Recent.Count - HistoryLimit);
        }
    }

    public void SetStatus(string readerId, string status)
    {
        var state = _states.GetOrAdd(readerId, _ => new ReaderState { ReaderId = readerId });
        lock (state) { state.Status = status; }
    }

    public ReaderState? Get(string readerId) =>
        _states.TryGetValue(readerId, out var s) ? s : null;

    public IReadOnlyCollection<ReaderState> GetAll() => _states.Values.ToList();

    public class ReaderState
    {
        public string ReaderId { get; set; } = "";
        public string Status { get; set; } = "Starting";
        public string? LastCardNumber { get; set; }
        public DateTime? LastCardAt { get; set; }
        public DateTime? LastFrameAt { get; set; }
        public long TotalFrames { get; set; }
        public long TotalCards { get; set; }
        public List<RecentFrame> Recent { get; set; } = new();
    }

    public class RecentFrame
    {
        public string CardNumber { get; set; } = "";
        public string RawText { get; set; } = "";
        public string RawHex { get; set; } = "";
        public bool Ok { get; set; }
        public bool Debounced { get; set; }
        public string? Reason { get; set; }
        public DateTime ReadAt { get; set; }
    }
}
