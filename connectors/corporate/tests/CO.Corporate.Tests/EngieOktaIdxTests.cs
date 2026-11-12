// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CO.Corporate.Tests;

// A small Okta Identity Engine: the /idp/idx/* steps EngieOktaIdx drives, enough to reach success with a number push.
public sealed class FakeOktaIdx : HttpMessageHandler
{
    public const string Base = "https://engie.okta-emea.com";
    public readonly CookieContainer Jar = new();
    public readonly List<string> Calls = new();
    public bool OfferFactorChoice = true;   // whether to require select-authenticator before the push
    public bool RejectPush;                  // push comes back REJECTED
    public int Polls;
    public string? SentPassword;

    private static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    private static async Task<string> Body(HttpRequestMessage req, CancellationToken ct) =>
        req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var u = req.RequestUri!;
        Calls.Add(req.Method + " " + u.AbsolutePath);

        if (u.AbsolutePath == "/oauth2/default/v1/authorize")
            return new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = req, Content = new StringContent("<html><script>var stateToken = 'ST-123';</script></html>") };

        if (u.AbsolutePath == "/idp/idx/introspect")
            // FastPass loopback probe present -> EngieOktaIdx must cancel it first.
            return Json(new
            {
                stateHandle = "sh",
                authenticatorChallenge = new { value = new { cancel = new { href = Base + "/idp/idx/cancel", value = new[] { new { name = "stateHandle", value = "sh" } } } } },
                remediation = new { value = new[] { new { name = "identify", href = Base + "/idp/idx/identify", value = new[] { new { name = "stateHandle", value = "sh" } } } } },
            });

        if (u.AbsolutePath == "/idp/idx/cancel")
            return Json(new
            {
                stateHandle = "sh",
                remediation = new { value = new[] { new { name = "identify", href = Base + "/idp/idx/identify", value = new[] { new { name = "stateHandle", value = "sh" } } } } },
            });

        if (u.AbsolutePath == "/idp/idx/identify")
            return Json(new
            {
                stateHandle = "sh",
                remediation = new { value = new[] { new { name = "challenge-authenticator", href = Base + "/idp/idx/challenge/answer", value = new[] { new { name = "stateHandle", value = "sh" } } } } },
            });

        if (u.AbsolutePath == "/idp/idx/challenge/answer")
        {
            string b = await Body(req, ct);
            using var d = JsonDocument.Parse(b);
            if (d.RootElement.TryGetProperty("credentials", out var cr) && cr.TryGetProperty("passcode", out var pc))
                SentPassword = pc.GetString();

            if (OfferFactorChoice)
                return Json(new
                {
                    stateHandle = "sh",
                    remediation = new { value = new[] { new {
                        name = "select-authenticator-authenticate", href = Base + "/idp/idx/challenge", value = new object[] {
                            new { name = "stateHandle", value = "sh" },
                            new { name = "authenticator", options = new[] { new {
                                label = "Okta Verify",
                                value = new { form = new { value = new[] { new { name = "id", value = "aut123" } } } }
                            } } }
                        } } } },
                });
            return PushChallenge();
        }

        if (u.AbsolutePath == "/idp/idx/challenge")   // selected the push authenticator
            return PushChallenge();

        if (u.AbsolutePath == "/idp/idx/challenge/poll")
        {
            Polls++;
            if (RejectPush)
                return Json(new { messages = new { value = new[] { new { message = "You have chosen to reject this login." } } } });
            if (Polls < 2)
                return Json(new
                {
                    currentAuthenticator = new { value = new { contextualData = new { correctAnswer = "42" } } },
                    remediation = new { value = new[] { new { name = "challenge-poll", href = Base + "/idp/idx/challenge/poll", refresh = 100, value = new[] { new { name = "stateHandle", value = "sh" } } } } },
                });
            return Json(new { success = new { href = Base + "/login/token/redirect" } });
        }

        if (u.AbsolutePath == "/login/token/redirect")
        {
            // Okta sets the org session cookie here; the method reads it from the jar afterwards.
            Jar.Add(new Cookie("sid", "SESSION-OK", "/", "engie.okta-emea.com") { Secure = true });
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = req, Content = new StringContent("<html>ok</html>") };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req, Content = new StringContent("{}") };

        HttpResponseMessage PushChallenge() => Json(new
        {
            stateHandle = "sh",
            currentAuthenticator = new { value = new { contextualData = new { correctAnswer = "42" } } },
            remediation = new { value = new[] { new { name = "challenge-poll", href = Base + "/idp/idx/challenge/poll", refresh = 100, value = new[] { new { name = "stateHandle", value = "sh" } } } } },
        });
    }
}

public sealed class EngieOktaIdxTests
{
    private static Task<EngieOktaIdx.Result> Run(FakeOktaIdx fake, List<string> numbers)
    {
        return EngieOktaIdx.SignInAsync(
            FakeOktaIdx.Base, "r@example.com", "secret-pw",
            c => numbers.Add(c.Number), _ => { }, CancellationToken.None, fake, fake.Jar);
    }

    [Fact]
    public async Task CompletesWithPushNumberAndCarriesSessionCookie()
    {
        var fake = new FakeOktaIdx();
        var numbers = new List<string>();
        var res = await Run(fake, numbers);

        Assert.True(res.Ok, res.Error);
        Assert.Equal("secret-pw", fake.SentPassword);        // password reached Okta, in-process only
        Assert.Contains("42", numbers);                       // the number was surfaced to the user
        Assert.Contains(res.OktaCookies, c => c.Name == "sid" && c.Value == "SESSION-OK");
        Assert.Contains("POST /idp/idx/cancel", fake.Calls);  // the FastPass probe was declined
    }

    [Fact]
    public async Task WorksWhenPushIsTheOnlyFactor()
    {
        var fake = new FakeOktaIdx { OfferFactorChoice = false };
        var numbers = new List<string>();
        var res = await Run(fake, numbers);

        Assert.True(res.Ok, res.Error);
        Assert.Contains("42", numbers);
    }

    [Fact]
    public async Task ReportsRejectedPush()
    {
        var fake = new FakeOktaIdx { RejectPush = true };
        var res = await Run(fake, new List<string>());

        Assert.False(res.Ok);
        Assert.Contains("reject", res.Error, StringComparison.OrdinalIgnoreCase);
    }
}
