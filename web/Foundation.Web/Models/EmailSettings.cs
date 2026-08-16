using System.ComponentModel.DataAnnotations;

namespace Foundation.Web.Models;

/// <summary>
/// Single-row (Id = 1) SMTP configuration for scheduled reports and per-load
/// emails (Setup → Email). Defaults target SMTP2GO, but any relay works —
/// nothing here is provider-specific.
///
/// The password is stored encrypted (see EmailSender / IDataProtector), unlike
/// the plaintext KioskCode / ApiDefinitionPin columns: an SMTP credential can
/// be used to send mail as the customer from anywhere, so a leaked backup of
/// Foundation.db shouldn't hand it over.
/// </summary>
public class EmailSettings
{
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>Master switch. Off = nothing is sent and the schedule worker
    /// idles, even with active schedules and rules configured.</summary>
    [Display(Name = "Enable Email")]
    public bool Enabled { get; set; }

    [StringLength(200)]
    [Display(Name = "SMTP Server")]
    public string Host { get; set; } = "mail.smtp2go.com";

    [Display(Name = "Port")]
    public int Port { get; set; } = 2525;

    /// <summary>None | StartTls | SslOnConnect. SMTP2GO: StartTls on 2525/587,
    /// SslOnConnect on 465.</summary>
    [StringLength(20)]
    [Display(Name = "Security")]
    public string SecurityMode { get; set; } = "StartTls";

    [StringLength(200)]
    [Display(Name = "Username")]
    public string? Username { get; set; }

    /// <summary>Data-protected ciphertext, never the raw password. Written only
    /// by EmailController when the operator types a new one; the UI reads back
    /// a placeholder so the secret never leaves the server.</summary>
    [StringLength(2000)]
    public string? PasswordProtected { get; set; }

    [StringLength(200)]
    [Display(Name = "From Address")]
    public string? FromAddress { get; set; }

    [StringLength(100)]
    [Display(Name = "From Name")]
    public string? FromName { get; set; }

    [StringLength(200)]
    [Display(Name = "Reply-To")]
    public string? ReplyTo { get; set; }

    /// <summary>Result of the last send attempt of any kind — shown on the
    /// Setup → Email tab so a wrong password or a site that lost its uplink is
    /// visible without digging through logs.</summary>
    [StringLength(500)]
    public string? LastError { get; set; }

    public DateTime? LastSendUtc { get; set; }
}
