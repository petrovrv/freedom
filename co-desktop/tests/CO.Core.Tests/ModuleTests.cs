// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Modules;
using CO.Core.Policy;
using CO.Core.Tools;
using Xunit;

namespace CO.Core.Tests;

// Freedom F10 (SPEC §3.6, ext/1 device.tools): the module loader of a device.
public class ModuleTests
{
    private const string Script = "print('hello')\n";
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private sealed class FakeSource : IModuleSource
    {
        public string List = "";
        public Dictionary<string, string> Files = new();
        public int Downloads;
        public Task<string> ListAsync(CancellationToken ct) => List.Length == 0 ? throw new HttpRequestException("site down") : Task.FromResult(List);
        public Task<byte[]> FileAsync(ModuleEntry m, string rel, CancellationToken ct)
        {
            Downloads++;
            return Task.FromResult(Encoding.UTF8.GetBytes(Files[rel]));
        }
    }

    private sealed class FakeRunner : IModuleRunner
    {
        public readonly List<(string File, JsonObject Req)> Runs = new();
        public Func<JsonObject, string> Answer = r => "{\"ok\":true,\"tool\":" + JsonSerializer.Serialize((string?)r["tool"] ?? (string?)r["op"]) + "}";
        public Task<ModuleRunResult> RunAsync(string file, string workDir, string stdin, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken ct)
        {
            var req = JsonNode.Parse(stdin)!.AsObject();
            Runs.Add((file, req));
            return Task.FromResult(new ModuleRunResult(0, Answer(req), "", false));
        }
    }

    private sealed class NativePart : IModuleNativePart
    {
        public string Id => "native-mod";
        public readonly List<bool> Activations = new();
        public IDeviceTool? NativeTool(string name) => name == "native_hello" ? new HelloTool() : null;
        public Task<JsonNode?>? DeviceOpAsync(string op, JsonElement args, CancellationToken ct) =>
            op == "native_status" ? Task.FromResult<JsonNode?>(new JsonObject { ["state"] = "online" }) : null;
        public void Activate(bool on) => Activations.Add(on);
    }

