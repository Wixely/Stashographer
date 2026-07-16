using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;

namespace Stashographer.Data;

/// <summary>Serializes a value to/from a JSON TEXT column.</summary>
public class JsonTypeHandler<T> : SqlMapper.TypeHandler<T> where T : new()
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value, Options);
    }

    public override T Parse(object? value) =>
        value is string s && !string.IsNullOrWhiteSpace(s)
            ? JsonSerializer.Deserialize<T>(s, Options) ?? new T()
            : new T();
}

/// <summary>Stores <see cref="DateOnly"/> as an ISO <c>yyyy-MM-dd</c> TEXT column.</summary>
public class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public override DateOnly Parse(object value) => value switch
    {
        string s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly")
    };
}

/// <summary>Stores <see cref="DateTimeOffset"/> as a round-trippable ISO TEXT column.</summary>
public class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);
    }

    public override DateTimeOffset Parse(object value) => value switch
    {
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset")
    };
}

public static class DapperConfig
{
    private static bool _configured;

    /// <summary>Registers all custom type handlers. Idempotent.</summary>
    public static void Register()
    {
        if (_configured) return;
        _configured = true;

        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<Dictionary<string, string>>());
        SqlMapper.AddTypeHandler(new JsonTypeHandler<List<string>>());
    }
}
