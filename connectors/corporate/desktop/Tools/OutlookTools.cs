// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CO.Core.Files;
using CO.Core.Policy;

namespace CO.Corporate.Tools;

// TZ §7.3: Outlook mail and calendar of the device. outlook_send is not offered: drafts are left for the user to send.
// Calendar tools change only the user's own events without attendees; anything that mails other people (invitations,
// answers to the organizer) is class E, which the managed profile denies.
public static class OutlookTools
{
    // A managed device releases their results without a prompt: the connector ticked is the consent (core ToolHost).
    static OutlookTools() => CO.Core.Host.ToolHost.ReleaseByConsent("outlook_", "calendar_");

    public static IEnumerable<IDeviceTool> All(IOutlook outlook, OutlookSettings settings) => new IDeviceTool[]
    {
        new OutlookFolders(outlook, settings), new OutlookSearch(outlook, settings), new OutlookRead(outlook, settings),
        new OutlookSaveAttachments(outlook, settings), new OutlookDraft(outlook, settings), new OutlookMove(outlook, settings),
        new OutlookCategorize(outlook, settings), new OutlookFlag(outlook, settings),
        new CalendarList(outlook, settings), new CalendarFindSlot(outlook, settings), new CalendarFind(outlook, settings),
        new CalendarCreate(outlook, settings), new CalendarUpdate(outlook, settings), new CalendarDelete(outlook, settings),
        new CalendarRespond(outlook, settings),
    };

    internal static void Require(OutlookSettings s, Rights need)
    {
        bool write = need != Rights.Read;
        if (!s.Read || (need.HasFlag(Rights.Create) && !s.CanCreate) || (need.HasFlag(Rights.Edit) && !s.CanEdit)
            || (need.HasFlag(Rights.Delete) && !s.CanDelete))
            throw new ToolException("outlook_disabled", write
                ? $"Outlook: {RightsText.List(need)} is not ticked. The user can tick it in CO Desktop → Connectors → Outlook."
                : "Outlook access is off. The user can allow it in CO Desktop → Connectors → Outlook.");
    }

    internal static ActionPlan Plan(string tool, RiskClass c, string summary) =>
        new ActionPlan { Tool = tool, Class = c, Summary = summary }.Also(p => p.Scopes.Add(new Scope(ScopeKind.App, "outlook")));

    internal static ActionPlan Also(this ActionPlan p, Action<ActionPlan> a) { a(p); return p; }

    internal static DateTime? Date(JsonElement a, string name)
    {
        string? s = Args.OptStr(a, name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d))
            throw new ToolException("invalid_args", $"'{name}': use a date like 2026-10-14 or 2026-10-14T09:00.");
        return d;
    }

    internal static string Utc(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "";

    internal static string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm");

    internal static object View(CalendarEvent e) => new
    {
        id = e.Id, start = Local(e.StartUtc), end = Local(e.EndUtc), subject = e.Subject, location = e.Location, organizer = e.Organizer,
        allDay = e.AllDay, busy = e.BusyStatus, response = e.Response, own = e.Own, recurring = e.Recurring,
    };
}

internal abstract class OutlookTool : IDeviceTool
{
    protected readonly IOutlook O;
    protected readonly OutlookSettings S;
    protected OutlookTool(IOutlook o, OutlookSettings s) { O = o; S = s; }
    public abstract ToolDescriptor Descriptor { get; }
    // Box of CO Desktop → Connectors → Outlook this tool needs: drafts and saved attachments Create, the rest of changes Edit.
    protected virtual Rights Need => Descriptor.Class == RiskClass.R ? Rights.Read : Rights.Edit;

    public virtual ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        OutlookTools.Require(S, Need);
        return OutlookTools.Plan(Descriptor.Name, Descriptor.Class, Descriptor.Name);
    }

    public Task<ToolOutput> ExecuteAsync(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        OutlookTools.Require(S, Need);
        return Run(a, env, ct);
    }

    protected abstract Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct);
    protected Task<ToolOutput> Out(object o, bool untrusted = false) =>
        Task.FromResult(new ToolOutput { Content = FsTools.Json(o), Untrusted = untrusted });
}

