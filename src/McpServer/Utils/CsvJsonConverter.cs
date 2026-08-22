using System.Text;
using System.Text.Json;

namespace McpServer.Utils;

/// <summary>
/// Minimaler, robuster CSV-Parser (unterstützt gequotete Felder mit Kommas/Zeilenumbrüchen)
/// für die von den Microsoft-Graph-Reports-Endpunkten gelieferten CSV-Dateien.
/// Wandelt die erste Zeile als Header in JSON-Property-Namen um.
/// </summary>
public static class CsvJsonConverter
{
    public static string ConvertToJsonArray(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0)
        {
            return "[]";
        }

        var headers = rows[0];
        var records = new List<Dictionary<string, string>>();

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0]))
            {
                continue; // leere Zeile am Ende überspringen
            }

            var record = new Dictionary<string, string>();
            for (var col = 0; col < headers.Count; col++)
            {
                record[headers[col]] = col < row.Count ? row[col] : string.Empty;
            }
            records.Add(record);
        }

        return JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    currentRow.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    currentRow.Add(field.ToString());
                    field.Clear();
                    rows.Add(currentRow);
                    currentRow = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            rows.Add(currentRow);
        }

        return rows;
    }
}