    private sealed class HelloTool : IDeviceTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = "native_hello", Class = RiskClass.R, Untrusted = UntrustedMode.Never, Description = "hello", InputSchema = Schema.Obj(),
        };
        public ActionPlan Plan(JsonElement a, ToolEnv env) => new() { Tool = "native_hello", Class = RiskClass.R, Summary = "hello" };
        public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct) => Task.FromResult(new ToolOutput { Content = "native hi" });
    }

    private static string ListJson(bool enabled = true, bool standard = true, string deviceDefault = "on", string script = Script) => new JsonObject
    {
        ["modules"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "devmod", ["version"] = "0.1.0", ["title"] = "Device module", ["enabled"] = enabled, ["standard"] = standard, ["permissions"] = new JsonArray("device.tools"),
                ["ui"] = new JsonArray { new JsonObject { ["slot"] = "ui.settings", ["id"] = "settings", ["entry"] = "ui/settings.html" },
                                         new JsonObject { ["slot"] = "ui.evil", ["entry"] = "ui/x.html" } },
                ["device"] = enabled ? new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "devmod_hello", ["class"] = "R", ["description"] = "says hello", ["run"] = "device/hello.py", ["untrusted"] = false },
                        new JsonObject { ["name"] = "devmod_write", ["class"] = "W", ["run"] = "device/hello.py" },
                        new JsonObject { ["name"] = "devmod_mac", ["class"] = "R", ["run"] = "device/hello.py", ["platforms"] = new JsonArray("macos") },
                        new JsonObject { ["name"] = "fs_read", ["class"] = "R", ["run"] = "device/hello.py" },
                        new JsonObject { ["name"] = "devmod_badclass", ["class"] = "Q" },
                        new JsonObject { ["name"] = "Bad Name", ["class"] = "R" },
                        new JsonObject { ["name"] = "devmod_outside", ["class"] = "R", ["run"] = "../x.py" },
                    },
                    ["ops"] = new JsonArray("devmod_folder_add", "Bad Op"),
                    ["opsRun"] = "device/hello.py",
                    ["files"] = new JsonObject { ["device/hello.py"] = Sha(script), ["ui/x.html"] = Sha("x") },
                    ["default"] = deviceDefault, ["warning"] = "Your data, your choice.",
                } : null,
            },
            new JsonObject
            {
                ["id"] = "native-mod", ["version"] = "1.0.0", ["title"] = "Native", ["enabled"] = true, ["standard"] = true, ["permissions"] = new JsonArray("device.tools"),
                ["device"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "native_hello", ["class"] = "R" },
                        new JsonObject { ["name"] = "native_missing", ["class"] = "R" },
                    },
                    ["ops"] = new JsonArray("native_status"), ["files"] = new JsonObject(),
                },
            },
            new JsonObject { ["id"] = "Bad Id", ["enabled"] = true },
        },
    }.ToJsonString();

    private sealed class Rig : IDisposable
    {
        public readonly Fixture F = new();
        public readonly FakeSource Source = new() { List = ListJson(), Files = { ["device/hello.py"] = Script } };
        public readonly FakeRunner Runner = new();
        public readonly NativePart Native = new();
        public readonly ModuleLoader Loader;
        public string Data => Path.Combine(F.Root, "data");
        public Rig(string platform = "windows")
        {
            Loader = new ModuleLoader(F.Catalog, Data, Source, new IModuleNativePart[] { Native }, Runner, platform);
        }
        public void Dispose() => F.Dispose();
    }

    [Fact]
    public void WindowPagesComeFromTheList()
    {
        // ext/1 ui.window: a ui.page with "window": true of an enabled module, by /x/<module>/<page> or its own path
        var list = ModuleList.Parse("""
            {"ext":1,"modules":[
              {"id":"ed","enabled":true,"navigate":["https://pc.example.net","/auth/login.php","http://plain.example","javascript:x"],
               "ui":[{"slot":"ui.page","id":"editor","entry":"ui/editor.html","title":"Editor","path":"/ed","window":true,"windowKey":"path"},
                     {"slot":"ui.page","id":"share","entry":"ui/share.html","path":"/ed-share"},
                     {"slot":"ui.panel","id":"p","entry":"ui/p.html","window":true}]},
              {"id":"off","enabled":false,"ui":[{"slot":"ui.page","id":"x","entry":"ui/x.html","window":true}]}]}
            """)!;
        var ed = list[0];
        Assert.Equal(new[] { "https://pc.example.net", "/auth/login.php" }, ed.Navigate);
        Assert.Equal("editor", ed.WindowPage("/ed")?.Id);
        Assert.Equal("path", ed.WindowPage("/x/ed/editor")!.WindowKey);
        Assert.Null(ed.WindowPage("/ed-share"));     // a page without window opens as before
        Assert.Null(ed.WindowPage("/x/ed/p"));       // only ui.page
        Assert.False(ed.Ui.Single(u => u.Id == "p").Window);
        Assert.Null(list[1].WindowPage("/x/off/x")); // a disabled module gets no window
    }

    [Fact]
    public void SeedSwitchNeverOverridesAChoice()
    {
        using var f = new Fixture();
        var l = new ModuleLoader(new ToolCatalog(), Path.Combine(f.Root, "data"), null);
        Assert.True(l.SeedSwitch("m", false));    // an older setting taken over once
        Assert.False(l.SeedSwitch("m", true));    // never over a switch already set
        var again = new ModuleLoader(new ToolCatalog(), Path.Combine(f.Root, "data"), null);
        Assert.False(again.SeedSwitch("m", true));
    }

    [Fact]
    public void ParseKeepsOnlyValidEntries()
    {
        var list = ModuleList.Parse(ListJson())!;
        Assert.Equal(new[] { "devmod", "native-mod" }, list.Select(m => m.Id));
        var m = list[0];
        Assert.Equal(new[] { "devmod_hello", "devmod_write", "devmod_mac", "fs_read" }, m.Tools.Select(t => t.Name));
        Assert.Equal(RiskClass.R, m.Tools[0].Class);
        Assert.False(m.Tools[0].Untrusted);
        Assert.True(m.Tools[1].Untrusted);   // untrusted unless the manifest says false
        Assert.Equal(new[] { "devmod_folder_add" }, m.DeviceOps);
        Assert.Equal(new[] { "device/hello.py" }, m.Files.Keys);
        Assert.Equal(new[] { "ui.settings" }, m.Ui.Select(u => u.Slot));
        Assert.Equal("device/hello.py", m.OpsRun);
        Assert.True(list[1].Tools[0].Native);
        Assert.Null(ModuleList.Parse("{\"ext\":2,\"modules\":[]}"));
        Assert.Null(ModuleList.Parse("not json"));
    }

    [Fact]
    public async Task ActiveModuleToolsGoThroughThePolicy()
    {
        using var r = new Rig();
        Assert.True(await r.Loader.RefreshAsync(CancellationToken.None));
        Assert.Equal(new[] { "devmod_hello", "devmod_write", "native_hello" }, r.Loader.RegisteredTools());
        var st = r.Loader.States().Single(s => s.Id == "devmod");
        Assert.Contains("fs_read: name taken by the core", st.Problem);
        Assert.Contains("native_missing", r.Loader.States().Single(s => s.Id == "native-mod").Problem);
        Assert.Equal(new[] { true }, r.Native.Activations);

        // R of a standard module: no question; the script gets the request on stdin
        var res = await r.F.Run("devmod_hello", new { name = "x" });
        Assert.True(res.Ok, res.Error?.Message);
        Assert.Contains("devmod_hello", res.Content);
        var (file, req) = r.Runner.Runs.Single();
        Assert.EndsWith(Path.Combine("device", "hello.py"), file);
        Assert.Equal("tool", (string?)req["kind"]);
        Assert.Equal(1, (int)req["ext"]!);
        Assert.Equal("x", (string?)req["args"]!["name"]);
        Assert.Empty(r.F.Approvals.Seen);

        // native tool of the compiled part
        Assert.Equal("native hi", (await r.F.Run("native_hello", new { })).Content);

        // the published catalog carries the class
        Assert.Contains(r.F.Catalog.ToMcpList(), t => (string?)t!["name"] == "devmod_write" && ((string?)t["description"])!.StartsWith("[W] [module Device module]"));
    }

    [Fact]
    public async Task ScriptToolsOfOtherAuthorsCountAsCodeExecution()
    {
        using var r = new Rig();
        r.Source.List = ListJson(standard: false);
        await r.Loader.RefreshAsync(CancellationToken.None);
        r.F.Approvals.Answers.Enqueue(new CO.Core.Host.ApprovalAnswer(CO.Core.Host.ApprovalChoice.Deny, "no"));
        var res = await r.F.Run("devmod_hello", new { });
        Assert.Equal("denied_by_user", res.Error?.Code);
        Assert.Equal(RiskClass.X, r.F.Approvals.Seen.Single().Plan.Class);
        Assert.Empty(r.Runner.Runs);
    }

    [Fact]
    public async Task SwitchOffTakesToolsOutWithAClearError()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        int changes = 0;
        r.Loader.CatalogChanged += () => changes++;
        Assert.True(await r.Loader.SetOnAsync("devmod", false, CancellationToken.None));
        Assert.Equal(1, changes);
        Assert.False(r.F.Catalog.Contains("devmod_hello"));
        Assert.Equal("module_off", (await r.F.Run("devmod_hello", new { })).Error?.Code);
        Assert.Equal("unknown_tool", (await r.F.Run("never_there", new { })).Error?.Code);

        // the switch is kept on this device and wins over the default
        var again = new ModuleLoader(new ToolCatalog(), r.Data, null, runner: r.Runner, platform: "windows");
        again.LoadCached();
        Assert.False(again.On(again.Modules.Single(m => m.Id == "devmod")));
        Assert.True(await r.Loader.SetOnAsync("devmod", true, CancellationToken.None));
        Assert.True((await r.F.Run("devmod_hello", new { })).Ok);
    }

    [Fact]
    public async Task DisabledInTheWorkbenchOrDefaultOff()
    {
        using var r = new Rig();
        r.Source.List = ListJson(deviceDefault: "off");
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.Equal(new[] { "native_hello" }, r.Loader.RegisteredTools());
        Assert.Equal(0, r.Source.Downloads);   // nothing downloaded for a module that is off here
        Assert.True(await r.Loader.SetOnAsync("devmod", true, CancellationToken.None));
        Assert.Contains("devmod_hello", r.Loader.RegisteredTools());

        r.Source.List = ListJson(enabled: false);
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain("devmod_hello", r.Loader.RegisteredTools());
        Assert.Equal("module_off", (await r.F.Run("devmod_hello", new { })).Error?.Code);
    }

    [Fact]
    public async Task ChangedFileIsNeverRun()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        var m = r.Loader.Modules.Single(x => x.Id == "devmod");
        File.WriteAllText(r.Loader.Cache.PathOf(m, "device/hello.py"), "import os; os.system('evil')\n");
        var res = await r.F.Run("devmod_hello", new { });
        Assert.Equal("module_file_changed", res.Error?.Code);
        Assert.Empty(r.Runner.Runs);
        // the next refresh downloads it again
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.True(r.Loader.Cache.Matches(m, "device/hello.py"));
        Assert.True((await r.F.Run("devmod_hello", new { })).Ok);
    }

    [Fact]
    public async Task DownloadThatDoesNotMatchIsRefused()
    {
        using var r = new Rig();
        r.Source.Files["device/hello.py"] = "tampered\n";
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain("devmod_hello", r.Loader.RegisteredTools());
        Assert.Contains("does not match", r.Loader.States().Single(s => s.Id == "devmod").Problem);
    }

    [Fact]
    public async Task SiteDownUsesTheKeptList()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        var cat = new ToolCatalog();
        var offline = new ModuleLoader(cat, r.Data, new FakeSource(), new IModuleNativePart[] { new NativePart() }, r.Runner, "windows");
        await offline.RefreshAsync(CancellationToken.None);
        Assert.True(cat.Contains("devmod_hello"));
    }

    [Fact]
    public async Task SwitchWrittenByAnotherProcessIsApplied()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        // the CO window of the Mac is another process: it reads the kept list and writes the switch file
        var window = new ModuleLoader(new ToolCatalog(), r.Data, null, runner: r.Runner, platform: "windows");
        window.LoadCached();
        await window.SetOnAsync("devmod", false, CancellationToken.None);
        Assert.Contains("devmod_hello", r.Loader.RegisteredTools());
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain("devmod_hello", r.Loader.RegisteredTools());
    }

    [Fact]
    public async Task NewFileUnderTheSameVersionReplacesTheTool()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        const string v2 = "print('hello again')\n";
        r.Source.List = ListJson(script: v2);
        r.Source.Files["device/hello.py"] = v2;
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.True((await r.F.Run("devmod_hello", new { })).Ok);
    }

    [Fact]
    public async Task RunnerTimeoutCoversAnUnreadInput()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "co-modrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "sleep.cmd");
            File.WriteAllText(file, "@ping -n 60 127.0.0.1 >nul\r\n");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = await new ProcessModuleRunner().RunAsync(file, dir, new string('x', 4 << 20), new Dictionary<string, string>(),
                TimeSpan.FromSeconds(3), CancellationToken.None);
            Assert.True(res.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ScriptOpsOfOtherAuthorsAskAndManagedRefuses()
    {
        using var r = new Rig();
        r.Source.List = ListJson(standard: false);
        await r.Loader.RefreshAsync(CancellationToken.None);
        var args = JsonDocument.Parse("{}").RootElement;
        // nobody to ask: refused, nothing run
        var e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None));
        Assert.Equal("denied_by_user", e.Code);
        Assert.Empty(r.Runner.Runs);
        int asked = 0;
        r.Loader.ConfirmScriptOp = (m, op, ct) => { asked++; return Task.FromResult(true); };
        await r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None);
        await r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None);
        Assert.Equal(1, asked);   // a yes holds for this module version
        Assert.Equal(2, r.Runner.Runs.Count);
        r.Loader.Managed = () => true;
        e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None));
        Assert.Equal("denied_by_policy", e.Code);
        // native ops of the compiled part are the app's own code
        Assert.NotNull(await r.Loader.DeviceOpAsync("native-mod", "native_status", args, CancellationToken.None));
    }

    [Fact]
    public async Task AppliedFiresWithoutToolChanges()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        int applied = 0;
        r.Loader.Applied += () => applied++;
        Assert.False(await r.Loader.RefreshAsync(CancellationToken.None));
        Assert.Equal(1, applied);
    }

    [Fact]
    public async Task TrustDowngradeReplacesTheTool()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        r.Source.List = ListJson(standard: false);
        await r.Loader.RefreshAsync(CancellationToken.None);
        r.F.Approvals.Answers.Enqueue(new CO.Core.Host.ApprovalAnswer(CO.Core.Host.ApprovalChoice.Deny, "no"));
        Assert.Equal("denied_by_user", (await r.F.Run("devmod_hello", new { })).Error?.Code);
        Assert.Equal(RiskClass.X, r.F.Approvals.Seen.Single().Plan.Class);
    }

    [Fact]
    public async Task AllowForTaskCoversTheNextCallOfTheTool()
    {
        using var r = new Rig();
        var json = JsonNode.Parse(ListJson())!;
        json["modules"]![0]!["device"]!["tools"]![0]!["class"] = "X";   // code execution, clean output: grantable
        r.Source.List = json.ToJsonString();
        await r.Loader.RefreshAsync(CancellationToken.None);
        r.F.Approvals.Answers.Enqueue(new CO.Core.Host.ApprovalAnswer(CO.Core.Host.ApprovalChoice.AllowForTask, ""));
        Assert.True((await r.F.Run("devmod_hello", new { })).Ok);
        Assert.True((await r.F.Run("devmod_hello", new { })).Ok);
        Assert.Single(r.F.Approvals.Seen);
    }

    [Fact]
    public void RiskIsAnotherNameOfTheClassAndOtherWordsAreForOtherDevices()
    {
        var json = JsonNode.Parse(ListJson())!;
        json["modules"]![1]!["device"]!["tools"] = new JsonArray
        {
            new JsonObject { ["name"] = "native_hello", ["risk"] = "W" },
            new JsonObject { ["name"] = "native_act", ["risk"] = "act" },
        };
        var m = ModuleList.Parse(json.ToJsonString())![1];
        Assert.Equal(new[] { "native_hello" }, m.Tools.Select(t => t.Name));
        Assert.Equal(RiskClass.W, m.Tools[0].Class);
    }

    [Fact]
    public void NoDevicePartWithoutThePermission()
    {
        var json = JsonNode.Parse(ListJson())!;
        json["modules"]![0]!["permissions"] = new JsonArray();
        var m = ModuleList.Parse(json.ToJsonString())![0];
        Assert.False(m.HasDevicePart);
        Assert.Empty(m.Files);
    }

    [Fact]
    public async Task NativePartOnlyForAStandardModule()
    {
        using var r = new Rig();
        var json = JsonNode.Parse(ListJson())!;
        json["modules"]![1]!["standard"] = false;
        r.Source.List = json.ToJsonString();
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.DoesNotContain("native_hello", r.Loader.RegisteredTools());
        Assert.Empty(r.Native.Activations);
        var e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("native-mod", "native_status", JsonDocument.Parse("{}").RootElement, CancellationToken.None));
        Assert.Equal("op_unavailable", e.Code);
    }

    [Fact]
    public async Task PlatformFilter()
    {
        using var r = new Rig("macos");
        await r.Loader.RefreshAsync(CancellationToken.None);
        Assert.Contains("devmod_mac", r.Loader.RegisteredTools());
    }

    [Fact]
    public async Task DeviceOpsOnlyDeclaredAndOnlyWhileOn()
    {
        using var r = new Rig();
        await r.Loader.RefreshAsync(CancellationToken.None);
        var args = JsonDocument.Parse("{\"path\":\"C:/x\"}").RootElement;
        var res = await r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None);
        Assert.Equal("devmod_folder_add", (string?)res!["tool"]);
        Assert.Equal("op", (string?)r.Runner.Runs.Single().Req["kind"]);
        Assert.Equal("C:/x", (string?)r.Runner.Runs.Single().Req["args"]!["path"]);
        Assert.Equal("online", (string?)(await r.Loader.DeviceOpAsync("native-mod", "native_status", args, CancellationToken.None))!["state"]);

        var e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("devmod", "fs_delete", args, CancellationToken.None));
        Assert.Equal("op_not_declared", e.Code);
        e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("nope", "devmod_folder_add", args, CancellationToken.None));
        Assert.Equal("module_disabled", e.Code);
        await r.Loader.SetOnAsync("devmod", false, CancellationToken.None);
        e = await Assert.ThrowsAsync<ToolException>(() => r.Loader.DeviceOpAsync("devmod", "devmod_folder_add", args, CancellationToken.None));
        Assert.Equal("module_off", e.Code);
    }

    [Fact]
    public async Task ProcessRunnerRunsAScript()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "co-modrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "echo.cmd");
            File.WriteAllText(file, "@echo off\r\nmore\r\n");
            var res = await new ProcessModuleRunner().RunAsync(file, dir, "{\"ext\":1}", new Dictionary<string, string> { ["AIWB_MODULE_ID"] = "x" },
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(0, res.ExitCode);
            Assert.Contains("{\"ext\":1}", res.Stdout);
            var (exe, a) = ProcessModuleRunner.Command("a.py");
            Assert.Equal("python", exe);
            Assert.Equal(new[] { "-I", "a.py" }, a);
        }
        finally { Directory.Delete(dir, true); }
    }
}
