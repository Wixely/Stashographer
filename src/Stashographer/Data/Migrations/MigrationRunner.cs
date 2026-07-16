using System.Reflection;
using Dapper;

namespace Stashographer.Data.Migrations;

/// <summary>
/// Applies ordered <c>*.sql</c> migration scripts (embedded resources under
/// <c>Data/Migrations/Scripts</c>) that have not yet run, tracked in a
/// <c>schema_migrations</c> table. Each script runs in a transaction. Hand-written SQL —
/// no ORM-generated migrations.
/// </summary>
public class MigrationRunner(IDbConnectionFactory connectionFactory, ILogger<MigrationRunner> logger)
{
    private const string ScriptPrefix = "Stashographer.Data.Migrations.Scripts.";

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        using var conn = await connectionFactory.OpenAsync(ct);

        await conn.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS schema_migrations (Id TEXT PRIMARY KEY, AppliedAt TEXT NOT NULL);");

        var applied = (await conn.QueryAsync<string>("SELECT Id FROM schema_migrations;"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, sql) in LoadScripts())
        {
            if (applied.Contains(id)) continue;

            logger.LogInformation("Applying migration {Migration}", id);
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_migrations (Id, AppliedAt) VALUES (@Id, @At);",
                new { Id = id, At = DateTimeOffset.UtcNow.ToString("O") }, tx);
            tx.Commit();
        }
    }

    /// <summary>Embedded <c>*.sql</c> resources, ordered by file name.</summary>
    private static IEnumerable<(string Id, string Sql)> LoadScripts()
    {
        var asm = typeof(MigrationRunner).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ScriptPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var id = name[ScriptPrefix.Length..];
            yield return (id, reader.ReadToEnd());
        }
    }
}
