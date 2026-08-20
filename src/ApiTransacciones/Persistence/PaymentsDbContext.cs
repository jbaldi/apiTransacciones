using ApiTransacciones.Domain;
using Microsoft.EntityFrameworkCore;

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
    }
}
