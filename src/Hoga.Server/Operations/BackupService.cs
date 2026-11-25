using System.Globalization;
using System.Threading.Channels;

namespace Hoga.Server.Operations;

internal interface IRoundBackupTrigger
{
    void AfterDailyReset(DateTimeOffset completedAt);
}

internal interface ISqliteSnapshot
{
    // Return false when this provider will not make a snapshot; write only to the staging file.
    ValueTask<bool> Copy(string database, string staging, CancellationToken stop);
}

// TASK-011: use SqliteConnection.BackupDatabase with separately opened source/destination connections.
// Never use byte copying on a running SQLite database (including WAL databases).
internal sealed class OfflineFileSnapshot(IConfiguration configuration) : ISqliteSnapshot
{
    public async ValueTask<bool> Copy(string database, string staging, CancellationToken stop)
    {
        if (!configuration.GetValue<bool>("Operations:AllowOfflineDatabaseCopy") || HasSidecar(database)) return false;
        await using var source = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var target = new FileStream(staging, options);
        await source.CopyToAsync(target, stop);
        await target.FlushAsync(stop);
        target.Flush(flushToDisk: true);
        return !HasSidecar(database);
    }

    private static bool HasSidecar(string database) => File.Exists(database + "-wal") ||
        File.Exists(database + "-shm") || File.Exists(database + "-journal");
}

internal sealed class BackupService(IConfiguration config, IWebHostEnvironment environment, TimeProvider clock,
    ISqliteSnapshot snapshot, ILogger<BackupService> logger) : BackgroundService, IRoundBackupTrigger
{
    private const int Retention = 7; // 임의 지정: retain seven dated backups.
    private static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
    private readonly Channel<DateTimeOffset> _daily = Channel.CreateUnbounded<DateTimeOffset>(new() { SingleReader = true });

    public void AfterDailyReset(DateTimeOffset completedAt) => _daily.Writer.TryWrite(completedAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnce(clock.GetUtcNow(), stoppingToken);
        await foreach (var at in _daily.Reader.ReadAllAsync(stoppingToken)) await RunOnce(at, stoppingToken);
    }

    internal async Task RunOnce(DateTimeOffset at, CancellationToken stop = default)
    {
        string? staging = null;
        try
        {
            var database = Path.GetFullPath(config["Market:DatabasePath"] ?? "data/hoga.db", environment.ContentRootPath);
            if (!File.Exists(database)) { logger.LogInformation("SQLite backup skipped: database missing"); return; }
            var directory = Path.GetFullPath(config["Operations:BackupDirectory"] ?? "data/backups", environment.ContentRootPath);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var date = TimeZoneInfo.ConvertTime(at, Seoul).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var destination = Path.Combine(directory, $"hoga-{date}.db");
            if (string.Equals(database, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                logger.LogWarning("SQLite backup skipped: source and destination overlap");
                return;
            }
            staging = Path.Combine(directory, $".backup-{Guid.NewGuid():N}.tmp");
            if (!await snapshot.Copy(database, staging, stop))
            {
                logger.LogWarning("SQLite backup skipped: no safe snapshot provider for this database");
                return;
            }
            File.Move(staging, destination, overwrite: true);
            staging = null;
            foreach (var old in Directory.EnumerateFiles(directory, "hoga-????????.db")
                .Where(path => DateOnly.TryParseExact(Path.GetFileName(path), "'hoga-'yyyyMMdd'.db'", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)).OrderDescending(StringComparer.Ordinal).Skip(Retention))
                File.Delete(old);
            logger.LogInformation("SQLite backup completed for {BackupDate}", date);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        { logger.LogWarning("SQLite backup failed ({FailureType})", ex.GetType().Name); }
        finally
        {
            if (staging is not null)
                try { File.Delete(staging); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Could not remove SQLite backup staging file"); }
        }
    }
}
