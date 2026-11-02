using ExcelDataReader;
using NufeCampus.SelfService;
using System.Globalization;
using System.IO;
using System.Text;
namespace NufeCampus.App;

public static class OfficialHistory
{
    private static readonly string[] Headers = ["上线时间", "下线时间", "使用时长(分钟)", "使用流量(MB)", "计费金额(元)", "国际上行流量(MB)", "国际下行流量(MB)", "国内上行流量(MB)", "国内下行流量(MB)", "登陆所在IP", "NASIP", "NASPORT"];
    public static HistoryRow[] Parse(byte[] bytes)
    {
        try { return ParseWorkbook(bytes); } catch (SelfException) { throw; } catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or ExcelDataReader.Exceptions.HeaderException) { throw new SelfException("protocol"); }
    }
    private static HistoryRow[] ParseWorkbook(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(bytes, false); using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        if (!reader.Read() || reader.FieldCount != Headers.Length) throw new SelfException("protocol");
        for (int i = 0; i < Headers.Length; i++) if (Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() != Headers[i]) throw new SelfException("protocol");
        var rows = new List<HistoryRow>(); bool footer = false;
        while (reader.Read())
        {
            string S(int i) => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (Enumerable.Range(0, Headers.Length).All(i => S(i).Length == 0)) continue;
            if (S(0) is "合计" or "总计" or "汇总") { if (footer) throw new SelfException("protocol"); footer = true; continue; }
            if (footer) throw new SelfException("protocol");
            string D(int i) { if (reader.GetValue(i) is DateTime date) return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); if (DateTime.TryParseExact(S(i), ["yyyy-MM-dd HH:mm:ss", "yyyy/M/d H:mm:ss", "yyyy-MM-dd HH:mm:ss.0"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); throw new SelfException("protocol"); }
            rows.Add(new(D(0), D(1), S(9), "", S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(10), S(11)));
            if (rows.Count > 10_000) throw new SelfException("too_many");
        }
        if (reader.NextResult()) throw new SelfException("protocol"); return rows.ToArray();
    }
    private static string Key(HistoryRow r)
    {
        string N(string v) => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("G29", CultureInfo.InvariantCulture) : v;
        return string.Join('\u001f', r.LoginTime, r.LogoutTime, r.Ip, N(r.Minutes), N(r.FlowMb), N(r.Cost), N(r.InternetUp), N(r.InternetDown), N(r.ChinaUp), N(r.ChinaDown), r.NasIp, r.NasPort);
    }
    public static bool Contains(HistoryRow[] all, IReadOnlyList<HistoryRow> subset)
    {
        var counts = all.GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
        foreach (var row in subset) { string key = Key(row); if (!counts.TryGetValue(key, out int n) || n == 0) return false; counts[key] = n - 1; }
        return true;
    }
    public static async Task<Page<HistoryRow>> LoadAsync(SelfClient client, DateOnly start, DateOnly end, CancellationToken token)
    {
        if (end < start || end.DayNumber - start.DayNumber > 3660) throw new SelfException("range");
        var all = new List<HistoryRow>(); var info = new List<string>();
        for (var from = start; from <= end;)
        {
            var to = DateOnly.FromDayNumber(Math.Min(end.DayNumber, from.DayNumber + 59));
            var before = await client.HistoryAsync(from, to, 1, token);
            var rows = Parse(await client.ExportHistoryAsync(from, to, token));
            // The deployed server can report a stale total (148) while returning 300 actual rows.
            // An independent full export, exact page sizes and an empty terminal page establish completeness.
            var detail = new List<HistoryRow>(before.Rows);
            if (before.Rows.Count != Math.Min(10, rows.Length)) throw new SelfException("history_mismatch");
            for (int page = 2; detail.Count < rows.Length; page++)
            {
                var next = await client.HistoryAsync(from, to, page, token);
                if (next.Total != before.Total || next.Rows.Count != Math.Min(10, rows.Length - detail.Count)) throw new SelfException("history_mismatch");
                detail.AddRange(next.Rows);
            }
            var terminal = await client.HistoryAsync(from, to, Math.Max(2, (rows.Length + 9) / 10 + 1), token);
            var after = await client.HistoryAsync(from, to, 1, token);
            if (before.Total != after.Total || terminal.Total != before.Total || terminal.Rows.Count != 0 || detail.Count != rows.Length || !Contains(rows, detail) || !Contains(rows, after.Rows)) throw new SelfException("history_mismatch");
            if (all.Count + rows.Length > 10_000) throw new SelfException("too_many"); all.AddRange(rows);
            info.Add($"{from:yyyy-MM-dd}～{to:yyyy-MM-dd}：官网全部导出与全部分页 {rows.Length} 条、12 个字段及重复次数一致，末页之后为空。" + (before.Total == rows.Length ? "" : $"官网汇总显示 {before.Total} 条，与实际明细不一致，以已核对的 {rows.Length} 条明细为准。"));
            if (to == end) break; from = to.AddDays(1);
        }
        return new(all.Count, all.OrderByDescending(r => r.LoginTime, StringComparer.Ordinal).ThenByDescending(r => r.LogoutTime, StringComparer.Ordinal).ToArray(), string.Join(Environment.NewLine, info));
    }
}
