using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Nomad.Core.Hub;

namespace Nomad.Core.Usage;

public readonly record struct MonthlyTokenTotals(LocalTokens Claude, LocalTokens Codex);

/// <summary>Persists file checkpoints and individual Claude message counts so deduplication survives app restarts.</summary>
public sealed class LocalTokenCache(string database, string claudeRoot, string codexRoot)
{
    public MonthlyTokenTotals? ReadCached(DateTimeOffset since)
    {
        if (!File.Exists(database)) return null;

        using var db = Open(SqliteOpenMode.ReadOnly);
        using var query = ScopeQuery(db, transaction: null, since);
        using var row = query.ExecuteReader();
        return row.Read() ? new MonthlyTokenTotals(
            new LocalTokens(row.GetInt64(0), row.GetInt64(1)), new LocalTokens(row.GetInt64(2), row.GetInt64(3))) : null;
    }

    public MonthlyTokenTotals Refresh(DateTimeOffset since)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var db = Open(SqliteOpenMode.ReadWriteCreate);
        using var tx = db.BeginTransaction();
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS totals (
                month INTEGER NOT NULL, claude_root TEXT NOT NULL, codex_root TEXT NOT NULL,
                claude_in INTEGER NOT NULL, claude_out INTEGER NOT NULL, codex_in INTEGER NOT NULL, codex_out INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS logs (
                path TEXT PRIMARY KEY, codex INTEGER NOT NULL, written INTEGER NOT NULL, created INTEGER NOT NULL,
                position INTEGER NOT NULL, complete INTEGER NOT NULL, signature TEXT NOT NULL,
                prev_in INTEGER NOT NULL, prev_out INTEGER NOT NULL, total_in INTEGER NOT NULL, total_out INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                path TEXT NOT NULL, id TEXT NOT NULL, input INTEGER NOT NULL, output INTEGER NOT NULL,
                PRIMARY KEY (path, id)
            );
            CREATE TEMP TABLE seen (path TEXT PRIMARY KEY);
            """);

        using (var scope = ScopeQuery(db, tx, since))
        {
            if (scope.ExecuteScalar() is null) Execute(db, tx, "DELETE FROM messages; DELETE FROM logs; DELETE FROM totals;");
        }

        ScanRoot(claudeRoot, codex: false);
        foreach (var root in LocalTokenUsage.CodexRoots(codexRoot)) ScanRoot(root, codex: true);

        Execute(db, tx, """
            DELETE FROM messages WHERE path NOT IN (SELECT path FROM seen);
            DELETE FROM logs WHERE path NOT IN (SELECT path FROM seen);
            """);
        var claude = Sum("""
            SELECT COALESCE(SUM(input), 0), COALESCE(SUM(output), 0)
            FROM (SELECT MAX(input) AS input, MAX(output) AS output FROM messages GROUP BY id);
            """);
        var codex = Sum("SELECT COALESCE(SUM(total_in), 0), COALESCE(SUM(total_out), 0) FROM logs WHERE codex = 1;");
        Execute(db, tx, "DELETE FROM totals;");
        using (var update = Command(db, tx, "INSERT INTO totals VALUES ($month, $claude, $codex, $ci, $co, $xi, $xo);"))
        {
            BindScope(update, since);
            update.Parameters.AddWithValue("$ci", claude.Input);
            update.Parameters.AddWithValue("$co", claude.Output);
            update.Parameters.AddWithValue("$xi", codex.Input);
            update.Parameters.AddWithValue("$xo", codex.Output);
            update.ExecuteNonQuery();
        }
        tx.Commit();
        return new MonthlyTokenTotals(claude, codex);

        LocalTokens Sum(string sql)
        {
            using var query = Command(db, tx, sql);
            using var row = query.ExecuteReader();
            row.Read();
            return new LocalTokens(row.GetInt64(0), row.GetInt64(1));
        }

        void ScanRoot(string root, bool codex)
        {
            foreach (var path in LocalTokenUsage.Files(root))
            {
                var info = new FileInfo(path);
                var written = info.LastWriteTimeUtc;
                if (written < since.UtcDateTime) continue;

                using (var mark = ForPath(db, tx, "INSERT OR IGNORE INTO seen VALUES ($path);", path))
                {
                    mark.ExecuteNonQuery();
                }
                tx.Save("file");
                try
                {
                    RefreshFile(db, tx, info, written.Ticks, since, codex);
                    tx.Release("file");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    tx.Rollback("file");
                    tx.Release("file");
                }
            }
        }
    }

    private static void RefreshFile(SqliteConnection db, SqliteTransaction tx, FileInfo info, long written, DateTimeOffset since, bool codex)
    {
        var path = info.FullName;
        var created = info.CreationTimeUtc.Ticks;
        var length = info.Length;
        var saved = default(LocalTokenUsage.Cursor);
        string? signature = null;
        using (var query = ForPath(db, tx,
            "SELECT written, created, position, complete, signature, prev_in, prev_out, total_in, total_out FROM logs WHERE path = $path;", path))
        using (var row = query.ExecuteReader())
        {
            if (row.Read() && row.GetInt64(1) == created)
            {
                var offset = row.GetInt64(2);
                if (offset == length && row.GetInt64(0) == written) return;
                if (offset < length && row.GetBoolean(3))
                {
                    saved = new LocalTokenUsage.Cursor(offset, true, new LocalTokens(row.GetInt64(5), row.GetInt64(6)),
                        new LocalTokens(row.GetInt64(7), row.GetInt64(8)));
                    signature = row.GetString(4);
                }
            }
        }

        using var stream = LocalTokenUsage.Open(path);
        if (stream.Length < length) throw new EndOfStreamException();
        if (signature is null || signature != Signature(stream.SafeFileHandle, saved.Position))
        {
            using var clear = ForPath(db, tx, "DELETE FROM messages WHERE path = $path;", path);
            clear.ExecuteNonQuery();
            saved = default;
        }

        using var record = ForPath(db, tx, """
            INSERT INTO messages VALUES ($path, $id, $input, $output)
            ON CONFLICT(path, id) DO UPDATE SET input = MAX(input, excluded.input), output = MAX(output, excluded.output);
            """, path);
        var id = record.Parameters.Add("$id", SqliteType.Text);
        var input = record.Parameters.Add("$input", SqliteType.Integer);
        var output = record.Parameters.Add("$output", SqliteType.Integer);
        var cursor = LocalTokenUsage.Scan(stream, length, path, since, codex, saved, (key, count) =>
        {
            id.Value = key;
            input.Value = count.Input;
            output.Value = count.Output;
            record.ExecuteNonQuery();
        });

        using var update = ForPath(db, tx, """
            INSERT OR REPLACE INTO logs VALUES ($path, $codex, $written, $created, $position, $complete, $signature,
                $prev_in, $prev_out, $total_in, $total_out);
            """, path);
        update.Parameters.AddWithValue("$codex", codex);
        update.Parameters.AddWithValue("$written", written);
        update.Parameters.AddWithValue("$created", created);
        update.Parameters.AddWithValue("$position", cursor.Position);
        update.Parameters.AddWithValue("$complete", cursor.Complete);
        update.Parameters.AddWithValue("$signature", Signature(stream.SafeFileHandle, cursor.Position));
        update.Parameters.AddWithValue("$prev_in", cursor.Previous.Input);
        update.Parameters.AddWithValue("$prev_out", cursor.Previous.Output);
        update.Parameters.AddWithValue("$total_in", cursor.Total.Input);
        update.Parameters.AddWithValue("$total_out", cursor.Total.Output);
        update.ExecuteNonQuery();
    }

    private static string Signature(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long position)
    {
        // Sample both ends of the processed prefix to detect truncation or replacement before resuming.
        const int sample = 2048;
        Span<byte> bytes = stackalloc byte[sample * 2];
        var head = (int)Math.Min(position, sample);
        var tail = (int)Math.Min(position - head, sample);
        ReadExactly(bytes[..head], 0);
        ReadExactly(bytes.Slice(head, tail), position - tail);
        return Convert.ToHexString(SHA256.HashData(bytes[..(head + tail)]));

        void ReadExactly(Span<byte> target, long offset)
        {
            while (!target.IsEmpty)
            {
                var read = RandomAccess.Read(handle, target, offset);
                if (read == 0) throw new EndOfStreamException();
                target = target[read..];
                offset += read;
            }
        }
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        SqliteSetup.Ensure();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 2,
        }.ToString());
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private SqliteCommand ScopeQuery(SqliteConnection db, SqliteTransaction? transaction, DateTimeOffset since)
    {
        var query = Command(db, transaction, """
            SELECT claude_in, claude_out, codex_in, codex_out FROM totals
            WHERE month = $month AND claude_root = $claude AND codex_root = $codex;
            """);
        BindScope(query, since);
        return query;
    }

    private void BindScope(SqliteCommand command, DateTimeOffset since)
    {
        command.Parameters.AddWithValue("$month", since.UtcTicks);
        command.Parameters.AddWithValue("$claude", claudeRoot);
        command.Parameters.AddWithValue("$codex", codexRoot);
    }

    private static SqliteCommand ForPath(SqliteConnection db, SqliteTransaction tx, string sql, string path)
    {
        var command = Command(db, tx, sql);
        command.Parameters.AddWithValue("$path", path);
        return command;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var command = Command(db, tx, sql);
        command.ExecuteNonQuery();
    }
}