internal sealed class OutlookFolders : OutlookTool
{
    public OutlookFolders(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_folders", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Mail folders of the Outlook mailbox ('Inbox', 'Inbox/Projects', 'Sent Items', ...).",
        InputSchema = Schema.Obj(),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct) => Out(O.Folders());
}

internal sealed class OutlookSearch : OutlookTool
{
    public OutlookSearch(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_search", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Search mail; newest first. Without 'folder' searches Inbox, Sent Items and Archive with their subfolders. Returns ids for outlook_read.",
        InputSchema = Schema.Obj(
            ("folder", "string", "Folder path from outlook_folders, e.g. 'Inbox' or 'Inbox/Projects'", false),
            ("from", "string", "Sender name or address contains", false),
            ("subject", "string", "Subject contains", false),
            ("text", "string", "Subject or body contains", false),
            ("since", "string", "Received on/after, e.g. 2026-10-01", false),
            ("until", "string", "Received before, e.g. 2026-10-14", false),
            ("hasAttachments", "boolean", "Only mail with attachments", false),
            ("unread", "boolean", "Only unread mail", false),
            ("subfolders", "boolean", "Include subfolders, default true", false),
            ("maxResults", "integer", "Default 50, at most 500", false)),
    };

    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        bool? opt(string n) => a.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        var q = new MailQuery(Args.OptStr(a, "folder"), Args.OptStr(a, "from"), Args.OptStr(a, "subject"), Args.OptStr(a, "text"),
            OutlookTools.Date(a, "since"), OutlookTools.Date(a, "until"), opt("hasAttachments"), opt("unread"),
            (int)Math.Clamp(Args.OptLong(a, "maxResults", 50), 1, 500), Args.OptBool(a, "subfolders", true));
        var list = O.Search(q).Select(m => new
        {
            id = m.Id, folder = m.Folder, received = OutlookTools.Utc(m.ReceivedUtc), from = m.From, subject = m.Subject,
            attachments = m.Attachments, unread = m.Unread, categories = m.Categories, flag = m.Flag,
        });
        return Out(list, untrusted: true);
    }
}

internal sealed class OutlookRead : OutlookTool
{
    public OutlookRead(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_read", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Text of a mail (plain body) with its attachments list. offset/length are characters of the body.",
        InputSchema = Schema.Obj(("id", "string", "Mail id from outlook_search", true),
            ("offset", "integer", "Start character, default 0", false), ("length", "integer", "Characters, default 30000", false)),
    };

    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var m = O.Read(Args.Str(a, "id"));
        int offset = (int)Math.Clamp(Args.OptLong(a, "offset", 0), 0, m.Body.Length);
        int length = (int)Math.Clamp(Args.OptLong(a, "length", 30_000), 0, m.Body.Length - offset);
        return Out(new
        {
            id = m.Id, folder = m.Folder, received = OutlookTools.Utc(m.ReceivedUtc), from = m.From, to = m.To, cc = m.Cc,
            subject = m.Subject, categories = m.Categories,
            attachments = m.Attachments.Select(x => new { index = x.Index, name = x.Name, size = x.Size }),
            body = m.Body.Substring(offset, length), bodyRange = $"{offset}-{offset + length} of {m.Body.Length}",
        }, untrusted: true);
    }
}

