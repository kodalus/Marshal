using Marshal.Domain.Contacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marshal.Infrastructure.Data.Configurations;

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("Contacts");
        builder.HasKey(k => k.Id);

        builder.Property(k => k.Name).IsRequired().HasMaxLength(100);
        builder.Property(k => k.Email).IsRequired().HasMaxLength(200);
        builder.Property(k => k.CreatedAt).IsRequired();
        builder.Property(k => k.UpdatedAt).IsRequired();
        builder.Property(k => k.Deleted).IsRequired();

        // Nagrobki zostają w tabeli — synchronizacja musi je widzieć.
        builder.HasIndex(k => k.Deleted);
    }
}
