using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// EF Core connection interceptor that sets SQLite PRAGMA busy_timeout on every new connection.
/// This tells SQLite to wait (up to the specified milliseconds) when the database is locked
/// instead of immediately throwing "database is locked" (SQLITE_BUSY / Error 5).
///
/// Note: busy_timeout is per-connection, not persistent like WAL mode.
/// Default Timeout in the connection string only sets ADO.NET command timeout, not SQLite's busy_timeout.
/// </summary>
public class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    private readonly int _busyTimeoutMs;

    public SqliteBusyTimeoutInterceptor(int busyTimeoutMs = 5000)
    {
        _busyTimeoutMs = busyTimeoutMs;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetBusyTimeout(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SetBusyTimeout(connection);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void SetBusyTimeout(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutMs};";
        command.ExecuteNonQuery();
    }
}
