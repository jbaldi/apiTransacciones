using ApiTransacciones.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApiTransacciones.Persistence;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<EventLogEntry> Events => Set<EventLogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Idempotencia: una key no puede repetirse. Corazón de "misma key → mismo resultado".
        b.Entity<Payment>().HasIndex(p => p.IdempotencyKey).IsUnique();
        b.Entity<Payment>().Property(p => p.Amount).HasConversion<double>(); // SQLite no tiene decimal nativo
        b.Entity<OutboxMessage>().HasIndex(o => o.Status);
        b.Entity<EventLogEntry>().Property(e => e.Id).ValueGeneratedOnAdd();

        // SQLite no ordena/compara DateTimeOffset nativo: lo guardamos como long binario (ordenable).
        var dtoConverter = new DateTimeOffsetToBinaryConverter();
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
                if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(dtoConverter);
    }
}
