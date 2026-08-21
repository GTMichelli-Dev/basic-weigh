using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

public class AppSetup
{
    [Key]
    public int Id { get; set; }

    [Display(Name = "Next Ticket Number")]
    public int TicketNumber { get; set; } = 1;

    [StringLength(100)]
    [Display(Name = "Header Line 1")]
    public string? Header1 { get; set; }

    [StringLength(100)]
    [Display(Name = "Header Line 2")]
    public string? Header2 { get; set; }

    [StringLength(100)]
    [Display(Name = "Header Line 3")]
    public string? Header3 { get; set; }

    [StringLength(100)]
    [Display(Name = "Header Line 4")]
    public string? Header4 { get; set; }

    [StringLength(100)]
    [Display(Name = "Printer Name")]
    public string? PrinterName { get; set; }

    [Display(Name = "Tickets Per Page")]
    public int TicketsPerPage { get; set; } = 1;

    [Display(Name = "Demo Mode")]
    public bool DemoMode { get; set; }

    [Display(Name = "Kiosk Count")]
    public int KioskCount { get; set; }

    [Display(Name = "Icon")]
    public byte[]? Icon { get; set; }

    [StringLength(50)]
    [Display(Name = "Icon Content Type")]
    public string? IconContentType { get; set; }

    [StringLength(20)]
    [Display(Name = "Theme")]
    public string Theme { get; set; } = "default";

    /// <summary>
    /// Site default language for the driver-facing screens (kiosk, phone,
    /// signature pad, ticket views): "en" or "es". A device overrides it with
    /// the on-screen toggle, or with ?lang= in its URL — a kiosk Pi pinned to
    /// Spanish at install time stays Spanish regardless of this.
    /// </summary>
    [StringLength(5)]
    [Display(Name = "Language")]
    public string Language { get; set; } = "en";

    // Kiosk prompts. Each user-facing prompt is split into "On Inbound" / "On Outbound"
    // checkboxes (so the operator only sees it when relevant) plus an "Allow Skip" flag
    // that controls whether the kiosk shows a "— None —" row + Skip button.

    [Display(Name = "Prompt Commodity on Inbound")]
    public bool PromptKioskCommodityOnInbound { get; set; } = true;
    [Display(Name = "Prompt Commodity on Outbound")]
    public bool PromptKioskCommodityOnOutbound { get; set; }
    [Display(Name = "Allow Skip Commodity")]
    public bool AllowSkipCommodity { get; set; } = true;

    [Display(Name = "Prompt Customer on Inbound")]
    public bool PromptKioskCustomerOnInbound { get; set; } = true;
    [Display(Name = "Prompt Customer on Outbound")]
    public bool PromptKioskCustomerOnOutbound { get; set; }
    [Display(Name = "Allow Skip Customer")]
    public bool AllowSkipCustomer { get; set; } = true;

    [Display(Name = "Prompt Carrier")]
    public bool PromptKioskCarrier { get; set; } = true;
    [Display(Name = "Allow Skip Carrier")]
    public bool AllowSkipCarrier { get; set; } = true;

    [Display(Name = "Prompt Location on Inbound")]
    public bool PromptKioskLocationOnInbound { get; set; } = true;
    [Display(Name = "Prompt Location on Outbound")]
    public bool PromptKioskLocationOnOutbound { get; set; }
    [Display(Name = "Allow Skip Location")]
    public bool AllowSkipLocation { get; set; } = true;

    [Display(Name = "Prompt Truck ID")]
    public bool PromptKioskTruckId { get; set; } = true;
    [Display(Name = "Allow Skip Truck ID")]
    public bool AllowSkipTruckId { get; set; } = true;

    [Display(Name = "Prompt Destination on Inbound")]
    public bool PromptKioskDestinationOnInbound { get; set; }
    [Display(Name = "Prompt Destination on Outbound")]
    public bool PromptKioskDestinationOnOutbound { get; set; } = true;
    [Display(Name = "Allow Skip Destination")]
    public bool AllowSkipDestination { get; set; } = true;

    [Display(Name = "Prompt Bin on Inbound")]
    public bool PromptKioskBinOnInbound { get; set; } = true;
    [Display(Name = "Prompt Bin on Outbound")]
    public bool PromptKioskBinOnOutbound { get; set; }
    [Display(Name = "Allow Skip Bin")]
    public bool AllowSkipBin { get; set; } = true;

    /// <summary>
    /// Hide the on-screen interactive buttons on the kiosk (numpad, Cancel,
    /// Skip, Select, Done) so the operator drives entirely with a physical
    /// keyboard / barcode scanner. The two ready-state buttons (New Load /
    /// Enter Ticket) and the post-print Reprint button stay visible.
    /// </summary>
    [Display(Name = "Hide On-Screen Buttons")]
    public bool HideKioskOnScreenButtons { get; set; }