internal sealed class OutlookSaveAttachments : OutlookTool
{
    public OutlookSaveAttachments(IOutlook o, OutlookSettings s) : base(o, s) { }
    protected override Rights Need => Rights.Create;
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_save_attachments", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Save attachments of a mail into a writable connected folder (local or SharePoint). Existing files are not overwritten.",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = "Mail id" },
                ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Target directory '<folderId>/<relative>'" },
                ["names"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Only these attachment names (default: all)" },
            },
            ["required"] = new JsonArray("id", "folder"),
            ["additionalProperties"] = false,
        },
    };

    public override ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        OutlookTools.Require(S, Need);
        var r = env.Folders.Resolve(Args.Str(a, "folder"));
        FsTools.Require(r, Rights.Create);
        var p = new ActionPlan { Tool = Descriptor.Name, Class = RiskClass.W, WritesToSynced = r.Folder.Synced, Summary = "save attachments to " + r.Virtual };
        p.Scopes.Add(new Scope(ScopeKind.Folder, r.FullPath));
        return p;
    }

    protected override async Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var r = env.Folders.Resolve(Args.Str(a, "folder"));
        FsTools.Require(r, Rights.Create);
        List<string>? names = a.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array
            ? n.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : null;

        // Attachments go to a temporary folder first, then into place without overwriting anything.
        string tmp = Path.Combine(Path.GetTempPath(), "co-att-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var saved = O.SaveAttachments(Args.Str(a, "id"), names, tmp);
            var placed = new List<string>();
            if (r.Remote == null)
            {
                Directory.CreateDirectory(r.FullPath);
                int step = env.Snapshots.BeginStep(env.RunId);
                foreach (string f in saved)
                {
                    string name = f;
                    for (int i = 2; File.Exists(Path.Combine(r.FullPath, name)); i++)
                        name = Path.GetFileNameWithoutExtension(f) + $" ({i})" + Path.GetExtension(f);
                    string target = Path.Combine(r.FullPath, name);
                    env.Snapshots.CaptureFile(env.RunId, step, target);   // undo removes it
                    File.Move(Path.Combine(tmp, f), target);
                    placed.Add(name);
                }
                return new ToolOutput { Content = FsTools.Json(new { folder = r.Virtual, saved = placed }) };
            }
            var sp = RemoteFolders.Of(env);
            foreach (string f in saved)
            {
                string name = f;
                for (int i = 2; await sp.IsDirAsync(sp.Child(r.Remote, name), ct) != null; i++)
                    name = Path.GetFileNameWithoutExtension(f) + $" ({i})" + Path.GetExtension(f);
                await sp.UploadAsync(sp.Child(r.Remote, name), await File.ReadAllBytesAsync(Path.Combine(tmp, f), ct), ct);
                placed.Add(name);
            }
            return new ToolOutput { Content = FsTools.Json(new { folder = r.Virtual, saved = placed }) };
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } }
    }
}

internal sealed class OutlookDraft : OutlookTool
{
    public OutlookDraft(IOutlook o, OutlookSettings s) : base(o, s) { }
    protected override Rights Need => Rights.Create;
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_draft", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Create a draft in Drafts (new mail, or a reply when replyToId is given). It is not sent: the user reviews and sends it.",
        InputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Recipients, separated by ';' (new mail)" },
                ["cc"] = new JsonObject { ["type"] = "string", ["description"] = "Copy, separated by ';'" },
                ["subject"] = new JsonObject { ["type"] = "string", ["description"] = "Subject (new mail; replies keep RE:)" },
                ["body"] = new JsonObject { ["type"] = "string", ["description"] = "Text of the mail; for a reply it goes above the quoted mail" },
                ["replyToId"] = new JsonObject { ["type"] = "string", ["description"] = "Mail id to reply to" },
                ["replyAll"] = new JsonObject { ["type"] = "boolean", ["description"] = "Reply to all, default false" },
                ["attachments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Files '<folderId>/<relative>' to attach" },
            },
            ["required"] = new JsonArray("body"),
            ["additionalProperties"] = false,
        },
    };

    protected override async Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        var temp = new List<string>();
        try
        {
            var paths = new List<string>();
            if (a.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var r = env.Folders.Resolve(e.GetString() ?? "");
                    if (r.Remote == null) { if (!File.Exists(r.FullPath)) throw new ToolException("not_found", r.Virtual + " not found."); paths.Add(r.FullPath); continue; }
                    var sp = RemoteFolders.Of(env);
                    string dir = Path.Combine(Path.GetTempPath(), "co-draft-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    temp.Add(dir);
                    string f = Path.Combine(dir, r.Remote.Name);
                    await File.WriteAllBytesAsync(f, await sp.DownloadAsync(r.Remote, ct), ct);
                    paths.Add(f);
                }
            string? replyTo = Args.OptStr(a, "replyToId");
            if (replyTo == null && string.IsNullOrWhiteSpace(Args.OptStr(a, "to")))
                throw new ToolException("invalid_args", "A new mail needs 'to'.");
            string id = O.CreateDraft(new DraftRequest(Args.OptStr(a, "to"), Args.OptStr(a, "cc"), Args.OptStr(a, "subject") ?? "",
                Args.Str(a, "body"), replyTo, Args.OptBool(a, "replyAll", false), paths));
            return new ToolOutput { Content = FsTools.Json(new { draftId = id, note = "Saved in Drafts; not sent." }) };
        }
        finally { foreach (string d in temp) try { Directory.Delete(d, true); } catch (IOException) { } }
    }
}

