using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WallTrek.Services.Personalization;

/// <summary>Append-only provenance alongside the existing GeneratedImages history.</summary>
public sealed class PersonalizationHistory
{
    private readonly string _connection;
    public PersonalizationHistory(string? dbPath = null)
    {
        var location = dbPath ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "WallTrek", "walltrek.db");
        Directory.CreateDirectory(Path.GetDirectoryName(location)!);
        _connection = new SqliteConnectionStringBuilder { DataSource = location }.ToString();
        using var db = new SqliteConnection(_connection);
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS PersonalizedRuns (
              ImagePath TEXT NOT NULL PRIMARY KEY,
              GeneratedUtc TEXT NOT NULL,
              ScenarioId TEXT NOT NULL,
              ScenarioName TEXT NOT NULL,
              ResolvedPrompt TEXT NOT NULL,
              VariablesJson TEXT NOT NULL,
              PhotoIdsJson TEXT NOT NULL,
              CombinationKey TEXT NOT NULL,
              ProviderId TEXT NOT NULL,
              ModelId TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PersonalizedRuns_Date
            ON PersonalizedRuns (GeneratedUtc DESC);";
        command.ExecuteNonQuery();

        // Keep the additional provenance in sync with upstream history archive,
        // restore and delete operations without changing DatabaseService itself.
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='GeneratedImages'";
        if (check.ExecuteScalar() is not null)
        {
            using var triggers = db.CreateCommand();
            triggers.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS trg_PersonalizationImageMoved
                AFTER UPDATE OF ImagePath ON GeneratedImages
                BEGIN
                  UPDATE PersonalizedRuns SET ImagePath = NEW.ImagePath WHERE ImagePath = OLD.ImagePath;
                END;
                CREATE TRIGGER IF NOT EXISTS trg_PersonalizationImageDeleted
                AFTER DELETE ON GeneratedImages
                BEGIN
                  DELETE FROM PersonalizedRuns WHERE ImagePath = OLD.ImagePath;
                END;";
            triggers.ExecuteNonQuery();
        }
    }

    public async Task RecordAsync(string imagePath, PreparedWallpaper prepared,
        string scenarioName, string providerId, string modelId, CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connection);
        await db.OpenAsync(ct);
        using var command = db.CreateCommand();
        command.CommandText = @"INSERT INTO PersonalizedRuns
            (ImagePath, GeneratedUtc, ScenarioId, ScenarioName, ResolvedPrompt,
             VariablesJson, PhotoIdsJson, CombinationKey, ProviderId, ModelId)
            VALUES (@path, @time, @scenario, @name, @prompt, @vars, @refs, @key, @provider, @model)";
        command.Parameters.AddWithValue("@path", imagePath);
        command.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("@scenario", prepared.ScenarioId.ToString("N"));
        command.Parameters.AddWithValue("@name", scenarioName);
        command.Parameters.AddWithValue("@prompt", prepared.FinalPrompt);
        command.Parameters.AddWithValue("@vars", JsonSerializer.Serialize(prepared.Variables));
        command.Parameters.AddWithValue("@refs", JsonSerializer.Serialize(prepared.ReferenceIds));
        command.Parameters.AddWithValue("@key", prepared.CombinationKey);
        command.Parameters.AddWithValue("@provider", providerId);
        command.Parameters.AddWithValue("@model", modelId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PersonalizedRunSummary>> RecentAsync(int limit = 20,
        CancellationToken ct = default)
    {
        using var db = new SqliteConnection(_connection);
        await db.OpenAsync(ct);
        using var command = db.CreateCommand();
        command.CommandText = @"SELECT ImagePath, GeneratedUtc, ScenarioName, ResolvedPrompt,
                                      VariablesJson, ProviderId, ModelId
                                FROM PersonalizedRuns ORDER BY GeneratedUtc DESC LIMIT @limit";
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));
        var rows = new List<PersonalizedRunSummary>();
        using var cursor = await command.ExecuteReaderAsync(ct);
        while (await cursor.ReadAsync(ct))
            rows.Add(new PersonalizedRunSummary(cursor.GetString(0),
                DateTimeOffset.Parse(cursor.GetString(1)), cursor.GetString(2),
                cursor.GetString(3), cursor.GetString(4), cursor.GetString(5),
                cursor.GetString(6)));
        return rows;
    }
}

public sealed record PersonalizedRunSummary(string ImagePath, DateTimeOffset Time,
    string ScenarioName, string Prompt, string VariablesJson, string ProviderId, string ModelId)
{
    public string Display => Time.ToLocalTime().ToString("g") + " · " + ScenarioName +
        " · " + ModelId;
}
