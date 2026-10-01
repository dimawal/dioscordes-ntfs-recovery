using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NtfsRecovery.Core.Scan;

public sealed record ScanCheckpoint(long ScanStartOffset, long ScanEndOffset, long LastScannedOffset);

public sealed record ScanGeometry(long PartitionOffset, int BytesPerSector, int BytesPerCluster, int MftRecordSize);

/// <summary>
/// SQLite-backed persistence for scan results, so a multi-hour scan of a 238 GB region
/// is not lost if interrupted, and so later commands (tree/list/info/recover) can work
/// from a saved result set instead of re-scanning the source device.
///
/// This store only ever opens its own database file for read/write; it has no awareness
/// of the source device and cannot write to it.
/// </summary>
public sealed class ScanStore : IDisposable
{
    private readonly SqliteConnection _connection;

    private ScanStore(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static ScanStore OpenOrCreate(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Records (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RecordNumber INTEGER NOT NULL,
                    SourceOffset INTEGER NOT NULL,
                    Name TEXT,
                    ParentRecordNumber INTEGER,
                    IsDirectory INTEGER NOT NULL,
                    IsInUse INTEGER NOT NULL,
                    Json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Records_RecordNumber ON Records(RecordNumber);
                CREATE INDEX IF NOT EXISTS IX_Records_ParentRecordNumber ON Records(ParentRecordNumber);

                CREATE TABLE IF NOT EXISTS Checkpoint (
                    SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                    ScanStartOffset INTEGER NOT NULL,
                    ScanEndOffset INTEGER NOT NULL,
                    LastScannedOffset INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Geometry (
                    SingletonId INTEGER PRIMARY KEY CHECK (SingletonId = 1),
                    PartitionOffset INTEGER NOT NULL,
                    BytesPerSector INTEGER NOT NULL,
                    BytesPerCluster INTEGER NOT NULL,
                    MftRecordSize INTEGER NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        return new ScanStore(connection);
    }

    public void InsertRecords(IEnumerable<ScanRecordDto> records)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Records (RecordNumber, SourceOffset, Name, ParentRecordNumber, IsDirectory, IsInUse, Json)
            VALUES ($recordNumber, $sourceOffset, $name, $parentRecordNumber, $isDirectory, $isInUse, $json);
            """;

        var recordNumberParam = cmd.Parameters.Add("$recordNumber", SqliteType.Integer);
        var sourceOffsetParam = cmd.Parameters.Add("$sourceOffset", SqliteType.Integer);
        var nameParam = cmd.Parameters.Add("$name", SqliteType.Text);
        var parentParam = cmd.Parameters.Add("$parentRecordNumber", SqliteType.Integer);
        var isDirectoryParam = cmd.Parameters.Add("$isDirectory", SqliteType.Integer);
        var isInUseParam = cmd.Parameters.Add("$isInUse", SqliteType.Integer);
        var jsonParam = cmd.Parameters.Add("$json", SqliteType.Text);

        foreach (ScanRecordDto record in records)
        {
            recordNumberParam.Value = record.RecordNumber;
            sourceOffsetParam.Value = record.SourceOffset;
            nameParam.Value = (object?)record.Name ?? DBNull.Value;
            parentParam.Value = (object?)record.ParentRecordNumber ?? DBNull.Value;
            isDirectoryParam.Value = record.IsDirectory ? 1 : 0;
            isInUseParam.Value = record.IsInUse ? 1 : 0;
            jsonParam.Value = JsonSerializer.Serialize(record);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SaveCheckpoint(ScanCheckpoint checkpoint)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Checkpoint (SingletonId, ScanStartOffset, ScanEndOffset, LastScannedOffset)
            VALUES (1, $start, $end, $last)
            ON CONFLICT(SingletonId) DO UPDATE SET
                ScanStartOffset = excluded.ScanStartOffset,
                ScanEndOffset = excluded.ScanEndOffset,
                LastScannedOffset = excluded.LastScannedOffset;
            """;
        cmd.Parameters.AddWithValue("$start", checkpoint.ScanStartOffset);
        cmd.Parameters.AddWithValue("$end", checkpoint.ScanEndOffset);
        cmd.Parameters.AddWithValue("$last", checkpoint.LastScannedOffset);
        cmd.ExecuteNonQuery();
    }

    public ScanCheckpoint? TryLoadCheckpoint()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT ScanStartOffset, ScanEndOffset, LastScannedOffset FROM Checkpoint WHERE SingletonId = 1;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ScanCheckpoint(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public List<ScanRecordDto> LoadAllRecords()
    {
        var results = new List<ScanRecordDto>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Json FROM Records;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string json = reader.GetString(0);
            ScanRecordDto? dto = JsonSerializer.Deserialize<ScanRecordDto>(json);
            if (dto is not null)
                results.Add(dto);
        }
        return results;
    }

    public void SaveGeometry(ScanGeometry geometry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Geometry (SingletonId, PartitionOffset, BytesPerSector, BytesPerCluster, MftRecordSize)
            VALUES (1, $partitionOffset, $bytesPerSector, $bytesPerCluster, $mftRecordSize)
            ON CONFLICT(SingletonId) DO UPDATE SET
                PartitionOffset = excluded.PartitionOffset,
                BytesPerSector = excluded.BytesPerSector,
                BytesPerCluster = excluded.BytesPerCluster,
                MftRecordSize = excluded.MftRecordSize;
            """;
        cmd.Parameters.AddWithValue("$partitionOffset", geometry.PartitionOffset);
        cmd.Parameters.AddWithValue("$bytesPerSector", geometry.BytesPerSector);
        cmd.Parameters.AddWithValue("$bytesPerCluster", geometry.BytesPerCluster);
        cmd.Parameters.AddWithValue("$mftRecordSize", geometry.MftRecordSize);
        cmd.ExecuteNonQuery();
    }

    public ScanGeometry? TryLoadGeometry()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT PartitionOffset, BytesPerSector, BytesPerCluster, MftRecordSize FROM Geometry WHERE SingletonId = 1;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ScanGeometry(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    public void Dispose() => _connection.Dispose();
}
