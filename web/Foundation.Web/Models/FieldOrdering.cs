namespace Foundation.Web.Models;

/// <summary>
/// One renderable field on a weigh form: either a standard field (Key =
/// "Commodity", "Customer", …, Field null) or a custom field (Key = "cf_{id}",
/// Field set). Produced by FieldOrdering.GetFormSlots in display order.
/// </summary>
public record FieldSlot(string Key, int Order, CustomField? Field);

/// <summary>
/// Merges the standard fields (AppSetup.FieldOrder*) and active custom fields
/// (CustomField.SortOrder) into one ordered list for the weigh forms. Hidden
/// standard fields and inactive custom fields are excluded.
/// </summary>
public static class FieldOrdering
{
    public static List<FieldSlot> GetFormSlots(AppSetup setup, IEnumerable<CustomField> customFields)
    {
        var slots = new List<FieldSlot>();

        void Std(string key, bool hidden, int order)
        {
            if (!hidden) slots.Add(new FieldSlot(key, order, null));
        }

        Std("Commodity", setup.HideCommodity, setup.FieldOrderCommodity);
        Std("Customer", setup.HideCustomer, setup.FieldOrderCustomer);
        Std("Carrier", setup.HideCarrier, setup.FieldOrderCarrier);
        Std("TruckId", setup.HideTruckId, setup.FieldOrderTruckId);
        Std("Location", setup.HideLocation, setup.FieldOrderLocation);
        Std("Destination", setup.HideDestination, setup.FieldOrderDestination);
        // Bin has no Hide flag — the Bin Inventory toggle is its visibility.
        Std("Bin", !setup.UseBinInventory, setup.FieldOrderBin);
        Std("Notes", setup.HideNotes, setup.FieldOrderNotes);

        // Formula fields are computed at save time — they show in grids and
        // on tickets, but there's nothing to type on the weigh forms.
        foreach (var f in customFields.Where(f => f.Active && f.FieldType != "Formula"))
            slots.Add(new FieldSlot($"cf_{f.Id}", f.SortOrder, f));

        return slots
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Field == null ? 0 : 1) // ties: standard field first
            .ThenBy(s => s.Field?.Name ?? s.Key)
            .ToList();
    }

    /// <summary>
    /// How many slots belong in the left column so both columns end up visually
    /// even. Fixed items (ticket number left, weight/date rows right) count as
    /// slot-sized rows.
    /// </summary>
    public static int LeftColumnCount(int slotCount, int leftFixed, int rightFixed)
    {
        var left = (slotCount + rightFixed - leftFixed + 1) / 2;
        return Math.Clamp(left, 0, slotCount);
    }
}
