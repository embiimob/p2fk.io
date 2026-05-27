using Microsoft.Data.Sqlite;
using P2FK.IO.Models;
using P2FK.IO.Options;
using System.Globalization;

namespace P2FK.IO.Services
{
    public class IngressMetadataStore
    {
        private const string InitialMigrationId = "20260527_initial";
        private readonly string _connectionString;
        private readonly ILogger<IngressMetadataStore> _logger;

        public IngressMetadataStore(IOptions<IpfsIngressOptions> options, IWebHostEnvironment environment, ILogger<IngressMetadataStore> logger)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(environment);

            _logger = logger;
            string databasePath = options.Value.DatabasePath;
            if (!Path.IsPathRooted(databasePath))
                databasePath = Path.Combine(environment.ContentRootPath, databasePath);

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS __IpfsIngressMigrations (
                    Id TEXT PRIMARY KEY,
                    AppliedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS UploadRecord (
                    Id TEXT PRIMARY KEY,
                    CID TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    FileSizeBytes INTEGER NOT NULL,
                    ClientIp TEXT NOT NULL,
                    UploadedUtc TEXT NOT NULL,
                    ExpiresUtc TEXT NOT NULL,
                    IsPinned INTEGER NOT NULL,
                    IsExpired INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS DailyUsage (
                    Id TEXT PRIMARY KEY,
                    ClientIp TEXT NOT NULL,
                    UsageDateUtc TEXT NOT NULL,
                    BytesUploaded INTEGER NOT NULL,
                    UNIQUE(ClientIp, UsageDateUtc)
                );

                CREATE INDEX IF NOT EXISTS IX_UploadRecord_ClientIp_UploadedUtc ON UploadRecord (ClientIp, UploadedUtc);
                CREATE INDEX IF NOT EXISTS IX_UploadRecord_Expiry ON UploadRecord (IsExpired, ExpiresUtc);
                CREATE INDEX IF NOT EXISTS IX_UploadRecord_CID ON UploadRecord (CID);
                CREATE INDEX IF NOT EXISTS IX_DailyUsage_ClientIp_UsageDateUtc ON DailyUsage (ClientIp, UsageDateUtc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var migration = connection.CreateCommand();
            migration.CommandText = "INSERT OR IGNORE INTO __IpfsIngressMigrations (Id, AppliedUtc) VALUES ($id, $appliedUtc);";
            migration.Parameters.AddWithValue("$id", InitialMigrationId);
            migration.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await migration.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("IPFS ingress database initialized");
        }

        public async Task<long> GetRollingUsageBytesAsync(string clientIp, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(FileSizeBytes), 0) FROM UploadRecord WHERE ClientIp = $clientIp AND UploadedUtc >= $sinceUtc;";
            command.Parameters.AddWithValue("$clientIp", clientIp);
            command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToString("O", CultureInfo.InvariantCulture));
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? 0L : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }

        public async Task RecordUploadAsync(IngressUploadRecord record, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO UploadRecord (Id, CID, FileName, FileSizeBytes, ClientIp, UploadedUtc, ExpiresUtc, IsPinned, IsExpired)
                    VALUES ($id, $cid, $fileName, $fileSizeBytes, $clientIp, $uploadedUtc, $expiresUtc, $isPinned, $isExpired);
                    """;
                command.Parameters.AddWithValue("$id", record.Id.ToString());
                command.Parameters.AddWithValue("$cid", record.CID);
                command.Parameters.AddWithValue("$fileName", record.FileName);
                command.Parameters.AddWithValue("$fileSizeBytes", record.FileSizeBytes);
                command.Parameters.AddWithValue("$clientIp", record.ClientIp);
                command.Parameters.AddWithValue("$uploadedUtc", record.UploadedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$expiresUtc", record.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$isPinned", record.IsPinned ? 1 : 0);
                command.Parameters.AddWithValue("$isExpired", record.IsExpired ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var usage = connection.CreateCommand())
            {
                usage.Transaction = transaction;
                usage.CommandText = """
                    INSERT INTO DailyUsage (Id, ClientIp, UsageDateUtc, BytesUploaded)
                    VALUES ($id, $clientIp, $usageDateUtc, $bytesUploaded)
                    ON CONFLICT(ClientIp, UsageDateUtc)
                    DO UPDATE SET BytesUploaded = BytesUploaded + excluded.BytesUploaded;
                    """;
                usage.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                usage.Parameters.AddWithValue("$clientIp", record.ClientIp);
                usage.Parameters.AddWithValue("$usageDateUtc", DateOnly.FromDateTime(record.UploadedUtc.UtcDateTime).ToString("O", CultureInfo.InvariantCulture));
                usage.Parameters.AddWithValue("$bytesUploaded", record.FileSizeBytes);
                await usage.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<(int ActivePins, long QueuedBytes, DateTimeOffset? OldestExpirationUtc)> GetActiveStatusAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    COALESCE(SUM(CASE WHEN IsPinned = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(FileSizeBytes), 0),
                    MIN(ExpiresUtc)
                FROM UploadRecord
                WHERE IsExpired = 0 AND ExpiresUtc > $nowUtc;
                """;
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            DateTimeOffset? oldestExpirationUtc = null;
            if (!reader.IsDBNull(2))
                oldestExpirationUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            return (reader.GetInt32(0), reader.GetInt64(1), oldestExpirationUtc);
        }

        public async Task<IReadOnlyList<IngressUploadRecord>> GetActiveUploadsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CID, FileName, FileSizeBytes, ClientIp, UploadedUtc, ExpiresUtc, IsPinned, IsExpired
                FROM UploadRecord
                WHERE IsExpired = 0 AND ExpiresUtc > $nowUtc
                ORDER BY UploadedUtc ASC;
                """;
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            return await ReadUploadsAsync(command, cancellationToken);
        }

        public async Task<IReadOnlyList<IngressUploadRecord>> GetExpiredUploadsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, CID, FileName, FileSizeBytes, ClientIp, UploadedUtc, ExpiresUtc, IsPinned, IsExpired
                FROM UploadRecord
                WHERE IsExpired = 0 AND ExpiresUtc <= $nowUtc
                ORDER BY ExpiresUtc ASC;
                """;
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            return await ReadUploadsAsync(command, cancellationToken);
        }

        public async Task<bool> IsCidActiveAsync(string cid, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM UploadRecord
                    WHERE CID = $cid AND IsExpired = 0 AND ExpiresUtc > $nowUtc
                );
                """;
            command.Parameters.AddWithValue("$cid", cid);
            command.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O", CultureInfo.InvariantCulture));
            object? result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long value && value == 1;
        }

        public async Task MarkExpiredAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE UploadRecord SET IsPinned = 0, IsExpired = 1 WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }

        private static async Task<IReadOnlyList<IngressUploadRecord>> ReadUploadsAsync(SqliteCommand command, CancellationToken cancellationToken)
        {
            var uploads = new List<IngressUploadRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                uploads.Add(new IngressUploadRecord
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    CID = reader.GetString(1),
                    FileName = reader.GetString(2),
                    FileSizeBytes = reader.GetInt64(3),
                    ClientIp = reader.GetString(4),
                    UploadedUtc = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    ExpiresUtc = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    IsPinned = reader.GetInt64(7) == 1,
                    IsExpired = reader.GetInt64(8) == 1
                });
            }

            return uploads;
        }
    }
}
