using NufeCampus.Windows;
using System.Net;
using System.Security.Cryptography;
using System.Text;
namespace NufeCampus.SelfService;

public sealed partial class SelfClient
{
    private static string OperatorInput(SecretPair[] rows)
    {
        if (rows.Length != 3 || rows.Any(r => r.Account.Length > 64 || r.Password.Length > 256 || r.Account.Any(char.IsControl) || r.Password.Any(char.IsControl) || (r.Account.Length == 0) != (r.Password.Length == 0))) throw new SelfException("broadband");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', rows.SelectMany(r => new[] { r.Account, r.Password })))));
    }
    public Task<OperatorFormTicket> PrepareOperatorFormAsync(SecretPair[] rows, CancellationToken token) => Locked(async () =>
    {
        string fingerprint = OperatorInput(rows); var form = await OperatorForm(token);
        var ticket = new OperatorFormTicket(form.State, fingerprint, clock().AddSeconds(45)); tickets.Clear(); tickets.Add(ticket); return ticket;
    }, token);
    public Task<bool> SubmitOperatorFormAsync(OperatorFormTicket ticket, SecretPair[] rows, CancellationToken token) => Locked(async () =>
    {
        Consume(ticket, ticket.Expires); if (OperatorInput(rows) != ticket.InputFingerprint) throw new SelfException("changed");
        var form = await OperatorForm(token); if (form.State.Fingerprint != ticket.Before.Fingerprint) throw new SelfException("changed");
        var fields = new Dictionary<string, string> { { "csrftoken", form.Token } };
        for (int i = 0; i < 3; i++) { fields["FLDEXTRA" + (2 * i + 1)] = rows[i].Account; fields["FLDEXTRA" + (2 * i + 2)] = rows[i].Password; }
        using var request = new HttpRequestMessage(HttpMethod.Post, form.Action) { Content = new FormUrlEncodedContent(fields) }; fields.Clear(); request.Headers.Referrer = new(Base, "service/operatorId"); request.Headers.Add("Origin", Base.GetLeftPart(UriPartial.Authority));
        Guard(); using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (302 or 303)) throw new SelfException("uncertain");
        var after = await OperatorForm(token);
        for (int i = 0; i < 3; i++) if (after.State.Groups[i].Account != rows[i].Account || after.State.Groups[i].HasPassword != (rows[i].Account.Length > 0)) throw new SelfException("uncertain");
        return true;
    }, token);
}
