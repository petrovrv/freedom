// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace CO.Core.Browser;

/// <summary>Kind of an input field of the agent tab for typing (CO Resident 0.5.3).</summary>
public enum FieldKind
{
    /// <summary>An ordinary field: the agent types.</summary>
    Plain,
    /// <summary>A one-time code field only (login/verification code from mail or SMS): typed only with AllowOtpFromChat.</summary>
    OneTimeCode,
    /// <summary>A password, payment, bank or other secret field: the agent never types there.</summary>
    Secret,
}

/// <summary>Attributes of an input as read by AgentPageScripts.Field (all lower or mixed case, may be empty).</summary>
public sealed record FieldAttrs(string Type = "", string Autocomplete = "", string Name = "", string Id = "",
    string AriaLabel = "", string Placeholder = "", string Label = "");

/// <summary>What the challenge check of a page decides (see <see cref="AgentOtpRules.Decide"/>).</summary>
/// <param name="Pause">Reason to pause the tab ("one-time code" / "payment"), null = no pause.</param>
/// <param name="Otp">The tab is marked otp-pending (a one-time code field the agent may fill from chat).</param>
/// <param name="Captcha">The tab is marked captcha-pending.</param>
public sealed record ChallengeDecision(string? Pause, bool Otp, bool Captcha);

/// <summary>
/// One-time codes from chat (owner's decision 13.11.2026, CO Resident 0.5.3): when the device setting
/// AllowOtpFromChat is on, a one-time-code page does not pause the agent tab and browser_type may type the code the
/// user sent in chat into a field that is protected ONLY because it takes a one-time code. Passwords, card, CVC, IBAN,
/// PIN and other secret fields stay refused; payment pages still pause; captcha rules are unchanged.
/// </summary>
public static class AgentOtpRules
{
    // Secret indicators (the agent never types): the pre-0.5.3 sensitive pattern without its one-time-code parts,
    // plus secret/token/api key words. Checked first: a field that is both (e.g. "SMS PIN") counts as a secret.
    private static readonly Regex SecretRe = new(
        @"card.?(?:num|no\b|nr\b|holder)|(?:credit|debit).?card|kaartnummer|cvc|cvv|csc|security.?code|iban|password|passcode|wachtwoord|пароль|\bpin\b"
        + @"|pin.?code|secret|api.?key|access.?token|\btoken\b|rekeningnummer|account.?n(?:umbe)?r|beveiligingscode|номер.?карты|\bпин\b|пин.?код",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // One-time code indicators (the pre-0.5.3 ones: one.?time, otp, verification.?code; plus common wordings).
    private static readonly Regex OtpRe = new(
        @"one.?time|otp|verification.?code|verify.?code|2fa|two.?factor|totp|mfa|sms.?code|auth(?:entication)?.?code|login.?code|sign.?in.?code"
        + @"|confirmation.?code|verificatiecode|bevestigingscode|code.?de.?v[ée]rification|best[äa]tigungscode|код.?подтверждения|код.?из.?смс|одноразов",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Classifies a field for browser_type.</summary>
    public static FieldKind Classify(FieldAttrs f)
    {
        var type = (f.Type ?? "").Trim().ToLowerInvariant();
        var acTokens = (f.Autocomplete ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (type == "password") return FieldKind.Secret;
        if (acTokens.Any(t => t.StartsWith("cc-", StringComparison.Ordinal) || t is "current-password" or "new-password"))
            return FieldKind.Secret;
        var parts = new[] { f.Name, f.Id, f.Autocomplete, f.AriaLabel, f.Placeholder, f.Label };
        if (parts.Any(p => !string.IsNullOrEmpty(p) && SecretRe.IsMatch(p))) return FieldKind.Secret;
        if (acTokens.Contains("one-time-code")) return FieldKind.OneTimeCode;
        if (parts.Any(p => !string.IsNullOrEmpty(p) && OtpRe.IsMatch(p))) return FieldKind.OneTimeCode;
        return FieldKind.Plain;
    }

    /// <summary>
    /// A typed one-time code must look like one: 3 to 12 letters or digits, spaces and dashes allowed in between.
    /// Keeps the agent from typing anything else (a password the user pasted, a sentence) into a code field.
    /// </summary>
    public static bool LooksLikeCode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 24) return false;
        var compact = text.Trim().Replace(" ", "").Replace("-", "");
        return compact.Length is >= 3 and <= 12 && compact.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z');
    }

    /// <summary>
    /// Decision for the raw result of AgentPageScripts.Challenge: a comma-separated set of "one-time code", "payment",
    /// "captcha" (or null/empty). Setting off: exactly the pre-0.5.3 behaviour (one-time code pauses before payment,
    /// either pause clears the captcha mark). Setting on: payment still pauses; a one-time code alone marks the tab
    /// otp-pending; a captcha marks it captcha-pending as before. A one-time code shortly after a purchase or payment
    /// click in the tab (<paramref name="afterPurchase"/>; a bank's 3-D Secure page) counts as payment and pauses;
    /// the page script also reports "payment" for a code page whose text looks like a card or bank confirmation.
    /// </summary>
    public static ChallengeDecision Decide(string? raw, bool allowOtpFromChat, bool afterPurchase = false)
    {
        bool otp = Has(raw, "one-time code"), pay = Has(raw, "payment"), cap = Has(raw, "captcha");
        if (!allowOtpFromChat)
        {
            if (otp) return new ChallengeDecision("one-time code", false, false);
            if (pay) return new ChallengeDecision("payment", false, false);
            return new ChallengeDecision(null, false, cap);
        }
        if (pay || (otp && afterPurchase)) return new ChallengeDecision("payment", false, false);
        return new ChallengeDecision(null, otp, cap);
    }

    /// <summary>How long after a purchase click a one-time code page still counts as payment.</summary>
    public static readonly TimeSpan PurchaseWindow = TimeSpan.FromMinutes(30);

    // Accessible names of one-time code fields for the snapshot mask (AgentPage.SensitiveName adds these).
    public static bool OtpWords(string? s) => !string.IsNullOrEmpty(s) && OtpRe.IsMatch(s);

    /// <summary>Whether the raw Challenge result contains <paramref name="kind"/>.</summary>
    public static bool Has(string? raw, string kind) =>
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(kind, StringComparer.Ordinal);

    /// <summary>
    /// Device default: an explicit setting wins; without one the setting is on for a household member's device
    /// (device role "member", owner's yes of 13.11.2026) and off for the owner's own devices and unknown roles.
    /// </summary>
    public static bool Effective(bool? setting, string? deviceRole) =>
        setting ?? string.Equals(deviceRole, "member", StringComparison.Ordinal);

    /// <summary>Text for the agent when a page waits for a one-time code it may type.</summary>
    public static string Hint(string? reference) =>
        "challenge: one-time code\nOne-time code needed: ask the user in chat to send the code they received (mail or SMS), then " +
        (reference is { Length: > 0 } r ? "browser_type it into ref " + r : "browser_type it into the code field (take its ref from browser_snapshot)") +
        ". Never ask for or type passwords, card or bank data.";
}
