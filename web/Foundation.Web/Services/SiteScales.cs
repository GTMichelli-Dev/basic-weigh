using Foundation.Web.Data;
using Foundation.Web.Models;

namespace Foundation.Web.Services;

/// <summary>
/// Resolves a named site scale (Models.Scale) to a live reading. In live mode
/// the reading comes from the scale's hardware feed in ScaleWeightStore; in
/// demo mode each scale has its own simulator entry (serviceId "sim", scaleId
/// = the Scale row id) so multiple scales can be simulated independently.
/// </summary>
public class SiteScales
{
    public const string SimServiceId = "sim";

    private readonly ScaleWeightStore _store;

    public SiteScales(ScaleWeightStore store)
    {
        _store = store;
    }

    public record Reading(int Weight, bool Motion, bool Ok, bool Error, bool ComError,
                          string Status, int? ScaleDbId, string? ScaleName,
                          bool OnScale = true);

    /// <summary>The requested scale, or the default (first active) when id is null.</summary>
    public static Scale? Resolve(ScaleDbContext db, int? scaleDbId) =>
        scaleDbId.HasValue
            ? db.Scales.Find(scaleDbId.Value)
            : db.Scales.Where(s => s.Active)
                .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
                .FirstOrDefault();

    public Reading Read(Scale? scale, bool demoMode)
    {
        if (scale == null)
            return new Reading(0, false, false, true, true, "No scale configured", null, null);

        if (demoMode)
        {
            // Untouched simulator = a healthy scale sitting at zero.
            var sim = _store.Get($"{SimServiceId}:{scale.Id}");
            if (sim == null)
                return new Reading(0, false, true, false, false, "Ok", scale.Id, scale.Name);
            return new Reading(sim.Weight, sim.Motion, sim.Ok, !sim.Ok, false, sim.Status, scale.Id, scale.Name);
        }

        if (string.IsNullOrEmpty(scale.HardwareId))
            return new Reading(0, false, false, true, true, "No hardware feed", scale.Id, scale.Name);

        var reading = _store.Get(scale.HardwareId);
        if (reading == null)
            return new Reading(0, false, false, true, true, "COM Error", scale.Id, scale.Name);

        return new Reading(reading.Weight, reading.Motion, reading.Ok, !reading.Ok,
            reading.ComError, reading.Status, scale.Id, scale.Name, reading.OnScale);
    }

    /// <summary>Set a scale's simulator state (demo mode).</summary>
    public void Simulate(Scale scale, int weight, bool motion, bool error)
    {
        _store.Update(scale.Id.ToString(), SimServiceId, weight, motion, !error,
            error ? "Error" : (motion ? "Motion" : "Ok"), noTimeout: true);
    }

    /// <summary>
    /// Ticket printer for a weighment: the capturing scale's own printer when
    /// one is assigned (looked up by the scale name recorded on the ticket),
    /// otherwise the site-wide default from Setup.
    /// </summary>
    public static string? ResolvePrinter(ScaleDbContext db, string? scaleName, bool outbound, AppSetup setup)
    {
        if (!string.IsNullOrEmpty(scaleName))
        {
            var scale = db.Scales.FirstOrDefault(s => s.Name == scaleName);
            var assigned = outbound ? scale?.OutboundPrinterId : scale?.InboundPrinterId;
            if (!string.IsNullOrEmpty(assigned)) return assigned;
        }
        return outbound ? setup.OutboundPrinterId : setup.InboundPrinterId;
    }
}
