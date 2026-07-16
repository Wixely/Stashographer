using Microsoft.Extensions.Logging.Abstractions;
using Stashographer.Data;
using Stashographer.Data.Migrations;

namespace Stashographer.Tests;

/// <summary>
/// A throwaway file-backed SQLite database with all migrations applied, for exercising the
/// Dapper data layer against a real database. Each instance uses its own temp file.
/// </summary>
public sealed class TestDb : IAsyncDisposable
{
    public string Path { get; }
    public IDbConnectionFactory Factory { get; }

    private TestDb(string path, IDbConnectionFactory factory)
    {
        Path = path;
        Factory = factory;
    }

    public static async Task<TestDb> CreateAsync()
    {
        DapperConfig.Register();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stash_test_{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory($"Data Source={path}");
        await new MigrationRunner(factory, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        return new TestDb(path, factory);
    }

    public ValueTask DisposeAsync()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }
}
