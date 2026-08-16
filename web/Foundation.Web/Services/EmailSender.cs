using Foundation.Web.Data;
using Foundation.Web.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Foundation.Web.Services;

/// <summary>One outgoing message: recipients, subject, HTML body, optional
/// attachment.</summary>
public class EmailMessage
{
    public required List<string> To { get; init; }
    public List<string> Cc { get; init; } = new();
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public byte[]? AttachmentBytes { get; init; }
    public string? AttachmentName { get; init; }
    public string AttachmentContentType { get; init; } =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}

/// <summary>
/// SMTP delivery for scheduled reports and per-load emails. Configured from the
/// EmailSettings row (Setup → Email); defaults point at SMTP2GO but any relay
/// works.
///
/// Sites frequently run without a reliable uplink (see the deployment notes on
/// offline installs), so a send failure is a normal outcome here, not an
/// exception to bubble up: every send returns a result the callers record and
/// retry from.
/// </summary>
public class EmailSender
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protection,
        ILogger<EmailSender> logger)
    {
        _scopeFactory = scopeFactory;
        _protector = protection.CreateProtector("Foundation.EmailSettings.Password");
        _logger = logger;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <summary>Decrypts a stored password. Returns null when the key ring has
    /// rotated or the value predates protection — the operator re-enters it
    /// rather than the app silently authenticating with garbage.</summary>
    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stored SMTP password could not be decrypted; re-enter it on Setup → Email.");
            return null;
        }
    }

    /// <summary>Current settings, or null when email is switched off or not yet
    /// usable (no server / no from address).</summary>
    public EmailSettings? GetUsableSettings(ScaleDbContext db)
    {
        var s = db.EmailSettings.AsNoTracking().FirstOrDefault();
        if (s == null || !s.Enabled) return null;
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.FromAddress)) return null;
        return s;
    }

    /// <summary>Sends one message. Never throws: the bool says whether it left
    /// the building, and Error carries what to show the operator.</summary>
    public async Task<(bool Sent, string? Error)> SendAsync(
        EmailSettings settings, EmailMessage message, CancellationToken ct = default)
    {
        var to = message.To.Where(EmailRecipients.IsValidAddress).ToList();
        if (to.Count == 0) return (false, "No valid recipient addresses.");

        try
        {
            var mime = BuildMime(settings, message, to);

            using var client = new SmtpClient();
            // 30s is generous for a relay handshake and short enough that a
            // dead uplink doesn't wedge the schedule worker for a whole tick.
            client.Timeout = 30_000;

            var security = settings.SecurityMode switch
            {
                "None" => SecureSocketOptions.None,
                "SslOnConnect" => SecureSocketOptions.SslOnConnect,
                _ => SecureSocketOptions.StartTls
            };

            await client.ConnectAsync(settings.Host, settings.Port, security, ct);

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                var password = Unprotect(settings.PasswordProtected);
                if (password == null)
                    return (false, "SMTP password is missing or unreadable — re-enter it on Setup → Email.");
                await client.AuthenticateAsync(settings.Username, password, ct);
            }

            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            // Truncated to fit EmailSettings.LastError / the per-row error columns.
            var error = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            _logger.LogWarning(ex, "Email send failed to {Recipients}", string.Join(", ", to));
            return (false, error);
        }
    }

    /// <summary>Sends and records the outcome on the EmailSettings row so the
    /// Setup tab can show the last failure without a log dive.</summary>
    public async Task<(bool Sent, string? Error)> SendAndRecordAsync(
        EmailSettings settings, EmailMessage message, CancellationToken ct = default)
    {
        var result = await SendAsync(settings, message, ct);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
        var row = db.EmailSettings.FirstOrDefault();
        if (row != null)
        {
            row.LastSendUtc = DateTime.UtcNow;
            row.LastError = result.Error;
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private static MimeMessage BuildMime(EmailSettings settings, EmailMessage message, List<string> to)
    {
        var mime = new MimeMessage();
        // GetUsableSettings / the settings endpoint both require a From address,
        // so this is non-null by the time a send is attempted.
        mime.From.Add(new MailboxAddress(settings.FromName ?? "", settings.FromAddress!));
        foreach (var address in to) mime.To.Add(MailboxAddress.Parse(address));
        foreach (var address in message.Cc.Where(EmailRecipients.IsValidAddress))
            mime.Cc.Add(MailboxAddress.Parse(address));
        if (!string.IsNullOrWhiteSpace(settings.ReplyTo) && EmailRecipients.IsValidAddress(settings.ReplyTo))
            mime.ReplyTo.Add(MailboxAddress.Parse(settings.ReplyTo));

        mime.Subject = message.Subject;

        var body = new BodyBuilder { HtmlBody = message.HtmlBody };
        if (message.AttachmentBytes is { Length: > 0 } && message.AttachmentName != null)
        {
            body.Attachments.Add(message.AttachmentName, message.AttachmentBytes,
                ContentType.Parse(message.AttachmentContentType));
        }
        mime.Body = body.ToMessageBody();

        return mime;
    }
}
