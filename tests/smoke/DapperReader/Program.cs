using System.Data;
using System.Security.Cryptography;
using Dapper;

if (args is not [var expectedDll])
{
    Console.Error.WriteLine("Usage: DapperReader /absolute/path/to/Dapper.dll");
    return 2;
}

using var expected = File.OpenRead(expectedDll);
using var loaded = File.OpenRead(typeof(SqlMapper).Assembly.Location);
if (!SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(loaded)))
{
    Console.Error.WriteLine($"Loaded {loaded.Name}, which differs from {expected.Name}.");
    return 1;
}

var table = new DataTable();
table.Columns.Add("Name", typeof(string));
table.Columns.Add("Id", typeof(int));
table.Rows.Add("abc", 123);
table.Rows.Add(DBNull.Value, 456);

using var reader = table.CreateDataReader();
var parser = reader.GetRowParser<Row>();
if (!ReferenceEquals(parser, reader.GetRowParser(typeof(Row))))
    throw new InvalidOperationException("The typed and untyped calls returned different parsers.");

using var secondReader = table.CreateDataReader();
if (!ReferenceEquals(parser, secondReader.GetRowParser<Row>()))
    throw new InvalidOperationException("The parser was not reused for an identical schema.");

var rows = reader.Parse<Row>().ToList();
if (rows is not [{ Name: "abc", Id: 123 }, { Name: null, Id: 456 }])
    throw new InvalidOperationException("Column mapping or DBNull handling changed.");

Console.WriteLine(string.Join(";", rows.Select(row => $"{row.Id}:{row.Name ?? "<NULL>"}")));
return 0;

public sealed class Row
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