    /// <summary>
    /// IANA timezone ID used to format dates everywhere user-facing — date-range
    /// filters, completed-tickets grid, ticket prints. Stored separately from
    /// host OS clock so a UTC cloud server still shows local time. Examples:
    /// America/Chicago, America/New_York, America/Denver, America/Los_Angeles.
    /// </summary>
    [Display(Name = "Display Time Zone")]
    [StringLength(100)]
    public string? TimeZoneId { get; set; } = "America/Chicago";

    [Display(Name = "Kiosk Dark Mode")]
    public bool KioskDarkMode { get; set; } = true;

    // Login / Security
    [Display(Name = "Require Login")]
    public bool UseLogin { get; set; }

    [StringLength(20)]
    [Display(Name = "Kiosk PIN Code")]
    public string KioskCode { get; set; } = "12345";

    [StringLength(20)]
    [Display(Name = "API Definition PIN")]
    public string ApiDefinitionPin { get; set; } = "12345";

    // QuickBooks integration
    [Display(Name = "Connect to QuickBooks")]
    public bool UseQuickBooks { get; set; }

    /// <summary>
    /// Bin inventory tracking. When true: a Bin field appears on the weigh
    /// forms and kiosk prompts, bins are managed on Edit Tables, and the
    /// Reports page gains a Bin Inventory tab. On-hand per bin is computed
    /// from ticket history — loads where the truck arrived heavy add to the
    /// bin, loads where it left heavy deduct — plus manual adjustments
    /// (true-ups for shrinkage, starting balances). When false, nothing
    /// bin-related is shown anywhere.
    /// </summary>
    [Display(Name = "Use Bin Inventory")]
    public bool UseBinInventory { get; set; }

    /// <summary>
    /// When Bin Inventory is on, require a bin on every ticket: the weigh
    /// forms won't save without one and the kiosk bin prompt can't be
    /// skipped (Allow Skip Bin is forced off). Keeps the inventory complete —
    /// no unbinned loads slip through.
    /// </summary>
    [Display(Name = "Bin Entry")]
    public bool BinRequired { get; set; }

    /// <summary>
    /// When Bin Inventory is on, tie each bin to the commodity it currently
    /// holds (computed balance &gt; 0): selecting that bin auto-sets the
    /// commodity on tickets and a mismatched commodity is rejected, so corn
    /// can't be booked into a wheat bin. The tie releases on its own when the
    /// bin is emptied or trued up to zero.
    /// </summary>
    [Display(Name = "Lock Bin to Commodity")]
    public bool BinCommodityLock { get; set; }

    /// <summary>
    /// Fallback reporting unit for report quantity columns. A commodity with
    /// its own Reporting Unit + Lbs per Unit (Edit Tables → Commodities)
    /// always uses those; commodities without one show this unit instead —
    /// "lbs" (plain pounds) or "kg" (converted from the weighed pounds).
    /// </summary>
    [StringLength(10)]
    [Display(Name = "Default Reporting Unit")]
    public string DefaultReportUnit { get; set; } = "lbs";

    // Camera / ticket images
    [Display(Name = "Save Picture for Ticket")]
    public bool SavePicture { get; set; }

    // Camera assignments (format: "serviceId:cameraId")
    [StringLength(100)]
    [Display(Name = "Inbound Camera")]
    public string? InboundCameraId { get; set; }

    [StringLength(100)]
    [Display(Name = "Outbound Camera")]
    public string? OutboundCameraId { get; set; }

    // Scale assignment (format: "serviceId:scaleId")
    [StringLength(100)]
    [Display(Name = "Scale")]
    public string? ScaleId { get; set; }

    // Recall last ticket values
    [Display(Name = "Recall Last Values")]
    public bool RecallLastValues { get; set; }

    /// <summary>
    /// When true, a weigh-out persists the truck's empty weight to Truck.RetainedTare,
    /// and the next kiosk weigh-in for the same truck auto-completes the ticket using
    /// that stored tare. When false, every truck does the normal two-pass in/out cycle.
    /// </summary>
    [Display(Name = "Use Retained Tare")]
    public bool UseRetainedTare { get; set; }

    /// <summary>
    /// When true, retained tares older than today are automatically cleared on
    /// the next read (kiosk weigh-in lookup, RetainedTare admin page, MasterData
    /// truck grid). When false, stored tares persist until manually cleared.
    /// </summary>
    [Display(Name = "Auto-Clear Tares at Midnight")]
    public bool AutoClearStaleRetainedTare { get; set; } = true;

