using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace NufeCampus.App;
// In-process, synthetic-only transport for --test-root. Never selected in the normal profile.
internal sealed class SelfDemoHandler : HttpMessageHandler
{
    private bool logged, online = true, ownOnline = true, macBound = true, stopped, stopScheduled, reopenScheduled;
    private string account = "14000001";
    private string[] bindings = ["13800000000", "excluded-demo-value", "", "", "", ""];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string path = request.RequestUri!.AbsolutePath; string body;
        if (path == "/Self/login/") { logged = false; body = "<form method='post' action='/Self/login/verify'><input name='account'><input name='password'><input name='code'><input name='foo' value='demo'></form><div id='randomDiv' class='hide'></div>"; }
        else if (path == "/Self/login/randomCode") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aMskAAAAASUVORK5CYII=")) };
        else if (path == "/Self/login/verify") { var fields = Fields(await request.Content!.ReadAsStringAsync(token)); account = fields["account"]; logged = true; return new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("/Self/dashboard/index", UriKind.Relative) } }; }
        else if (path == "/Self/login/logout") { logged = false; return new(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("/Self/login/", UriKind.Relative) } }; }
        else if (!logged) return new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent(""), Headers = { Location = new Uri("/Self/login/", UriKind.Relative) } };
        else if (path == "/Self/dashboard/getOnlineList")
        {
            var rows = new List<object>(); if (ownOnline) rows.Add(new { sessionId = "demo-own", mac = "001122334455", ip = "192.0.2.1", loginTime = "2026-10-31 10:00:00", useTime = 240, upFlow = 1024, downFlow = 2048, hostName = "示例本机", terminalType = "Windows" });
            if (online) rows.Add(new { sessionId = "demo-phone", mac = "AABBCCDDEEFF", ip = "192.0.2.2", loginTime = "2026-10-31 10:00:00", useTime = 120, upFlow = 512, downFlow = 512, hostName = "示例手机", terminalType = "Android" }); body = JsonSerializer.Serialize(rows);
        }
        else if (path == "/Self/dashboard/tooffline") { if (Fields(request.RequestUri.Query.TrimStart('?'))["sessionid"] == "demo-own") ownOnline = false; else online = false; body = "{}"; }
        else if (path == "/Self/bill") body = "<a href='/Self/bill/userOnlineLog'>上网记录</a><a href='/Self/bill/monthPay'>历史账单</a><a href='/Self/bill/operatorLog'>业务办理记录</a>";
        else if (path == "/Self/service") body = "<a href='/Self/service/goStop'>账号报停</a><a href='/Self/service/goReopen'>账号复通</a><a href='/Self/service/myMac'>我的设备</a><a href='/Self/service/operatorId'>绑定运营商账号</a><a href='/Self/unlogin/help'>使用帮助</a><a href='/Self/unlogin/agreement'>服务协议</a>";
        else if (path == "/Self/dashboard") body = "<nav><a href='/Self/dashboard'>账号概况</a><a href='/Self/service/myMac'>绑定设备</a><a href='/Self/service/operatorId'>运营商管理</a></nav><script>var user=" + JsonSerializer.Serialize(new { userName = account, useFlag = 1, serviceDefault = "示例校园套餐", useTime = 60, useFlow = 100, leftMoney = 20, userPassword = "excluded-demo-value" }) + ";</script>";
        else if (path == "/Self/bill/getUserOnlineLog")
        {
            int page = int.Parse(Fields(request.RequestUri.Query.TrimStart('?'))["pageNumber"]);
            body = JsonSerializer.Serialize(new { total = 25, summary = new { TIME = 50, FLOW = 75, COSTMONEY = 0, INTERNETUPFLOW = 1, INTERNETDOWNFLOW = 2, CHINANETUPFLOW = 3, CHINANETDOWNFLOW = 4 }, rows = Enumerable.Range((page - 1) * 10, Math.Max(0, Math.Min(10, 25 - (page - 1) * 10))).Select(i => new { loginTime = $"2026-10-{i + 1:00} 10:00:00", logoutTime = $"2026-10-{i + 1:00} 10:02:00", userIp = "192.0.2.2", time = 2, flow = 3, costMoney = 0, internetUpFlow = 1, internetDownFlow = 2, chinanetUpFlow = 3, chinanetDownFlow = 4, nasIp = "192.0.2.254", nasPort = 3200 + i }) });
        }
        else if (path == "/Self/bill/getOperatorLog") body = "{\"total\":1,\"rows\":[[\"2026-10-30 10:00:00\",\"示例修改资料\",\"9999\",\"未展示内部字段\",\"合成记录\"]]}";
        else if (path is "/Self/service/getStopLog" or "/Self/service/goReopenLog") body = "{\"total\":1,\"rows\":[{\"fldoperatedate\":\"2026-10-30 10:00:00\",\"fldoperateid\":\"8\",\"fldadminid\":\"9999\",\"fldmemo\":\"合成示例\",\"fldnewvalue\":\"2026-11-01\"}]}";
        else if (path is "/Self/service/goStop" or "/Self/service/goReopen")
        {
            bool reopen = path.EndsWith("goReopen"); string disabled = (reopen ? !stopped : stopped) ? " disabled" : "";
            body = $"<form>账号状态：{(stopped ? "停机" : "正常")}\n套餐：合成套餐\n预约：{(reopen ? reopenScheduled : stopScheduled)}<button id='{(reopen ? "reOpenNow" : "stopNow")}'{disabled}>立即办理</button><button id='{(reopen ? "reOpen" : "stop")}'{disabled}>预约办理</button></form>";
            if (reopen ? reopenScheduled : stopScheduled) body += $"<button data-toggle='{(reopen ? "undoPreReopen" : "undoPreStop")}'>取消预约</button>";
            body += $"<script>var AJAXCSRFTOKEN='synthetic-token'; function {(reopen ? "userOpen" : "disable")}(flag) {{ }}</script>";
        }
        else if (path is "/Self/service/stop" or "/Self/service/reOpen" or "/Self/service/undoStop" or "/Self/service/undoReOpen")
        {
            var fields = Fields(await request.Content!.ReadAsStringAsync(token));
            if (path.EndsWith("undoStop")) stopScheduled = false;
            else if (path.EndsWith("undoReOpen")) reopenScheduled = false;
            else if (fields["flag"] == "2") { if (path.EndsWith("stop")) stopScheduled = true; else reopenScheduled = true; }
            else stopped = path.EndsWith("stop"); body = "{\"state\":\"success\"}";
        }
        else if (path == "/Self/unlogin/help") body = "<div class='container'><a href='/Self/unlogin/helpinfo/0'>暂无使用帮助信息</a></div>";
        else if (path == "/Self/unlogin/helpinfo/0") body = "<div class='container'>暂无使用帮助信息</div>";
        else if (path == "/Self/unlogin/agreement") body = "<div class='container'><h2>合成服务协议示例（仅用于界面验证）</h2><p>这里展示读取的正文，不表示接受或签署。</p><script>excluded-script-text</script></div>";
        else if (path == "/Self/bill/getMonthPay") body = "{\"total\":1,\"summary\":{\"USETIME\":2,\"USEFLOW\":3,\"USEBASEMONEY\":10,\"USEDMONEY\":0},\"rows\":[[\"2026-10-01\",\"2026-10-31\",\"示例校园套餐\",10,0,2,3,\"2026-10-31\"]]}";
        else if (path == "/Self/service/getMacList") body = macBound ? "{\"total\":1,\"rows\":[[0,\"AABBCCDDEEFF\",\"示例手机\",\"2026-10-30 10:00:00\",\"192.0.2.2\"]]}" : "{\"total\":0,\"rows\":[]}";
        else if (path == "/Self/service/unbindmac") { macBound = false; body = "{}"; }
        else if (path == "/Self/service/bind-operator") { var fields = Fields(await request.Content!.ReadAsStringAsync(token)); bindings = Enumerable.Range(1, 6).Select(n => fields["FLDEXTRA" + n]).ToArray(); body = "{}"; }
        else if (path == "/Self/service/operatorId") { body = "<form method='post' action='/Self/service/bind-operator'><input name='csrftoken' value='demo-csrf'>"; for (int i = 0; i < 6; i++) body += $"<input name='FLDEXTRA{i + 1}' value='{WebUtility.HtmlEncode(bindings[i])}'>"; body += "</form>"; }
        else throw new InvalidOperationException("Unexpected demo route");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, path.Contains("get") ? "application/json" : "text/html") };
    }
    private static Dictionary<string, string> Fields(string value) => value.Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
}
