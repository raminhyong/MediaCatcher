using System.Globalization;
using MediaCatcher.Core.Logging;
using MediaCatcher.Core.Models;
using Microsoft.Data.Sqlite;

namespace MediaCatcher.Core.Storage;

public sealed class DownloadStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS downloads (
            id TEXT PRIMARY KEY NOT NULL,
            url TEXT NOT NULL,
            final_url TEXT,
            file_name TEXT NOT NULL,
            dir TEXT NOT NULL,
            category TEXT NOT NULL,
            kind TEXT NOT NULL,
            state TEXT NOT NULL,
            total_bytes INTEGER NOT NULL DEFAULT 0,
            downloaded_bytes INTEGER NOT NULL DEFAULT 0,
            connections INTEGER NOT NULL DEFAULT 8,
            etag TEXT,
            last_modified TEXT,
            referer TEXT,
            user_agent TEXT,
            engine TEXT,
            ytdlp_format TEXT,
            error TEXT,
            source TEXT,
            priority INTEGER NOT NULL DEFAULT 0,
            takeover INTEGER NOT NULL DEFAULT 0,
            cookies_enc BLOB,
            created_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_downloads_state ON downloads(state);
        CREATE INDEX IF NOT EXISTS ix_downloads_url ON downloads(url);
        """;

    private readonly string _connectionString;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DownloadStore(string? databasePath = null, IAppLogger? logger = null)
    {
        DatabasePath = databasePath ?? new AppPaths().DatabaseFile;
        _logger = logger ?? NullAppLogger.Instance;
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql + "\nPRAGMA user_version = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"Download store ready at {DatabasePath}");
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async Task UpsertAsync(
        DownloadItem item,
        byte[]? protectedCookies = null,
        bool preserveExistingCookies = true,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO downloads (
                    id, url, final_url, file_name, dir, category, kind, state,
                    total_bytes, downloaded_bytes, connections, etag, last_modified,
                    referer, user_agent, engine, ytdlp_format, error, source, priority,
                    takeover, cookies_enc, created_at, started_at, completed_at
                ) VALUES (
                    @id, @url, @final_url, @file_name, @dir, @category, @kind, @state,
                    @total_bytes, @downloaded_bytes, @connections, @etag, @last_modified,
                    @referer, @user_agent, @engine, @ytdlp_format, @error, @source, @priority,
                    @takeover, @cookies_enc, @created_at, @started_at, @completed_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    url = excluded.url,
                    final_url = excluded.final_url,
                    file_name = excluded.file_name,
                    dir = excluded.dir,
                    category = excluded.category,
                    kind = excluded.kind,
                    state = excluded.state,
                    total_bytes = excluded.total_bytes,
                    downloaded_bytes = excluded.downloaded_bytes,
                    connections = excluded.connections,
                    etag = excluded.etag,
                    last_modified = excluded.last_modified,
                    referer = excluded.referer,
                    user_agent = excluded.user_agent,
                    engine = excluded.engine,
                    ytdlp_format = excluded.ytdlp_format,
                    error = excluded.error,
                    source = excluded.source,
                    priority = excluded.priority,
                    takeover = excluded.takeover,
                    cookies_enc = {(preserveExistingCookies ? "COALESCE(excluded.cookies_enc, downloads.cookies_enc)" : "excluded.cookies_enc")},
                    created_at = excluded.created_at,
                    started_at = excluded.started_at,
                    completed_at = excluded.completed_at;
                """;

            AddParameters(command, item, protectedCookies);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DownloadItem?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM downloads WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    public async Task<byte[]?> GetProtectedCookiesAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cookies_enc FROM downloads WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] bytes && bytes.Length > 0 ? bytes : null;
    }

    public async Task<List<DownloadItem>> GetRecentAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM downloads ORDER BY created_at DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@limit", limit);
        return await ReadItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DownloadItem>> GetResumableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM downloads
            WHERE state IN ('Queued', 'Probing', 'Downloading', 'Merging', 'Paused', 'WaitingForSchedule')
            ORDER BY priority DESC, created_at ASC;
            """;
        return await ReadItemsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadItem?> FindDuplicateAsync(
        string url,
        string? fileName,
        long totalBytes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM downloads
            WHERE url = @url
              AND state NOT IN ('Failed', 'Canceled', 'Completed')
              AND (@file_name IS NULL OR file_name = @file_name)
              AND (@total_bytes = 0 OR total_bytes = 0 OR total_bytes = @total_bytes)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@url", url);
        command.Parameters.AddWithValue("@file_name", (object?)fileName ?? DBNull.Value);
        command.Parameters.AddWithValue("@total_bytes", totalBytes);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    public async Task UpdateProgressAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE downloads
                SET downloaded_bytes = @downloaded_bytes,
                    total_bytes = @total_bytes,
                    state = @state,
                    etag = COALESCE(@etag, etag),
                    last_modified = COALESCE(@last_modified, last_modified)
                WHERE id = @id
                  AND state IN ('Probing', 'Downloading', 'Merging');
                """;
            command.Parameters.AddWithValue("@id", item.Id);
            command.Parameters.AddWithValue("@downloaded_bytes", item.DownloadedBytes);
            command.Parameters.AddWithValue("@total_bytes", item.TotalBytes);
            command.Parameters.AddWithValue("@state", item.State.ToString());
            command.Parameters.AddWithValue("@etag", (object?)item.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("@last_modified", (object?)item.LastModified ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ClearCookiesAsync(string id, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "UPDATE downloads SET cookies_enc = NULL WHERE id = @id;",
            cancellationToken,
            ("@id", id)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(
            "DELETE FROM downloads WHERE id = @id;",
            cancellationToken,
            ("@id", id)).ConfigureAwait(false);
    }

    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE state IN ('Completed', 'Failed', 'Canceled');";
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM downloads;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long count ? (int)count : 0;
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void AddParameters(SqliteCommand command, DownloadItem item, byte[]? protectedCookies)
    {
        command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@url", item.Url);
        command.Parameters.AddWithValue("@final_url", (object?)item.FinalUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_name", item.FileName);
        command.Parameters.AddWithValue("@dir", item.Directory);
        command.Parameters.AddWithValue("@category", item.Category);
        command.Parameters.AddWithValue("@kind", item.Kind.ToString());
        command.Parameters.AddWithValue("@state", item.State.ToString());
        command.Parameters.AddWithValue("@total_bytes", item.TotalBytes);
        command.Parameters.AddWithValue("@downloaded_bytes", item.DownloadedBytes);
        command.Parameters.AddWithValue("@connections", item.Connections);
        command.Parameters.AddWithValue("@etag", (object?)item.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("@last_modified", (object?)item.LastModified ?? DBNull.Value);
        command.Parameters.AddWithValue("@referer", (object?)item.Referer ?? DBNull.Value);
        command.Parameters.AddWithValue("@user_agent", (object?)item.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("@engine", (object?)item.Engine ?? DBNull.Value);
        command.Parameters.AddWithValue("@ytdlp_format", (object?)item.YtdlpFormat ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)item.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("@source", (object?)item.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("@priority", item.Priority);
        command.Parameters.AddWithValue("@takeover", item.Takeover ? 1 : 0);
        command.Parameters.AddWithValue("@cookies_enc", (object?)protectedCookies ?? DBNull.Value);
        command.Parameters.AddWithValue("@created_at", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "@started_at",
            item.StartedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@completed_at",
            item.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
    }

    private async Task<List<DownloadItem>> ReadItemsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var items = new List<DownloadItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    private static DownloadItem ReadItem(SqliteDataReader reader)
    {
        var item = new DownloadItem
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Url = reader.GetString(reader.GetOrdinal("url")),
            FinalUrl = GetNullableString(reader, "final_url"),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            Directory = reader.GetString(reader.GetOrdinal("dir")),
            Category = reader.GetString(reader.GetOrdinal("category")),
            Kind = Enum.TryParse<DownloadEngineKind>(reader.GetString(reader.GetOrdinal("kind")), out var kind)
                ? kind
                : DownloadEngineKind.Http,
            State = Enum.TryParse<DownloadState>(reader.GetString(reader.GetOrdinal("state")), out var state)
                ? state
                : DownloadState.Failed,
            TotalBytes = reader.GetInt64(reader.GetOrdinal("total_bytes")),
            DownloadedBytes = reader.GetInt64(reader.GetOrdinal("downloaded_bytes")),
            Connections = reader.GetInt32(reader.GetOrdinal("connections")),
            ETag = GetNullableString(reader, "etag"),
            LastModified = GetNullableString(reader, "last_modified"),
            Referer = GetNullableString(reader, "referer"),
            UserAgent = GetNullableString(reader, "user_agent"),
            Engine = GetNullableString(reader, "engine"),
            YtdlpFormat = GetNullableString(reader, "ytdlp_format"),
            Error = GetNullableString(reader, "error"),
            Source = GetNullableString(reader, "source"),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            Takeover = reader.GetInt32(reader.GetOrdinal("takeover")) != 0,
            CreatedAt = ParseDate(GetNullableString(reader, "created_at")) ?? DateTimeOffset.UtcNow,
            StartedAt = ParseDate(GetNullableString(reader, "started_at")),
            CompletedAt = ParseDate(GetNullableString(reader, "completed_at"))
        };

        return item;
    }

    private static string? GetNullableString(SqliteDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