internal sealed class OutlookMove : OutlookTool
{
    public OutlookMove(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_move", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Move a mail to another folder (path from outlook_folders). Returns the new id.",
        InputSchema = Schema.Obj(("id", "string", "Mail id", true), ("folder", "string", "Target folder path", true)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct) =>
        Out(new { id = O.Move(Args.Str(a, "id"), Args.Str(a, "folder")) });
}

internal sealed class OutlookCategorize : OutlookTool
{
    public OutlookCategorize(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_categorize", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Set the categories of a mail (comma-separated; empty clears).",
        InputSchema = Schema.Obj(("id", "string", "Mail id", true), ("categories", "string", "e.g. 'Project X, To read'", true)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        O.SetCategories(Args.Str(a, "id"), Args.Str(a, "categories"));
        return Out(new { ok = true });
    }
}

internal sealed class OutlookFlag : OutlookTool
{
    public OutlookFlag(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "outlook_flag", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Flag a mail for follow-up, mark the flag complete, or clear it.",
        InputSchema = Schema.Obj(("id", "string", "Mail id", true), ("flag", "string", "flag | complete | clear", true)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string f = Args.Str(a, "flag");
        if (f is not ("flag" or "complete" or "clear")) throw new ToolException("invalid_args", "flag: flag | complete | clear.");
        O.SetFlag(Args.Str(a, "id"), f);
        return Out(new { ok = true });
    }
}

internal sealed class CalendarList : OutlookTool
{
    public CalendarList(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_list", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Calendar events (recurring ones expanded) between two dates; default today and the next 7 days. ids are for calendar_update/delete/respond.",
        InputSchema = Schema.Obj(("from", "string", "Start, e.g. 2026-10-14", false), ("to", "string", "End (exclusive), e.g. 2026-10-21", false)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        DateTime from = OutlookTools.Date(a, "from") ?? DateTime.Today.ToUniversalTime();
        DateTime to = OutlookTools.Date(a, "to") ?? from.AddDays(7);
        var list = O.Calendar(from, to).Select(e => new
        {
            id = e.Id, start = OutlookTools.Local(e.StartUtc), end = OutlookTools.Local(e.EndUtc),
            subject = e.Subject, location = e.Location, organizer = e.Organizer, allDay = e.AllDay, busy = e.BusyStatus, attendees = e.Attendees,
            response = e.Response, own = e.Own,
        });
        return Out(new { timeZone = TimeZoneInfo.Local.Id, events = list }, untrusted: true);
    }
}

internal sealed class CalendarFindSlot : OutlookTool
{
    public CalendarFindSlot(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_find_slot", Class = RiskClass.R, Untrusted = UntrustedMode.Never,
        Description = "Free time in the user's calendar: working days, 09:00–17:00 local unless given.",
        InputSchema = Schema.Obj(("minutes", "integer", "Length of the slot", true),
            ("from", "string", "Search from, default now", false), ("to", "string", "Search until, default +7 days", false),
            ("dayStart", "integer", "Working day start hour, default 9", false), ("dayEnd", "integer", "Working day end hour, default 17", false)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        DateTime fromUtc = OutlookTools.Date(a, "from") ?? DateTime.UtcNow;
        DateTime toUtc = OutlookTools.Date(a, "to") ?? fromUtc.AddDays(7);
        var events = O.Calendar(fromUtc, toUtc);
        var slots = FreeSlots.Find(events, TimeZoneInfo.ConvertTimeFromUtc(fromUtc, TimeZoneInfo.Local), TimeZoneInfo.ConvertTimeFromUtc(toUtc, TimeZoneInfo.Local),
            (int)Math.Clamp(Args.OptLong(a, "minutes", 30), 5, 24 * 60), (int)Args.OptLong(a, "dayStart", 9), (int)Args.OptLong(a, "dayEnd", 17));
        return Out(new { timeZone = TimeZoneInfo.Local.Id, slots = slots.Take(50).Select(s => new { start = s.start.ToString("yyyy-MM-dd HH:mm"), end = s.end.ToString("yyyy-MM-dd HH:mm") }) });
    }
}

internal sealed class CalendarFind : OutlookTool
{
    public CalendarFind(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_find", Class = RiskClass.R, Untrusted = UntrustedMode.Always,
        Description = "Find calendar events by subject and/or date (default today and the next 30 days); returns the ids for calendar_update, calendar_delete and calendar_respond.",
        InputSchema = Schema.Obj(("subject", "string", "Text contained in the subject (case-insensitive)", false), ("date", "string", "One day, e.g. 2026-10-14 (overrides from/to)", false),
            ("from", "string", "Start, e.g. 2026-10-14", false), ("to", "string", "End (exclusive), e.g. 2026-11-14", false), ("maxResults", "integer", "1..200, default 20", false)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        DateTime from = OutlookTools.Date(a, "from") ?? DateTime.Today.ToUniversalTime();
        DateTime to = OutlookTools.Date(a, "to") ?? from.AddDays(30);
        if (OutlookTools.Date(a, "date") is DateTime d) { from = d; to = d.AddDays(1); }
        if (to <= from) throw new ToolException("invalid_args", "'to' must be after 'from'.");
        string? subject = Args.OptStr(a, "subject");
        int max = (int)Math.Clamp(Args.OptLong(a, "maxResults", 20), 1, 200);
        var list = O.Calendar(from, to)
            .Where(e => string.IsNullOrEmpty(subject) || e.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.StartUtc).Take(max).Select(OutlookTools.View);
        return Out(new { timeZone = TimeZoneInfo.Local.Id, events = list }, untrusted: true);
    }
}

internal sealed class CalendarCreate : OutlookTool
{
    private static readonly string[] BusyValues = { "free", "tentative", "busy", "outOfOffice", "workingElsewhere" };
    public CalendarCreate(IOutlook o, OutlookSettings s) : base(o, s) { }
    protected override Rights Need => Rights.Create;
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_create", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Create an event in the user's calendar. Without attendees nothing is sent; attendees are mailed an invitation and need send=true (external action).",
        InputSchema = Input(),
    };

