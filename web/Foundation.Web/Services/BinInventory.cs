using Foundation.Web.Data;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Shared bin-inventory math (Setup → Options → Use Bin Inventory). On-hand
/// per (bin, commodity) is computed from ticket history rather than a stored
/// counter, so it can be reported as of any date:
///   - truck arrived heavy (InWeight > OutWeight)  → load INTO the bin
///   - truck left heavy   (OutWeight > InWeight)   → load OUT of the bin
///   - plus manual BinAdjustments (true-ups for shrinkage, starting balances)
/// Used by the Bin Inventory report and by the Lock Bin to Commodity rule.
/// </summary>
public static class BinInventory
{
    public sealed class Row
    {
        public int LoadsIn; public long LbsIn;
        public int LoadsOut; public long LbsOut;
        public long AdjustmentLbs;
        public long OnHand => LbsIn - LbsOut + AdjustmentLbs;
    }

    /// <summary>Accumulate completed non-void binned tickets and adjustments
    /// into per-(bin, commodity) rows. cutoff = exclusive UTC upper bound, or
    /// null for all history.</summary>
    public static Dictionary<(string Bin, string Commodity), Row> Compute(ScaleDbContext db, DateTime? cutoff)
    {
        var rows = new Dictionary<(string, string), Row>();

        Row Get(string bin, string? commodity)
        {
            var key = (bin, commodity ?? "");
            if (!rows.TryGetValue(key, out var r)) rows[key] = r = new Row();
            return r;
        }

        var txns = db.Transactions
            .Where(t => !t.Void && t.DateOut != null && t.OutWeight != null
                        && t.Bin != null && t.Bin != "")
            .Where(t => cutoff == null || t.DateOut < cutoff)
            .Select(t => new { t.Bin, t.TransferToBin, t.Commodity, t.InWeight, t.OutWeight })
            .ToList();

        foreach (var t in txns)
        {
            var net = Math.Abs(t.InWeight - t.OutWeight!.Value);
            if (net == 0) continue;
            if (!string.IsNullOrEmpty(t.TransferToBin))
            {
                // Weighed bin-to-bin transfer: the net leaves the source bin
                // and lands in the destination, whichever weigh came first.
                var src = Get(t.Bin!, t.Commodity);
                src.LoadsOut++; src.LbsOut += net;
                var dst = Get(t.TransferToBin, t.Commodity);
                dst.LoadsIn++; dst.LbsIn += net;
                continue;
            }
            var r = Get(t.Bin!, t.Commodity);
            if (t.InWeight > t.OutWeight.Value) { r.LoadsIn++; r.LbsIn += net; }
            else { r.LoadsOut++; r.LbsOut += net; }
        }

        var adjustments = db.BinAdjustments
            .Where(a => cutoff == null || a.Date < cutoff)
            .ToList();
        foreach (var a in adjustments)
            Get(a.Bin, a.Commodity).AdjustmentLbs += a.AmountLbs;

        return rows;
    }

    /// <summary>
    /// Bin → the commodity it currently holds: the named commodity with a
    /// positive computed balance (largest wins if legacy data left a mix).
    /// A bin whose balances are all zero-or-below holds nothing and is free
    /// for any commodity — which is how a true-up to zero or hauling the bin
    /// empty releases the lock. An explicitly assigned commodity (Edit Tables
    /// → Bins, or Set Commodity on the Bin Report) overrides the computed
    /// contents, so an empty bin can be reserved for a commodity ahead of the
    /// first load.
    /// </summary>
    public static Dictionary<string, string> HeldCommodities(ScaleDbContext db)
    {
        var held = Compute(db, null)
            .Where(kv => kv.Key.Commodity != "" && kv.Value.OnHand > 0)
            .GroupBy(kv => kv.Key.Bin)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(kv => kv.Value.OnHand).First().Key.Commodity);

        foreach (var b in db.Bins.Where(b => b.Commodity != null && b.Commodity != "").ToList())
            held[b.BinName] = b.Commodity!;

        return held;
    }

    /// <summary>Bin → its assigned commodity, for bins that have one.</summary>
    public static Dictionary<string, string> AssignedCommodities(ScaleDbContext db) =>
        db.Bins.Where(b => b.Commodity != null && b.Commodity != "")
            .ToDictionary(b => b.BinName, b => b.Commodity!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gate for changing a bin's assigned commodity (null = clear to Empty):
    /// allowed while the bin's computed balance is zero-or-below, or when the
    /// new assignment matches what it already holds. A bin still holding a
    /// different commodity returns an error message (null = change is fine).
    /// </summary>
    public static string? ValidateAssignment(ScaleDbContext db, string bin, string? commodity)
    {
        var heldNow = Compute(db, null)
            .Where(kv => string.Equals(kv.Key.Bin, bin, StringComparison.OrdinalIgnoreCase)
                         && kv.Key.Commodity != "" && kv.Value.OnHand > 0)
            .OrderByDescending(kv => kv.Value.OnHand)
            .Select(kv => kv.Key.Commodity)
            .FirstOrDefault();

        if (heldNow == null) return null;
        if (commodity != null && string.Equals(commodity, heldNow, StringComparison.OrdinalIgnoreCase)) return null;

        return $"Bin \"{bin}\" still has {heldNow} on hand — empty it or true up to zero before changing its commodity.";
    }

    /// <summary>
    /// Lock Bin to Commodity enforcement for every ticket save path (weigh
    /// forms, grid edits, kiosk, table API). When the ticket's bin currently
    /// holds a commodity: a blank ticket commodity is auto-set to it, and a
    /// different one returns an error message (null = ticket is fine). No-op
    /// while the feature toggles are off.
    /// </summary>
    public static string? ValidateTicket(ScaleDbContext db, AppSetup setup, Transaction t)
    {
        if (string.IsNullOrWhiteSpace(t.TransferToBin)) t.TransferToBin = null;
        if (!setup.UseBinInventory) return null;
        // A void ticket doesn't count toward any bin — never block voiding.
        if (t.Void) return null;

        // Weighed transfer sanity, independent of the commodity lock.
        if (t.TransferToBin != null)
        {
            if (string.IsNullOrWhiteSpace(t.Bin))
                return "A transfer needs a source bin — pick the Bin the load came out of.";
            if (string.Equals(t.Bin, t.TransferToBin, StringComparison.OrdinalIgnoreCase))
                return "Transfer To must be a different bin than the source Bin.";
        }

        if (!setup.BinCommodityLock) return null;
        if (string.IsNullOrWhiteSpace(t.Bin)) return null;

        var heldMap = HeldCommodities(db);

        if (heldMap.TryGetValue(t.Bin, out var held))
        {
            if (string.IsNullOrWhiteSpace(t.Commodity))
                t.Commodity = held;
            else if (!string.Equals(t.Commodity, held, StringComparison.OrdinalIgnoreCase))
                return $"Bin \"{t.Bin}\" holds {held} — it can't take {t.Commodity}. Empty or true up the bin first.";
        }

        // The destination of a transfer must be unassigned/empty or match.
        if (t.TransferToBin != null
            && !string.IsNullOrWhiteSpace(t.Commodity)
            && heldMap.TryGetValue(t.TransferToBin, out var destHeld)
            && !string.Equals(destHeld, t.Commodity, StringComparison.OrdinalIgnoreCase))
            return $"Bin \"{t.TransferToBin}\" holds {destHeld} — it can't take {t.Commodity}. Empty or true up the bin first.";

        return null;
    }
}