    /// <summary>
    /// HID / prox card weighing. When true: the Cards pages appear, a kiosk
    /// mapped to a card reader accepts card presentations, and the loader
    /// operator issues cards from a phone (Cards → Card Setup). When false,
    /// nothing card-related is shown and readers are ignored.
    /// </summary>
    [Display(Name = "Use Card Reader")]
    public bool UseCardReader { get; set; }

    /// <summary>
    /// Site-wide default for what happens to a card when its transaction
    /// closes. True: the card stays issued with its stored values so the same
    /// driver can run another load without seeing the loader operator. False:
    /// the card is deactivated and must be re-issued. Individual cards can
    /// override this (Card.RecycleMode).
    /// </summary>
    [Display(Name = "Recycle Cards")]
    public bool RecycleCards { get; set; }

    // Remote printing mode: None, Scale, RemotePrinter
    [StringLength(20)]
    [Display(Name = "Remote Printing")]
    public string RemotePrintMode { get; set; } = "None";

    // Printer assignments (format: "serviceId:printerId")
    [StringLength(100)]
    [Display(Name = "Inbound Printer")]
    public string? InboundPrinterId { get; set; }

    [StringLength(100)]
    [Display(Name = "Outbound Printer")]
    public string? OutboundPrinterId { get; set; }

    [StringLength(100)]
    [Display(Name = "Kiosk Printer")]
    public string? KioskPrinterId { get; set; }

    // Driver signature capture on weigh-out.
    // None            — feature off.
    // Operator        — the operator's own device shows a full-screen capture
    //                   overlay on the Weigh Out page (tablet handed to driver).
    // RemotePad       — a dedicated tablet runs /SignaturePad in standby mode and
    //                   is woken over SignalR when the operator requests a signature.
    [StringLength(20)]
    [Display(Name = "Signature Capture")]
    public string SignatureMode { get; set; } = "None";

    /// <summary>
    /// Which remote pad receives signature requests (RemotePad mode). Matches the
    /// pad-id the tablet passed when it opened /SignaturePad?pad-id=...
    /// </summary>
    [StringLength(100)]
    [Display(Name = "Signature Pad ID")]
    public string? SignaturePadId { get; set; }

    /// <summary>
    /// When true, Save on Weigh Out is blocked until a signature is captured.
    /// When false the operator gets a confirm prompt but can save without one.
    /// </summary>
    [Display(Name = "Signature Required")]
    public bool SignatureRequired { get; set; }

    [Display(Name = "Print Signature on Ticket")]
    public bool PrintSignatureOnTicket { get; set; } = true;

    // Standard-field visibility (Setup → Fields). A hidden field disappears from
    // the weigh forms, grids, the reports page, printed tickets, and its kiosk
    // prompts are forced off. Values already stored stay in the database.
    // Rules enforced on save: hiding Carrier hides Truck ID (trucks belong to
    // carriers); Retained Tare keeps Carrier + Truck ID visible.
    [Display(Name = "Hide Customer")]
    public bool HideCustomer { get; set; }

    [Display(Name = "Hide Carrier")]
    public bool HideCarrier { get; set; }

    [Display(Name = "Hide Truck ID")]
    public bool HideTruckId { get; set; }

    [Display(Name = "Hide Commodity")]
    public bool HideCommodity { get; set; }

    [Display(Name = "Hide Location")]
    public bool HideLocation { get; set; }

    [Display(Name = "Hide Destination")]
    public bool HideDestination { get; set; }

    [Display(Name = "Hide Notes")]
    public bool HideNotes { get; set; }

    // Standard-field sort order (Setup → Fields). Shared ordering scale with
    // CustomField.SortOrder so admin-defined fields can interleave with the
    // built-ins. Controls the weigh forms (Weigh In / Weigh Out / Edit / Basic
    // Ticket) and the order of auto-appended custom rows on printed tickets;
    // the printed position of standard fields stays with the Ticket Designer.
    // Defaults mirror the historical form layout (Commodity first … Notes last,
    // custom fields after, which the migration rebases to 100+).
    [Display(Name = "Commodity Order")]
    public int FieldOrderCommodity { get; set; } = 10;

    [Display(Name = "Customer Order")]
    public int FieldOrderCustomer { get; set; } = 20;

    [Display(Name = "Carrier Order")]
    public int FieldOrderCarrier { get; set; } = 30;

    [Display(Name = "Truck ID Order")]
    public int FieldOrderTruckId { get; set; } = 40;

    [Display(Name = "Location Order")]
    public int FieldOrderLocation { get; set; } = 50;

    [Display(Name = "Destination Order")]
    public int FieldOrderDestination { get; set; } = 60;

    [Display(Name = "Bin Order")]
    public int FieldOrderBin { get; set; } = 65;

    [Display(Name = "Notes Order")]
    public int FieldOrderNotes { get; set; } = 70;
}
