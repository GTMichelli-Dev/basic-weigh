using Foundation.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Foundation.Web.Data;

public class ScaleDbContext : DbContext
{
    public ScaleDbContext(DbContextOptions<ScaleDbContext> options) : base(options) { }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Carrier> Carriers => Set<Carrier>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Destination> Destinations => Set<Destination>();
    public DbSet<Bin> Bins => Set<Bin>();
    public DbSet<BinAdjustment> BinAdjustments => Set<BinAdjustment>();
    public DbSet<Truck> Trucks => Set<Truck>();
    public DbSet<Commodity> Commodities => Set<Commodity>();
    public DbSet<AppSetup> AppSetup => Set<AppSetup>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<CameraConfig> CameraConfigs => Set<CameraConfig>();
    public DbSet<CustomField> CustomFields => Set<CustomField>();
    public DbSet<CustomFieldListValue> CustomFieldListValues => Set<CustomFieldListValue>();
    public DbSet<TransactionCustomValue> TransactionCustomValues => Set<TransactionCustomValue>();
    public DbSet<Scale> Scales => Set<Scale>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<ReportSchedule> ReportSchedules => Set<ReportSchedule>();
    public DbSet<LoadEmailRule> LoadEmailRules => Set<LoadEmailRule>();
    public DbSet<LoadEmailLog> LoadEmailLogs => Set<LoadEmailLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(e =>
        {
            e.ToTable("Transactions");
            e.HasIndex(t => t.DateIn);
            e.HasIndex(t => t.Customer);
            e.HasIndex(t => t.Carrier);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.HasIndex(c => c.CustomerName).IsUnique();
            e.Property(c => c.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<Carrier>(e =>
        {
            e.ToTable("Carriers");
            e.HasIndex(c => c.CarrierName).IsUnique();
            e.Property(c => c.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<Location>(e =>
        {
            e.ToTable("Locations");
            e.HasIndex(l => l.LocationName).IsUnique();
            e.Property(l => l.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<Destination>(e =>
        {
            e.ToTable("Destinations");
            e.HasIndex(d => d.DestinationName).IsUnique();
            e.Property(d => d.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<Bin>(e =>
        {
            e.ToTable("Bins");
            e.HasIndex(b => b.BinName).IsUnique();
            e.Property(b => b.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<BinAdjustment>(e =>
        {
            e.ToTable("BinAdjustments");
            e.HasIndex(a => a.Bin);
        });
        modelBuilder.Entity<Truck>(e =>
        {
            e.ToTable("Trucks");
            e.HasIndex(t => new { t.TruckId, t.CarrierName }).IsUnique();
            e.Property(t => t.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<Commodity>(e =>
        {
            e.ToTable("Commodities");
            e.HasIndex(c => c.CommodityName).IsUnique();
            e.Property(c => c.UseAtKiosk).HasDefaultValue(true);
        });
        modelBuilder.Entity<AppSetup>(e =>
        {
            e.ToTable("AppSetup");
            e.Property(s => s.PromptKioskCommodityOnInbound).HasDefaultValue(true);
            e.Property(s => s.PromptKioskCustomerOnInbound).HasDefaultValue(true);
            e.Property(s => s.PromptKioskCarrier).HasDefaultValue(true);
            e.Property(s => s.PromptKioskLocationOnInbound).HasDefaultValue(true);
            e.Property(s => s.PromptKioskTruckId).HasDefaultValue(true);
            e.Property(s => s.PromptKioskDestinationOnOutbound).HasDefaultValue(true);
            e.Property(s => s.AllowSkipCommodity).HasDefaultValue(true);
            e.Property(s => s.AllowSkipCustomer).HasDefaultValue(true);
            e.Property(s => s.AllowSkipCarrier).HasDefaultValue(true);
            e.Property(s => s.AllowSkipLocation).HasDefaultValue(true);
            e.Property(s => s.AllowSkipTruckId).HasDefaultValue(true);
            e.Property(s => s.AllowSkipDestination).HasDefaultValue(true);
            e.Property(s => s.PromptKioskBinOnInbound).HasDefaultValue(true);
            e.Property(s => s.AllowSkipBin).HasDefaultValue(true);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<CameraConfig>(e =>
        {
            e.ToTable("CameraConfigs");
            e.HasIndex(c => c.CameraId).IsUnique();
        });

        modelBuilder.Entity<CustomField>(e =>
        {
            e.ToTable("CustomFields");
            e.HasIndex(f => f.Name).IsUnique();
        });

        modelBuilder.Entity<CustomFieldListValue>(e =>
        {
            e.ToTable("CustomFieldListValues");
            e.HasIndex(v => new { v.CustomFieldId, v.ParentValue, v.Value }).IsUnique();
            e.HasOne<CustomField>()
                .WithMany()
                .HasForeignKey(v => v.CustomFieldId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Scale>(e =>
        {
            e.ToTable("Scales");
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<Site>(e =>
        {
            e.ToTable("Sites");
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<ReportTemplate>(e =>
        {
            e.ToTable("ReportTemplates");
            e.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<EmailSettings>(e => e.ToTable("EmailSettings"));

        modelBuilder.Entity<ReportSchedule>(e =>
        {
            e.ToTable("ReportSchedules");
            e.HasIndex(s => s.Name).IsUnique();
        });

        modelBuilder.Entity<LoadEmailRule>(e =>
        {
            e.ToTable("LoadEmailRules");
            e.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<LoadEmailLog>(e =>
        {
            e.ToTable("LoadEmailLogs");
            // The sweep's de-dup key: one attempt row per ticket per rule.
            e.HasIndex(l => new { l.Ticket, l.RuleId }).IsUnique();
            e.HasOne<LoadEmailRule>()
                .WithMany()
                .HasForeignKey(l => l.RuleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TransactionCustomValue>(e =>
        {
            e.ToTable("TransactionCustomValues");
            e.HasIndex(v => v.Ticket);
            e.HasIndex(v => new { v.Ticket, v.CustomFieldId }).IsUnique();
            e.HasOne<Transaction>()
                .WithMany()
                .HasForeignKey(v => v.Ticket)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CustomField>()
                .WithMany()
                .HasForeignKey(v => v.CustomFieldId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetup>().HasData(new AppSetup
        {
            Id = 1,
            TicketNumber = 1,
            Header1 = "Foundation",
            Header2 = "",
            Header3 = "",
            Header4 = "",
            TicketsPerPage = 1,
            DemoMode = false,
            KioskCount = 0,
            Icon = null,
            IconContentType = null,
            Theme = "default"
        });

        // Single settings row, same shape as AppSetup — the Email tab edits it
        // in place and never inserts.
        modelBuilder.Entity<EmailSettings>().HasData(new EmailSettings
        {
            Id = 1,
            Enabled = false,
            Host = "mail.smtp2go.com",
            Port = 2525,
            SecurityMode = "StartTls"
        });
    }
}
