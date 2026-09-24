namespace NugoloMag.Analyst.Domain.Discovery;

/// <summary>Nome qualificato di tabella o vista. <see cref="Quoted"/> è sicuro da concatenare in T-SQL.</summary>
public readonly record struct TableName(string Schema, string Name)
{
    public string Quoted => $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Name)}";

    public static string QuoteIdentifier(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public static TableName Parse(string value)
    {
        var dot = value.IndexOf('.');
        return dot <= 0 ? new TableName("dbo", value) : new TableName(value[..dot], value[(dot + 1)..]);
    }

    public override string ToString() => $"{Schema}.{Name}";
}

public sealed record CatalogColumn(string Name, string SqlType, bool IsNullable, bool IsPrimaryKey)
{
    private static readonly HashSet<string> DateTypes = new(StringComparer.OrdinalIgnoreCase) { "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset" };
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric", "int", "bigint", "smallint", "tinyint", "float", "real", "money", "smallmoney" };
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "text", "ntext" };

    public bool IsDate => DateTypes.Contains(SqlType);
    public bool IsNumeric => NumericTypes.Contains(SqlType);
    public bool IsText => TextTypes.Contains(SqlType);
    public bool IsKeyLike => IsText || SqlType is "int" or "bigint" or "smallint";
}

public sealed record CatalogTable(TableName Name, bool IsView, long? RowCount, IReadOnlyList<CatalogColumn> Columns)
{
    public CatalogColumn? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record ForeignKey(TableName From, string FromColumn, TableName To, string ToColumn);

/// <summary>Fotografia dello schema del database sorgente.</summary>
public sealed record DatabaseCatalog(string Database, IReadOnlyList<CatalogTable> Tables, IReadOnlyList<ForeignKey> ForeignKeys)
{
    public CatalogTable? Find(TableName name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name.Schema, name.Schema, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(t.Name.Name, name.Name, StringComparison.OrdinalIgnoreCase));

    public int ColumnCount => Tables.Sum(t => t.Columns.Count);
}
