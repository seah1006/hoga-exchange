using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Threading.Channels;

namespace Hoga.Server.Operations;

internal interface IRoundBackupTrigger
{
    void AfterReset(DateTimeOffset completedAt);
}

internal interface ISqliteSnapshot
{
    // Return false when this provider will not make a snapshot; write only to the staging file.
    ValueTask<bool> Copy(string database, string staging, CancellationToken stop);
}

internal sealed class OnlineSqliteSnapshot : ISqliteSnapshot
{
    public ValueTask<bool> Copy(string database, string staging, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        // Restrict permissions before opening a file that will contain member data.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (new FileStream(staging, options)) { }
        using var source = Open(database, SqliteOpenMode.ReadOnly);
        using var target = Open(staging, SqliteOpenMode.ReadWrite);
        source.BackupDatabase(target);
        // The backup must open read-only without needing writable WAL sidecars.
        using var journalMode = target.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals(journalMode.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite backup could not be made standalone.");
        stop.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 1 }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}


internal sealed class BackupService(IConfiguration config, IWebHostEnvironment environment, TimeProvider clock,
    IServiceProvider services, ISqliteSnapshot snapshot, ILogger<BackupService> logger) : BackgroundService, IRoundBackupTrigger
{
    private const string Stamp = "yyyyMMddHHmmssfffffff";
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7); // 임의 지정
    private readonly SemaphoreSlim _singleRun = new(1);
    private readonly Channel<DateTimeOffset> _resets = Channel.CreateBounded<DateTimeOffset>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void AfterReset(DateTimeOffset completedAt) => _resets.Writer.TryWrite(completedAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock); // 임의 지정
        await RunOnce(clock.GetUtcNow(), stoppingToken);
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var reset = _resets.Reader.WaitToReadAsync(stoppingToken).AsTask();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAny(tick, reset);
            if (reset.IsCompleted && await reset)
            {
                if (_resets.Reader.TryRead(out var at)) await RunOnce(at, stoppingToken);
                reset = _resets.Reader.WaitToReadAsync(stoppingToken).AsTask();
            }
            if (tick.IsCompleted && await tick)
            {
                await RunOnce(clock.GetUtcNow(), stoppingToken);
                tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            }
        }
    }

    internal async Task RunOnce(DateTimeOffset at, CancellationToken stop = default)
    {
        await _singleRun.WaitAsync(stop);
        string? staging = null;
        try
        {
            // Housekeeping is not part of the market mutation or its database transaction.
            using (var scope = services.CreateScope())
            {
                try { await Members.PruneAudits(scope.ServiceProvider.GetRequiredService<MarketDb>(), at, stop); }
                catch (SqliteException) { logger.LogWarning("Could not prune login audit rows"); }
            }
            var directory = Path.GetFullPath(config["Operations:BackupDirectory"] ?? "data/backups", environment.ContentRootPath);
            PrivateDirectory(directory);
            foreach (var old in Directory.EnumerateFiles(directory, "hoga-*.zip"))
                if (DateTime.TryParseExact(Path.GetFileName(old), $"'hoga-'{Stamp}'Z.zip'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) && utc <= at.UtcDateTime - Retention)
                    File.Delete(old);

            staging = Path.Combine(directory, $".backup-{Guid.NewGuid():N}.tmp");
            PrivateDirectory(staging);
            var db = Path.Combine(staging, "hoga.db");
            var market = services.GetRequiredService<MarketLoop>();
            await using var checkpoint = await market.CaptureBackup(snapshot, db, stop);
            var archive = Path.Combine(staging, "bundle.zip");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(archive, options))
            using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                await using (var database = File.OpenRead(db))
                await using (var entry = zip.CreateEntry("hoga.db").Open()) await database.CopyToAsync(entry, stop);
                foreach (var (name, input, length) in checkpoint.Files)
                {
                    await using var entry = zip.CreateEntry(name).Open();
                    await CopyPrefix(input, entry, length, stop);
                }
            }
            var destination = Path.Combine(directory, "hoga-" + at.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture) + "Z.zip");
            File.Move(archive, destination, overwrite: true);
            logger.LogInformation("Recovery bundle completed at {BackupTime}", at.UtcDateTime);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        { logger.LogWarning("Recovery bundle failed ({FailureType})", ex.GetType().Name); }
        finally
        {
            if (staging is not null)
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning("Could not remove recovery bundle staging directory"); }
            _singleRun.Release();
        }
    }

    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task CopyPrefix(Stream source, Stream output, long length, CancellationToken stop)
    {
        var bytes = new byte[65536];
        while (length > 0)
        {
            var read = await source.ReadAsync(bytes.AsMemory(0, (int)Math.Min(length, bytes.Length)), stop);
            if (read == 0) throw new EndOfStreamException("Backup input shortened after checkpoint.");
            await output.WriteAsync(bytes.AsMemory(0, read), stop);
            length -= read;
        }
    }
}

internal sealed class BackupCheckpoint : IAsyncDisposable
{
    internal List<(string Name, FileStream Input, long Length)> Files { get; } = [];
    internal void Add(string entry, string path)
    {
        var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try { Files.Add((entry, input, input.Length)); }
        catch { input.Dispose(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var (_, input, _) in Files) await input.DisposeAsync();
    }
}
