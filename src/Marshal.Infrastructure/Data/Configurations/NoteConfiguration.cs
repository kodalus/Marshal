using Marshal.Domain.Attachments;
using Marshal.Domain.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("Notes");
        builder.HasKey(n => n.Id);

        builder.Property(n => n.Title).IsRequired().HasMaxLength(500);
        builder.Property(n => n.Content).IsRequired();
        builder.Property(n => n.IsPinned).IsRequired();
        builder.Property(n => n.CreatedAt).IsRequired();
        builder.Property(n => n.UpdatedAt).IsRequired();
        builder.Property(n => n.Deleted).IsRequired();

        // Krok zerowy przeglądu pyta o przypięte przy każdym otwarciu.
        builder.HasIndex(n => n.IsPinned);
        builder.HasIndex(n => n.AreaId);
    }
}

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);

        // Sześćdziesiąt cztery znaki szesnastkowe — długość skrótu SHA-256.
        builder.Property(a => a.Sha256).IsRequired().HasMaxLength(64);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.Size).IsRequired();
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt).IsRequired();
        builder.Property(a => a.Deleted).IsRequired();

        builder.HasIndex(a => a.TaskId);
        builder.HasIndex(a => a.NoteId);

        // Po skrócie sprawdzamy, czy plik już gdzieś jest — dwa takie same zdjęcia
        // mają zajmować jedno miejsce.
        builder.HasIndex(a => a.Sha256);
    }
}
