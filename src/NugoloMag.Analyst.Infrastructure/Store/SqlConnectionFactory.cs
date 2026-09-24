using Microsoft.Data.SqlClient;

namespace NugoloMag.Analyst.Infrastructure.Store;

/// <summary>Connessioni al database di NugoloMag (schema nugolo), separato dal gestionale.</summary>
public sealed class SqlConnectionFactory(string connectionString)
{
    public string ConnectionString => connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public static SqlParameter Param(string name, System.Data.SqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
