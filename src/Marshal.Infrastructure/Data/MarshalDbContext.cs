using System.Reflection;
using Marshal.Domain.Areas;
using Marshal.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Marshal.Infrastructure.Data;

public sealed class MarshalDbContext : DbContext
{
    public MarshalDbContext(DbContextOptions<MarshalDbContext> options)
        : base(options)
    {
    }

    public DbSet<Area> Areas => Set<Area>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Hlc>()
            .HaveConversion<HlcConverter>()
            .HaveMaxLength(64);

        // GUID jako tekst, nie blob: baza daje się czytać narzędziami, a postać
        // jest ta sama co w logu synchronizacji.
        configurationBuilder.Properties<Guid>()
            .HaveConversion<GuidToStringConverter>()
            .HaveMaxLength(36);

        base.ConfigureConventions(configurationBuilder);
    }
}
