// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace CO.Core.Browser;

// 0.9.9: agent browser settings of the chat (environment defaults < chat override, resolved by the server). Bridge
// sends them as the unsigned "browser" object of a device tool call: {"tabs":0,"idleMin":120,"formIdleMin":360,
// "captchaKeep":true}. Every value is clamped to the contract ranges; a missing or broken value takes the default.
public sealed record AgentTabRules(int Tabs, int IdleMin, int FormIdleMin, bool CaptchaKeep)
{
    // tabs 0 = unlimited; a tab closes after IdleMin without actions, FormIdleMin when it holds typed data, never while
    // it shows an unsolved captcha and CaptchaKeep is on.
    public static readonly AgentTabRules Default = new(0, 120, 360, true);

    public TimeSpan Idle => TimeSpan.FromMinutes(IdleMin);
    public TimeSpan FormIdle => TimeSpan.FromMinutes(FormIdleMin);

    public static AgentTabRules Parse(JsonElement? browser)
    {
        if (browser is not { ValueKind: JsonValueKind.Object } b) return Default;
        int tabs = Math.Clamp(Int(b, "tabs") ?? Default.Tabs, 0, 20);
        int idle = Math.Clamp(Int(b, "idleMin") ?? Default.IdleMin, 5, 1440);
        int form = Math.Clamp(Int(b, "formIdleMin") ?? Default.FormIdleMin, idle, 2880);
        return new AgentTabRules(tabs, idle, form, Bool(b, "captchaKeep") ?? Default.CaptchaKeep);
    }

    private static int? Int(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        double d;
        if (v.ValueKind == JsonValueKind.Number) d = v.GetDouble();
        else if (v.ValueKind != JsonValueKind.String
                 || !double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
        if (double.IsNaN(d) || double.IsInfinity(d)) return null;
        return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
    }

    private static bool? Bool(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "yes" => true,
                "0" or "false" or "no" => false,
                _ => null,
            },
            _ => null,
        };
    }
}