    private static JsonObject Input()
    {
        var sc = Schema.Obj(("subject", "string", "Subject", true), ("start", "string", "Local start, e.g. 2026-10-14T09:30", true),
            ("end", "string", "Local end (or minutes)", false), ("minutes", "integer", "Duration, default 30", false), ("location", "string", "Location", false),
            ("body", "string", "Notes", false), ("reminder", "integer", "Reminder minutes before, default 15; 0 = none", false),
            ("busy", "string", "free|tentative|busy|outOfOffice|workingElsewhere, default busy", false), ("allDay", "boolean", "All-day event", false),
            ("send", "boolean", "Required true when attendees are given (mails the invitation)", false));
        sc["properties"]!["attendees"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "E-mail addresses to invite (default none)" };
        return sc;
    }

    internal static string? Busy(JsonElement a, string? dflt)
    {
        string? b = Args.OptStr(a, "busy");
        if (b is null) return dflt;
        return BusyValues.FirstOrDefault(v => v.Equals(b, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException("invalid_args", "busy: free | tentative | busy | outOfOffice | workingElsewhere.");
    }

    private static List<string> Attendees(JsonElement a) =>
        a.TryGetProperty("attendees", out var at) && at.ValueKind == JsonValueKind.Array
            ? at.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    public override ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        OutlookTools.Require(S, Need);
        int n = Attendees(a).Count;
        if (n > 0 && !Args.OptBool(a, "send", false)) throw new ToolException("invalid_args", "Attendees are mailed an invitation: pass send=true.");
        string start = OutlookTools.Date(a, "start") is DateTime d ? OutlookTools.Local(d) : "?";
        string summary = $"create event '{Args.OptStr(a, "subject")}' {start}" + (n > 0 ? $" and mail an invitation to {n} attendee(s)" : "");
        return OutlookTools.Plan(Descriptor.Name, n > 0 ? RiskClass.E : RiskClass.W, summary);
    }

    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string subject = Args.Str(a, "subject");
        DateTime start = OutlookTools.Date(a, "start") ?? throw new ToolException("invalid_args", "Missing string argument 'start'.");
        DateTime? endArg = OutlookTools.Date(a, "end");
        long minutes = Args.OptLong(a, "minutes", 0);
        if (endArg is not null && minutes != 0) throw new ToolException("invalid_args", "Pass 'end' or 'minutes', not both.");
        if (minutes < 0) throw new ToolException("invalid_args", "'minutes' must be positive.");
        bool allDay = Args.OptBool(a, "allDay", false);
        DateTime end = endArg ?? (allDay && minutes == 0 ? start.AddDays(1) : start.AddMinutes(minutes == 0 ? 30 : minutes));
        if (end <= start) throw new ToolException("invalid_args", "'end' must be after 'start'.");
        var attendees = Attendees(a);
        bool send = Args.OptBool(a, "send", false);
        if (attendees.Count > 0 && !send) throw new ToolException("invalid_args", "Attendees are mailed an invitation: pass send=true.");
        long reminder = Args.OptLong(a, "reminder", 15);
        if (reminder < 0) throw new ToolException("invalid_args", "'reminder' must be 0 or more.");
        string id = O.CreateEvent(new EventRequest(subject, start, end, Args.OptStr(a, "location"), Args.OptStr(a, "body"),
            reminder == 0 ? null : (int)Math.Min(reminder, 40320), Busy(a, "busy")!, allDay, attendees));
        string note = attendees.Count > 0 ? $"Invitation sent to {attendees.Count} attendee(s)." : "Saved in the calendar; nothing was sent.";
        return Out(new { id, start = OutlookTools.Local(start), end = OutlookTools.Local(end), subject, note });
    }
}

internal sealed class CalendarUpdate : OutlookTool
{
    public CalendarUpdate(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_update", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Change (move, rename, ...) one of the user's own events without attendees (id from calendar_find); only the given fields change, nothing is sent.",
        InputSchema = Schema.Obj(("id", "string", "Event id", true), ("subject", "string", "New subject", false), ("start", "string", "New local start (keeps the duration)", false),
            ("end", "string", "New local end", false), ("minutes", "integer", "New duration from the (new) start", false), ("location", "string", "New location", false),
            ("body", "string", "New notes", false), ("reminder", "integer", "Reminder minutes before; 0 = off", false),
            ("busy", "string", "free|tentative|busy|outOfOffice|workingElsewhere", false)),
    };
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string id = Args.Str(a, "id");
        var ev = O.GetEvent(id);
        if (!ev.Own) throw new ToolException("not_own_event", "Only the user's own events without attendees can be changed (an invitation: calendar_respond).");
        DateTime? start = OutlookTools.Date(a, "start"), end = OutlookTools.Date(a, "end");
        long minutes = Args.OptLong(a, "minutes", 0);
        if (end is not null && minutes != 0) throw new ToolException("invalid_args", "Pass 'end' or 'minutes', not both.");
        if (minutes < 0) throw new ToolException("invalid_args", "'minutes' must be positive.");
        if (minutes > 0) end = (start ?? ev.StartUtc).AddMinutes(minutes);
        else if (start is not null && end is null) end = start.Value + (ev.EndUtc - ev.StartUtc);
        if ((end ?? ev.EndUtc) <= (start ?? ev.StartUtc)) throw new ToolException("invalid_args", "'end' must be after 'start'.");
        long reminder = Args.OptLong(a, "reminder", -1);
        string newId = O.UpdateEvent(id, new EventChange(Args.OptStr(a, "subject"), start, end, Args.OptStr(a, "location"), Args.OptStr(a, "body"),
            reminder < 0 ? null : (int)Math.Min(reminder, 40320), CalendarCreate.Busy(a, null)));
        return Out(new { id = newId, @event = OutlookTools.View(O.GetEvent(newId)), note = "Changed; nothing was sent." });
    }
}

internal sealed class CalendarDelete : OutlookTool
{
    public CalendarDelete(IOutlook o, OutlookSettings s) : base(o, s) { }
    protected override Rights Need => Rights.Delete;
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_delete", Class = RiskClass.D, Untrusted = UntrustedMode.Never,
        Description = "Delete one of the user's own events without attendees (to Deleted Items, nothing is sent; one occurrence of a series). Needs the Delete box of the Outlook connector.",
        InputSchema = Schema.Obj(("id", "string", "Event id", true)),
    };
    public override ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        OutlookTools.Require(S, Need);
        // The ticked Delete box is the permission (as for fs_delete): no prompt, also on the managed profile.
        return new ActionPlan { Tool = Descriptor.Name, Class = RiskClass.D, PermittedByUser = true, Summary = "delete calendar event" }
            .Also(p => p.Scopes.Add(new Scope(ScopeKind.App, "outlook")));
    }
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string id = Args.Str(a, "id");
        var ev = O.GetEvent(id);
        if (!ev.Own) throw new ToolException("not_own_event", "Only the user's own events without attendees can be deleted; decline an invitation with calendar_respond.");
        O.DeleteEvent(id);
        return Out(new { deleted = id, subject = ev.Subject, start = OutlookTools.Local(ev.StartUtc), note = "Moved to Deleted Items; nothing was sent." });
    }
}

internal sealed class CalendarRespond : OutlookTool
{
    private static readonly string[] Responses = { "accept", "tentative", "decline" };
    private static readonly string[] Invited = { "notResponded", "tentative", "accepted", "declined" };
    public CalendarRespond(IOutlook o, OutlookSettings s) : base(o, s) { }
    public override ToolDescriptor Descriptor { get; } = new()
    {
        Name = "calendar_respond", Class = RiskClass.W, Untrusted = UntrustedMode.Never,
        Description = "Answer an invitation from someone else: accept, tentative or decline (decline moves the event to Deleted Items). The organizer is mailed only with send=true (external action).",
        InputSchema = Schema.Obj(("id", "string", "Event id", true), ("response", "string", "accept|tentative|decline", true),
            ("send", "boolean", "Mail the answer to the organizer, default false", false), ("comment", "string", "Message to the organizer (needs send=true)", false)),
    };
    private static string Response(JsonElement a)
    {
        string r = Args.Str(a, "response");
        return Responses.FirstOrDefault(v => v.Equals(r, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException("invalid_args", "response: accept | tentative | decline.");
    }
    public override ActionPlan Plan(JsonElement a, ToolEnv env)
    {
        OutlookTools.Require(S, Need);
        bool send = Args.OptBool(a, "send", false);
        string summary = $"{Response(a)} invitation" + (send ? " and mail the answer to the organizer" : "");
        return OutlookTools.Plan(Descriptor.Name, send ? RiskClass.E : RiskClass.W, summary);
    }
    protected override Task<ToolOutput> Run(JsonElement a, ToolEnv env, CancellationToken ct)
    {
        string id = Args.Str(a, "id"), response = Response(a);
        bool send = Args.OptBool(a, "send", false);
        string? comment = Args.OptStr(a, "comment");
        if (!string.IsNullOrEmpty(comment) && !send) throw new ToolException("invalid_args", "A comment is mailed to the organizer: pass send=true.");
        var ev = O.GetEvent(id);
        if (!Invited.Contains(ev.Response)) throw new ToolException("not_an_invitation", "This event is not an invitation from someone else.");
        // Outlook may give an answered invitation a new id; a declined one leaves the calendar ("").
        string newId = O.RespondEvent(id, response, send, string.IsNullOrEmpty(comment) ? null : comment);
        return Out(new { id = newId, response, sent = send, note = send ? "Answer sent to the organizer." : "Answer saved in the calendar; the organizer was not notified." });
    }
}
