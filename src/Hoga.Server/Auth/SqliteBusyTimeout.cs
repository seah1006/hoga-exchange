using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hoga.Server;

internal sealed class SqliteBusyTimeout : DbConnectionInterceptor
{
    internal static readonly SqliteBusyTimeout Instance = new();
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => SetTimeout(connection);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetTimeout(connection);
        return Task.CompletedTask;
    }
    private static void SetTimeout(DbConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=100"; // 임의 지정: native busy handler; provider retry is capped at one second.
        pragma.ExecuteNonQuery();
    }
}
