// SPDX-License-Identifier: AGPL-3.0-or-later
// co-resident 0.5.3: one-time codes from chat in the agent browser - field classification (one-time code vs password,
// payment, bank, secret), the typing rule of browser_type, the pause decision of a challenge page and the setting.

using System.Text.Json;
using CO.Core.Browser;
using CO.Core.Tools;
using Xunit;

namespace CO.Core.Tests;

public class AgentOtpTests
{
    [Theory]
    [InlineData("text", "one-time-code", "", "", "", "", "")]
    [InlineData("text", "", "otp", "", "", "", "")]
    [InlineData("tel", "", "", "otp-input", "", "", "")]
    [InlineData("text", "", "verificationCode", "", "", "", "")]
    [InlineData("text", "", "", "", "Verification code", "", "")]
    [InlineData("text", "", "", "", "", "Enter the 6-digit code", "Two-factor code")]
    [InlineData("number", "", "totp", "", "", "", "")]
    [InlineData("text", "", "sms_code", "", "", "", "")]
    [InlineData("text", "", "", "", "", "", "Verificatiecode")]
    [InlineData("text", "", "", "", "", "", "Код подтверждения")]
    [InlineData("text", "one-time-code", "code", "", "", "", "")]
    public void Classify_OneTimeCodeFields(string type, string ac, string name, string id, string aria, string ph, string label) =>
        Assert.Equal(FieldKind.OneTimeCode, AgentOtpRules.Classify(new FieldAttrs(type, ac, name, id, aria, ph, label)));

    [Theory]
    [InlineData("password", "one-time-code", "otp", "", "", "", "")]       // password type wins over one-time code
    [InlineData("text", "current-password", "", "", "", "", "")]
    [InlineData("text", "cc-number", "", "", "", "", "")]
    [InlineData("text", "cc-csc", "otp", "", "", "", "")]
    [InlineData("text", "", "cardnumber", "", "", "", "")]
    [InlineData("text", "", "cvc", "", "", "", "")]
    [InlineData("text", "", "", "", "CVV", "", "")]
    [InlineData("text", "", "iban", "", "", "", "")]
    [InlineData("text", "", "", "", "", "", "Kaartnummer")]
    [InlineData("text", "one-time-code", "pin", "", "", "", "")]           // "SMS PIN": a PIN stays a secret
    [InlineData("text", "", "", "", "", "PIN", "")]
    [InlineData("text", "", "api_key", "", "", "", "")]
    [InlineData("text", "", "client_secret", "", "", "", "")]
    [InlineData("text", "", "token", "", "", "", "")]
    [InlineData("text", "", "", "", "", "", "Wachtwoord")]
    [InlineData("text", "", "", "", "", "", "Пароль")]
    [InlineData("text", "", "", "", "Security code", "", "")]
    [InlineData("text", "", "", "", "", "", "Rekeningnummer")]
    [InlineData("text", "", "pincode", "", "", "", "")]
    [InlineData("text", "", "", "", "", "", "Пин-код")]
    [InlineData("text", "", "", "", "", "", "Enter your one-time code or your password")]
    public void Classify_SecretFields(string type, string ac, string name, string id, string aria, string ph, string label) =>
        Assert.Equal(FieldKind.Secret, AgentOtpRules.Classify(new FieldAttrs(type, ac, name, id, aria, ph, label)));

    [Theory]
    [InlineData("email", "email", "email", "", "", "", "E-mail")]
    [InlineData("text", "username", "login", "", "", "", "Username")]
    [InlineData("text", "", "q", "", "Search", "", "")]
    [InlineData("text", "postal-code", "zip", "", "", "", "Postcode")]
    [InlineData("text", "", "spinner", "", "", "", "")]
    public void Classify_PlainFields(string type, string ac, string name, string id, string aria, string ph, string label) =>
        Assert.Equal(FieldKind.Plain, AgentOtpRules.Classify(new FieldAttrs(type, ac, name, id, aria, ph, label)));

