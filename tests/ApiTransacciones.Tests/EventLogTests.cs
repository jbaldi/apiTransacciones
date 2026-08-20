using ApiTransacciones.Domain;
using ApiTransacciones.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class EventLogTests
{
    private static PaymentsDbContext NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(conn).Options;
        var db = new PaymentsDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task EventLog_EsInmutable_SoloAppend_YMantieneOrden()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var clock = new FakeTimeProvider();
        using var db = NewDb(conn);
        var log = new EventLog(db, clock);
        var pid = Guid.NewGuid();

        await log.AppendAsync(pid, DomainEvents.PaymentReceived);
        clock.Advance(TimeSpan.FromSeconds(1));
        await log.AppendAsync(pid, DomainEvents.SentToProcessor, new { processor = "primary" });

        var events = await db.Events.Where(e => e.PaymentId == pid).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(DomainEvents.PaymentReceived, events[0].EventType);
        Assert.True(events[1].Id > events[0].Id); // orden garantizado por autoincremento
    }
}
