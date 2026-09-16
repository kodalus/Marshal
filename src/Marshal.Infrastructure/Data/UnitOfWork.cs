using Marshal.Application.Repositories;

namespace Marshal.Infrastructure.Data;

public sealed class UnitOfWork(MarshalDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
