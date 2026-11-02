using System.Text.Json.Serialization;
using NufeCampus.Core;
namespace NufeCampus.Windows;

public sealed record Settings
{
    // Setters let source-generated JSON preserve initializers for fields absent in old files.
    [JsonConstructor]
    public Settings() { }
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public string KeepAliveStrategy { get; set; } = "agent";
    public string TaskName { get; set; } = "";
    public int CheckIntervalSeconds { get; set; } = 30;
    public int ForegroundQuerySeconds { get; set; } = 3;
    public int ForegroundProbeSeconds { get; set; } = 120;
    public bool ScheduledStopped { get; set; }
    public bool HttpConsent { get; set; }
    public string Carrier { get; set; } = "@cmcc";
    public PolicyMemory Memory { get; set; } = new();
    public string CredentialIssue { get; set; } = "";
    public string LastNotifiedIssue { get; set; } = "";
    public string ActiveProfileId { get; set; } = "";
    public bool SelfConsent { get; set; }
    public bool SelfLoginBlocked { get; set; }
    public bool AutoOfflineEnabled { get; set; }
    public string[] ProtectedMacs { get; set; } = [];
    public string OfflineOrder { get; set; } = "oldest";
    public DateTimeOffset? LastAutomaticOffline { get; set; }
}
public sealed record SecretPair(string Account, string Password);
public sealed record PreferencesBackup(int Version, bool Enabled, bool AutoStart, string Carrier,
    string KeepAliveStrategy = "agent", int CheckIntervalSeconds = 30,
    int ForegroundQuerySeconds = 3, int ForegroundProbeSeconds = 120);
public sealed record AgentRequest(string Command);
public sealed record AgentStatus
{
    // Older agents omit newer fields; retain the defaults when deserializing their responses.
    [JsonConstructor]
    public AgentStatus() { }
    public int ProtocolVersion { get; set; }
    public int ProcessId { get; set; }
    public string Phase { get; set; } = "starting";
    public string Issue { get; set; } = "";
    public bool IsNufe { get; set; }
    public bool Paused { get; set; }
    public bool HasCredentials { get; set; }
    public bool KeepAliveEnabled { get; set; }
    public string Internet { get; set; } = "unknown";
    public DateTimeOffset? LastRecovery { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string CurrentAccount { get; set; } = "";
    public string Authentication { get; set; } = "unknown";
    public string ServerMessage { get; set; } = "";
    public DateTimeOffset? FailureAt { get; set; }
}
public sealed record AgentResponse(bool Ok, AgentStatus Status, string Code = "ok");
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(SecretPair))]
[JsonSerializable(typeof(Dictionary<string, SecretPair>))]
[JsonSerializable(typeof(PreferencesBackup))]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(AgentStatus))]
[JsonSerializable(typeof(AccountVault))]
public partial class AppJson : JsonSerializerContext { }

public static class Texts
{
    public static string Phase(string code) => code switch
    {
        "online" => "校园网已认证",
        "off_campus" => "当前不在 NUFE-STU，保持静默",
        "paused" => "已暂停保活",
        "disabled" => "保活已关闭",
        "no_credentials" => "请先保存校园网凭据",
        "consent_required" => "请先确认校方 HTTP 传输说明",
        "authenticating" => "正在认证",
        "waiting" => "等待任务计划检查，当前状态尚未确认",
        "offline" => "认证离线，等待恢复",
        "unreachable" => "认证服务器暂不可达",
        "wifi_permission" => "无法读取 Wi-Fi 状态，请检查 Windows 位置权限",
        "fault" => "需要处理",
        _ => "正在检查网络"
    };
    public static string Issue(string code) => code switch
    {
        "campus_credentials" => "校园网账号或密码被拒绝，自动重试已停止。修改凭据后保存，或修复后手动重试。",
        "carrier_credentials" => "运营商宽带密码被拒绝，自动重试已停止。请在自助平台修复绑定后手动重试。",
        "quota" => "在线设备数量达到限制，请在自助平台处理设备后手动重试。",
        "carrier_state" => "运营商账号状态异常，请核对欠费或停机情况后手动重试。",
        "campus_missing" => "校园账号不存在，请检查学号；自动提交已停止。",
        "carrier_account" => "运营商账号被拒绝，请检查宽带绑定；自动提交已停止。",
        "carrier_quota" => "运营商账号登录数量达到限制，校内设备下线不一定能解决；请人工核对。",
        "business_rejected" => "服务器拒绝了本次认证，原因尚未识别；自动提交已停止，请展开服务器提示。",
        "auto_offline_suspended" => "自动处理超限已停止，请核对设备和自助状态后手动恢复规则。",
        "profile_transition" => "账号配置切换尚未完成，未发送认证或设备操作。请重新载入并保存所选账号。",
        "credentials_unreadable" => "本机加密凭据无法解密，请重新输入并保存；不会回退到明文。",
        "different_account" => "本机当前由其他账号认证，请自行核对；工具不会自动下线或切换账号。",
        "external_probe" => "校园网认证在线，但外网探测未通过。请检查 VPN、DNS 或外网情况；不会自动下线重登。",
        "unknown_response" => "服务器响应未被识别，稍后按退避规则检查。",
        "network" => "网络或服务器暂时不可用。",
        "" => "",
        _ => "操作未完成，请查看脱敏诊断。"
    };
}

public sealed record AccountProfile(string Id, SecretPair Campus, string Carrier, Settings Preferences, SecretPair? Broadband = null);
public sealed record AccountVault(int Version, AccountProfile[] Profiles);
public sealed record AccountChoice(string Id, string Account, string Carrier)
{
    public override string ToString() => Account + (Carrier == "@cmcc" ? " · 移动" : " · 电信");
}
