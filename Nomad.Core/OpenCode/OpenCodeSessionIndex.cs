using Microsoft.Data.Sqlite;
using Nomad.Core.Hub;

namespace Nomad.Core.OpenCode;

/// <summary>Reads sidebar metadata without launching OpenCode or changing its database.</summary>
public static class OpenCodeSessionIndex
{
    public static IReadOnlyList<OpenCodeSessionInfo> Read()
    {
        var path = OpenCodeServer.DatabasePath;
        if (!File.Exists(path)) return [];

        SqliteSetup.Ensure();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT id, directory, title, time_updated
            FROM session
            WHERE parent_id IS NULL
            ORDER BY time_updated DESC
            LIMIT 500
            """;
        using var rows = query.ExecuteReader();
        var sessions = new List<OpenCodeSessionInfo>();
        while (rows.Read())
        {
            sessions.Add(new OpenCodeSessionInfo(
                rows.GetString(0),
                rows.GetString(1),
                OpenCodeTitles.Clean(rows.GetString(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(3)).UtcDateTime,
                Model: null));
        }
        return sessions;
    }
}
