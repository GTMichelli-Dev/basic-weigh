using Microsoft.EntityFrameworkCore;
using GateControllerService.Models;

namespace GateControllerService.Data;

public class GateDbContext : DbContext
{
    public GateDbContext(DbContextOptions<GateDbContext> options) : base(options) { }

    public DbSet<GateConfigEntity> Gates => Set<GateConfigEntity>();
    public DbSet<ServiceSettings> Settings => Set<ServiceSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GateConfigEntity>(e =>
        {
            e.ToTable("Gates");
            e.HasIndex(g => g.GateId).IsUnique();
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
