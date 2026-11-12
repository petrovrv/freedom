// SPDX-License-Identifier: AGPL-3.0-or-later
using CO.Core.Host;
using CO.Core.Tools;
using Xunit;

namespace CO.Corporate.Tests;

public class EngiePromptSanitizerTests
{
    const string S = ":" + "//";
    static string C(params int[] cps) => string.Concat(cps.Select(char.ConvertFromUtf32));
    static string E(string hex) => "\\" + "u" + hex;
    // fake engie.ai page: a fresh Okta token for the token probe
    static string Token(string js) => js.Contains("secondsLeft") ? "{\"has\":true,\"secondsLeft\":3600}" : "null";

    [Theory]
    [InlineData("http" + S + "127.0.0.1:8765/api/x", "localhost port 8765 path /api/x")]
    [InlineData("https" + S + "localhost/", "localhost")]
    [InlineData("http" + S + "[::1]:9000", "localhost port 9000")]
    [InlineData("HTTP" + S + "LOCALHOST:80/", "localhost port 80")]
    public void RewritesLoopbackUrls(string input, string expected)
    {
        var r = EngiePromptSanitizer.Sanitize("see " + input + " now");
        Assert.Equal("see " + expected + " now", r.Text);
        Assert.Equal(1, r.Urls);
    }

    [Fact]
    public void LeavesNonLoopbackUnchanged()
    {
        var t = "localhost:8765 and https" + S + "example.com:8080/a";
        var r = EngiePromptSanitizer.Sanitize(t);
        Assert.Equal(t, r.Text);
        Assert.Equal(0, r.Urls);
    }

    [Fact]
    public void ReturnsSameInstanceForPlainAscii()
    {
        var t = "plain ascii";
        Assert.Same(t, EngiePromptSanitizer.Sanitize(t).Text);
    }

    [Fact]
    public void NullReturnsEmpty() => Assert.Equal("", EngiePromptSanitizer.Sanitize(null).Text);

    [Fact]
    public void PreservesCommonUnicode()
    {
        var t = C(1055, 1088, 1080, 945, 20013, 233, 8212, 171, 187, 8230, 8364, 8470);
        var r = EngiePromptSanitizer.Sanitize(t);
        Assert.Equal(t, r.Text);
        Assert.Equal(0, r.Chars);
    }

    [Fact]
    public void EscapesSymbolChars()
    {
        var r = EngiePromptSanitizer.Sanitize(C(9282, 8618, 10551));
        Assert.Equal(E("2442") + E("21AA") + E("2937"), r.Text);
        Assert.Equal(3, r.Chars);
    }

    [Fact]
    public void EscapesSurrogatePairAsOneChar()
    {
        var r = EngiePromptSanitizer.Sanitize(C(128640));
        Assert.Equal(E("D83D") + E("DE80"), r.Text);
        Assert.Equal(1, r.Chars);
    }

    [Fact]
    public void EscapesZeroWidthJoiner() => Assert.Equal(E("200D"), EngiePromptSanitizer.Sanitize(C(8205)).Text);

    [Fact]
    public void StrictEscapesDashAndNbspButKeepsCyrillic()
    {
        var r = EngiePromptSanitizer.Sanitize("a" + C(8212, 160, 1055), strict: true);
        Assert.Equal("a" + E("2014") + E("00A0") + C(1055), r.Text);
        Assert.Equal(2, r.Chars);
    }

    // engie_chat sends the cleaned prompt, retries once (strict) after fetch_failed and notes it in the reply.
    [Fact]
    public async Task EngieChatCleansPromptRetriesOnceAndNotes()
    {
        var b = new FakeBrowser();
        int calls = 0;
        b.Answer = js => !js.Contains("bodyObj") ? Token(js)
            : ++calls == 1 ? "{\"error\":\"fetch_failed Failed to fetch\",\"authed\":true}"
            : "{\"status\":200,\"ok\":true,\"text\":\"pong\",\"authed\":true}";
        using var f = new Fixture(managed: true, extraTools: EngieTools.All(b));
        f.Approvals.Answers.Enqueue(new ApprovalAnswer(ApprovalChoice.Allow)); // release the untrusted reply
        var r = await f.Run("engie_chat", new { model = "claude-opus-5.5", prompt = "x http" + S + "127.0.0.1:8765/a " + C(8212, 8618) });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(2, calls);
        var sent = b.Scripts.Where(s => s.Contains("bodyObj")).ToList();
        Assert.All(sent, s => Assert.DoesNotContain("127.0.0.1", s));
        Assert.Contains("strict retry", r.Content);
        Assert.Contains("pong", r.Content);
    }

    [Fact]
    public async Task EngieChatGivesUpAfterOneRetry()
    {
        var b = new FakeBrowser();
        int calls = 0;
        b.Answer = js => { if (!js.Contains("bodyObj")) return Token(js); calls++; return "{\"error\":\"fetch_failed Failed to fetch\"}"; };
        using var f = new Fixture(managed: true, extraTools: EngieTools.All(b));
        var r = await f.Run("engie_chat", new { model = "claude-opus-5.5", prompt = "hi" });
        Assert.False(r.Ok);
        Assert.Equal(2, calls);
        Assert.Contains("also after strict cleanup", r.Error!.Message);
    }
}
