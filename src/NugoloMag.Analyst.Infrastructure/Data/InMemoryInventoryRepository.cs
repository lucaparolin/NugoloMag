using NugoloMag.Analyst.Application.Abstractions;
using NugoloMag.Analyst.Domain;

namespace NugoloMag.Analyst.Infrastructure.Data;

public sealed class InMemoryInventoryRepository(IReadOnlyList<StockDay> rows) : IInventoryRepository
{
    public Task<IReadOnlyList<StockDay>> LoadAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StockDay>>(rows.Where(r => r.Date >= from && r.Date <= to).ToList());
}
