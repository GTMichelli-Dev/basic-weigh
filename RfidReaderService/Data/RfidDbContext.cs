using Microsoft.EntityFrameworkCore;
using RfidReaderService.Models;

namespace RfidReaderService.Data;

public class RfidDbContext : DbContext
{
    public RfidDbContext(DbContextOptions<RfidDbContext> options) : base(options) { }

    public DbSet<ReaderConfigEntity> Readers => Set<ReaderConfigEntity>();
    public DbSet<ServiceSettings> Settings => Set<ServiceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReaderConfigEntity>(e =>
        {
            e.ToTable("Readers");
            e.HasIndex(r => r.ReaderId).IsUnique();
        });

        modelBuilder.Entity<ServiceSettings>(e =>
        {
            e.ToTable("Settings");
            e.HasData(new ServiceSettings
            {
                Id = 1,
                ServiceId = "default",
                ServerUrl = "http://localhost:5110",
                SignalRHub = "/scaleHub"
            });
        });
    }
}