    [Theory]
    [InlineData("123456", true)]
    [InlineData("123 456", true)]
    [InlineData("C69F-0EB6Q", true)]
    [InlineData("ab1", true)]
    [InlineData("12", false)]
    [InlineData("", false)]
    [InlineData("my password is hunter2", false)]
    [InlineData("P@ssw0rd!", false)]
    [InlineData("1234567890123", false)]
    public void LooksLikeCode(string text, bool expected) => Assert.Equal(expected, AgentOtpRules.LooksLikeCode(text));

    // Setting off: exactly the pre-0.5.3 decisions.
    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("", null, false, false)]
    [InlineData("captcha", null, false, true)]
    [InlineData("one-time code", "one-time code", false, false)]
    [InlineData("one-time code,captcha", "one-time code", false, false)]
    [InlineData("one-time code,payment", "one-time code", false, false)]
    [InlineData("payment", "payment", false, false)]
    [InlineData("payment,captcha", "payment", false, false)]
    public void Decide_Off_IsUnchanged(string? raw, string? pause, bool otp, bool captcha) =>
        Assert.Equal(new ChallengeDecision(pause, otp, captcha), AgentOtpRules.Decide(raw, false));

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("captcha", null, false, true)]
    [InlineData("one-time code", null, true, false)]
    [InlineData("one-time code,captcha", null, true, true)]
    [InlineData("one-time code,payment", "payment", false, false)]   // payment always pauses
    [InlineData("payment", "payment", false, false)]
    [InlineData("payment,captcha", "payment", false, false)]
    public void Decide_On_OtpDoesNotPause_PaymentDoes(string? raw, string? pause, bool otp, bool captcha) =>
        Assert.Equal(new ChallengeDecision(pause, otp, captcha), AgentOtpRules.Decide(raw, true));

    [Fact]
    public void Decide_On_CodeSoonAfterPurchase_IsPayment()
    {
        Assert.Equal(new ChallengeDecision("payment", false, false), AgentOtpRules.Decide("one-time code", true, afterPurchase: true));
        Assert.Equal(new ChallengeDecision(null, true, false), AgentOtpRules.Decide("one-time code", true, afterPurchase: false));
        // Setting off: unchanged, the code pauses as a one-time code page.
        Assert.Equal(new ChallengeDecision("one-time code", false, false), AgentOtpRules.Decide("one-time code", false, afterPurchase: true));
        Assert.Equal(new ChallengeDecision(null, false, true), AgentOtpRules.Decide("captcha", true, afterPurchase: true));
    }

    [Theory]
    [InlineData("Login code")]
    [InlineData("Sign-in code")]
    [InlineData("Confirmation code")]
    [InlineData("Two-factor code")]
    [InlineData("Bevestigingscode")]
    [InlineData("Verification code")]
    [InlineData("Код подтверждения")]
    public void Snapshot_MasksEveryOneTimeCodeWording(string accessibleName) => Assert.True(AgentPage.SensitiveName(accessibleName));

    [Theory]
    [InlineData("Postcode")]
    [InlineData("Promo code")]
    [InlineData("E-mail")]
    public void Snapshot_DoesNotMaskOrdinaryCodes(string accessibleName) => Assert.False(AgentPage.SensitiveName(accessibleName));

    [Theory]
    [InlineData(null, "member", true)]
    [InlineData(null, "owner", false)]
    [InlineData(null, "", false)]
    [InlineData(null, null, false)]
    [InlineData(false, "member", false)]
    [InlineData(true, "owner", true)]
    public void Effective_DefaultOnlyForMembers(bool? setting, string? role, bool expected) =>
        Assert.Equal(expected, AgentOtpRules.Effective(setting, role));

    [Fact]
    public void Hint_NamesTheRef()
    {
        Assert.Contains("one-time code needed", AgentOtpRules.Hint("n42"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("browser_type it into ref n42", AgentOtpRules.Hint("n42"));
        Assert.Contains("browser_snapshot", AgentOtpRules.Hint(null));
    }

    // Result of AgentPageScripts.Field as the page returns it.
    static JsonElement Field(bool sensitive, string reason, string type = "text", string ac = "", string name = "", string label = "") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sensitive,
            reason,
            attrs = new { type, autocomplete = ac, name, id = "", ariaLabel = "", placeholder = "", label },
        })).RootElement.Clone();

    static string Refused(JsonElement f, string text, bool allow) =>
        Assert.Throws<ToolException>(() => AgentPage.CheckTypingAllowed(f, text, allow)).Code;

    [Fact]
    public void Typing_Off_RefusesEverySensitiveField_AsBefore()
    {
        Assert.Equal("sensitive_field", Refused(Field(true, "one-time-code autocomplete", ac: "one-time-code"), "123456", false));
        Assert.Equal("sensitive_field", Refused(Field(true, "password field", type: "password"), "123456", false));
        // Not sensitive by the old rule: typed as before, even if the new classifier would see a secret word.
        AgentPage.CheckTypingAllowed(Field(false, "", name: "api_token"), "abc", false);
        AgentPage.CheckTypingAllowed(Field(false, "", name: "email"), "someone@example.org", false);
    }

    [Fact]
    public void Typing_On_OnlyCodesIntoOneTimeCodeFields()
    {
        var otp = Field(true, "one-time-code autocomplete", ac: "one-time-code");
        Assert.True(AgentPage.CheckTypingAllowed(otp, "123456", true));
        AgentPage.CheckTypingAllowed(otp, "123-456", true);
        Assert.Equal("sensitive_field", Refused(otp, "this is not a code", true));
        Assert.Equal("sensitive_field", Refused(Field(true, "password field", type: "password"), "123456", true));
        Assert.Equal("sensitive_field", Refused(Field(true, "payment autocomplete", ac: "cc-number"), "4111111111111111", true));
        Assert.Equal("sensitive_field", Refused(Field(true, "name matches sensitive pattern", name: "cvc"), "123", true));
        Assert.Equal("sensitive_field", Refused(Field(true, "name matches sensitive pattern", name: "iban"), "BE71096123456769", true));
        Assert.Equal("sensitive_field", Refused(Field(true, "name matches sensitive pattern", ac: "one-time-code", name: "pin"), "1234", true));
        // New secret words are refused while the setting is on.
        Assert.Equal("sensitive_field", Refused(Field(false, "", name: "client_secret"), "abc", true));
        Assert.False(AgentPage.CheckTypingAllowed(Field(false, "", name: "email"), "someone@example.org", true));
        // A code field the old rule did not know (placeholder "Login code") takes only a code while the setting is on.
        Assert.True(AgentPage.CheckTypingAllowed(Field(false, "", name: "code", label: "Login code"), "AB12CD9", true));
        Assert.Equal("sensitive_field", Refused(Field(false, "", name: "code", label: "Login code"), "not a code at all", true));
    }

    [Fact]
    public void Setting_MissingIsNull_RoundTrips()
    {
        Assert.Null(new AgentWebSettings().AllowOtpFromChat);
        var dir = Path.Combine(Path.GetTempPath(), "co-agentweb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "agent-browser.json");
        try
        {
            File.WriteAllText(file, "{\"Journal\":true,\"ConfirmForms\":false}");
            var st = new AgentWebSettingsStore(file);
            Assert.Null(st.Current.AllowOtpFromChat);
            var c = st.Current;
            c.AllowOtpFromChat = true;
            st.Save(c);
            Assert.True(new AgentWebSettingsStore(file).Current.AllowOtpFromChat);
            Assert.False(new AgentWebSettingsStore(file).Current.ConfirmForms);
            st.Save(new AgentWebSettings { AllowOtpFromChat = false });
            Assert.False(new AgentWebSettingsStore(file).Current.AllowOtpFromChat);
            st.Save(new AgentWebSettings());
            Assert.DoesNotContain("AllowOtpFromChat", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
