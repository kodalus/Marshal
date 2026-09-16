using Marshal.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class SyncCursorConfiguration : IEntityTypeConfiguration<SyncCursor>
{
    public void Configure(EntityTypeBuilder<SyncCursor> builder)
    {
        builder.ToTable("SyncCursors");
        builder.HasKey(c => c.RemoteDeviceId);

        builder.Property(c => c.RemoteDeviceId).HasMaxLength(64);
        builder.Property(c => c.Offset).IsRequired();
    }
}

public sealed class LocalSettingConfiguration : IEntityTypeConfiguration<LocalSetting>
{
    public void Configure(EntityTypeBuilder<LocalSetting> builder)
    {
        builder.ToTable("LocalSettings");
        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key).HasMaxLength(64);
        builder.Property(s => s.Value).IsRequired();
    }
}
