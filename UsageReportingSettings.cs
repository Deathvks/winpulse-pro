using System;
using System.Linq;
using System.Net.Mail;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WinPulseOptimizer;

internal sealed record UsageReportingSettings(Uri Endpoint, string Controller, string Contact, Uri Policy)
{
    internal static readonly UsageReportingSettings? Current = Load();

    internal string ConsentScope => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"1\n{Endpoint.AbsoluteUri}\n{Controller}\n{Contact}\n{Policy.AbsoluteUri}")));

    private static UsageReportingSettings? Load()
    {
        var values = typeof(UsageReportingSettings).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        string? Read(string key) => values.FirstOrDefault(value => value.Key == key)?.Value?.Trim();
        string? controller = Read("WinPulseUsageController");
        string? contact = Read("WinPulseUsageContact");
        if (string.IsNullOrWhiteSpace(controller) || !MailAddress.TryCreate(contact, out var email) || email.Address != contact ||
            !TryHttps(Read("WinPulseUsageEndpoint"), out var endpoint) || endpoint.AbsolutePath != "/v1/usage" ||
            !TryHttps(Read("WinPulseUsagePolicy"), out var policy)) return null;
        return new(endpoint, controller, contact, policy);
    }

    private static bool TryHttps(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var valid) && valid.Scheme == Uri.UriSchemeHttps &&
            valid.HostNameType == UriHostNameType.Dns && !valid.IsLoopback && valid.UserInfo.Length == 0 &&
            valid.Query.Length == 0 && valid.Fragment.Length == 0)
        {
            uri = valid;
            return true;
        }
        uri = null!;
        return false;
    }
}
