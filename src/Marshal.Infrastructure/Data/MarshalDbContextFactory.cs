using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Dla narzędzia <c>dotnet ef</c>. Aplikacja w czasie działania buduje kontekst
/// przez wstrzykiwanie zależności, nie przez tę fabrykę.
/// </summary>
public sealed class MarshalDbContextFactory : IDesignTimeDbContextFactory<MarshalDbContext>
{
    public MarshalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarshalDbContext>()
            .UseSqlite("Data Source=marshal-design.db")
            .Options;

        return new MarshalDbContext(options);
    }
}
