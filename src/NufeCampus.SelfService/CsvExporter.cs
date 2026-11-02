using System.Text;
namespace NufeCampus.SelfService;

public static class CsvExporter
{
    public static async Task WriteAsync<T>(string path, string[] headers, Func<int, CancellationToken, Task<Page<T>>> query, Func<T, string[]> columns, CancellationToken token, Action<int, int>? progress = null)
    {
        string temp = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(true)); await writer.WriteLineAsync(SelfData.Csv(headers));
                int expected = -1, count = 0;
                for (int p = 1; p <= 1000; p++)
                {
                    token.ThrowIfCancellationRequested(); var page = await query(p, token); if (page.Total > 10_000) throw new SelfException("too_many");
                    if (expected < 0) expected = page.Total; else if (page.Total != expected) throw new SelfException("changed");
                    // Different sessions may legitimately have identical visible values; preserve every returned row.
                    foreach (var row in page.Rows) { string line = SelfData.Csv(columns(row)); await writer.WriteLineAsync(line.AsMemory(), token); count++; }
                    progress?.Invoke(count, expected); if (count == expected) break; if (page.Rows.Count != 10 || count > expected) throw new SelfException("changed");
                }
                if (count != expected) throw new SelfException("changed");
            }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
