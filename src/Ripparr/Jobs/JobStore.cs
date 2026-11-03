using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ripparr.Options;
using Ripparr.Profiles;

namespace Ripparr.Jobs;

/// <summary>Jobs, profiles and small settings in one SQLite file. Each row holds its object as JSON.</summary>
public sealed class JobStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _connectionString;
    private readonly Lock _lock = new();

    public JobStore(IOptions<RipparrOptions> options) : this(options.Value.DatabasePath)
    {
    }

    public JobStore(string databasePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS jobs (id INTEGER PRIMARY KEY AUTOINCREMENT, status TEXT NOT NULL, created TEXT NOT NULL, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles (id TEXT PRIMARY KEY, position INTEGER NOT NULL, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);

        if (Scalar<long>(connection, "SELECT COUNT(*) FROM profiles") == 0)
        {
            var position = 0;
            foreach (var profile in RipProfile.Defaults())
            {
                SaveProfile(connection, profile, position++);
            }
        }
    }

    // ---- Jobs ----

    public Job Add(Job job)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO jobs (status, created, data) VALUES ($status, $created, '{}'); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$status", job.Status.ToString());
            command.Parameters.AddWithValue("$created", job.Created.ToString("O"));
            job.Id = (long)command.ExecuteScalar()!;
            Update(connection, job);
            return job;
        }
    }

    public void Update(Job job)
    {
        lock (_lock)
        {
            using var connection = Open();
            Update(connection, job);
        }
    }

    public Job? Get(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM jobs WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    /// <summary>Newest first.</summary>
    public List<Job> List(int limit = 200, IEnumerable<JobStatus>? statuses = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var filter = statuses?.ToList();
        command.CommandText = filter is { Count: > 0 }
            ? $"SELECT data FROM jobs WHERE status IN ({string.Join(',', filter.Select(s => $"'{s}'"))}) ORDER BY id DESC LIMIT $limit"
            : "SELECT data FROM jobs ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var jobs = new List<Job>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Deserialize(reader.GetString(0)) is { } job)
            {
                jobs.Add(job);
            }
        }

        return jobs;
    }

    /// <summary>The oldest queued job, or null.</summary>
    public Job? NextQueued()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM jobs WHERE status = 'Queued' ORDER BY id LIMIT 1";
        return command.ExecuteScalar() is string json ? Deserialize(json) : null;
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM jobs WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    // ---- Profiles ----

    public List<RipProfile> Profiles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM profiles ORDER BY position, id";
        var list = new List<RipProfile>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<RipProfile>(reader.GetString(0), Json) is { } profile)
                {
                    list.Add(profile);
                }
            }
            catch (JsonException)
            {
                // A broken row should not hide the others.
            }
        }

        return list;
    }

    public RipProfile? Profile(string id) => Profiles().FirstOrDefault(p => p.Id == id);

    public void SaveProfile(RipProfile profile)
    {
        lock (_lock)
        {
            using var connection = Open();
            var position = Scalar<long>(connection, "SELECT COALESCE(MAX(position), -1) + 1 FROM profiles");
            using var existing = connection.CreateCommand();
            existing.CommandText = "SELECT position FROM profiles WHERE id = $id";
            existing.Parameters.AddWithValue("$id", profile.Id);
            if (existing.ExecuteScalar() is long current)
            {
                position = current;
            }

            SaveProfile(connection, profile, position);
        }
    }

    public void DeleteProfile(string id)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM profiles WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    // ---- Settings ----

    public string? Setting(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            if (value == null)
            {
                command.CommandText = "DELETE FROM settings WHERE key = $key";
            }
            else
            {
                command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = $value";
                command.Parameters.AddWithValue("$value", value);
            }

            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
    }

    private static void SaveProfile(SqliteConnection connection, RipProfile profile, long position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profiles (id, position, data) VALUES ($id, $position, $data)
            ON CONFLICT(id) DO UPDATE SET data = $data, position = $position
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$position", position);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(profile, Json));
        command.ExecuteNonQuery();
    }

    private static void Update(SqliteConnection connection, Job job)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $status, data = $data WHERE id = $id";
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(job, Json));
        command.ExecuteNonQuery();
    }

    private static Job? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Job>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
