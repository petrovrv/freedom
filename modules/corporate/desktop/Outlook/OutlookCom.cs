// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using CO.Core.Tools;

namespace CO.Corporate.Outlook;

// Classic Outlook through its COM object model (TZ §7.3), late-bound, on one STA thread. CreateInstance attaches to
// the running Outlook (it is a single-instance server) or starts it with the default profile.
public sealed class OutlookCom : IOutlook, IDisposable
{
    private const int olFolderDeletedItems = 3, olFolderSentMail = 5, olFolderInbox = 6, olFolderCalendar = 9, olFolderDrafts = 16, olFolderJunk = 23;
    private const int olMail = 0, olMailItemClass = 43, olFormatHTML = 2;
    private const int olAppointmentItem = 1, olAppointmentClass = 26, olRequired = 1, olApptOccurrence = 2, olApptException = 3;
    private const int olNonMeeting = 0, olMeeting = 1, olMeetingReceived = 3;
    private const int olResponseOrganized = 1, olResponseTentative = 2, olResponseAccepted = 3, olResponseDeclined = 4;
    private const int olMeetingTentative = 2, olMeetingAccepted = 3, olMeetingDeclined = 4;
    private const string OccurrenceFormat = "yyyyMMdd'T'HHmm";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private dynamic? _app;

    public OutlookCom()
    {
        _thread = new Thread(() => { foreach (var a in _queue.GetConsumingEnumerable()) a(); }) { IsBackground = true, Name = "Outlook COM" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dispose() => _queue.CompleteAdding();

    private T Sta<T>(Func<dynamic, T> f)
    {
        var done = new TaskCompletionSource<T>();
        _queue.Add(() =>
        {
            try { done.SetResult(f(Namespace())); }
            catch (ToolException e) { done.SetException(e); }
            catch (COMException e) { _app = null; done.SetException(new ToolException("outlook_error", "Outlook: " + e.Message)); }
            catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or ArgumentException)
            { done.SetException(new ToolException("outlook_error", "Outlook: " + e.Message)); }
        });
        return done.Task.GetAwaiter().GetResult();
    }

    private dynamic Namespace()
    {
        if (_app != null)
        {
            try { return _app.GetNamespace("MAPI"); } catch (COMException) { _app = null; }
        }
        Type? t = Type.GetTypeFromProgID("Outlook.Application");
        if (t == null) throw Unsupported();
        try { _app = Activator.CreateInstance(t); }
        catch (COMException) { throw Unsupported(); }
        return _app!.GetNamespace("MAPI");
    }

    private static ToolException Unsupported() => new("unsupported_client",
        "Classic Outlook is not available on this computer. New Outlook has no automation interface: in Outlook switch " +
        "'New Outlook' off (classic Outlook), keep it running, and retry.");

    // ------------------------------------------------------------------ folders

    private static readonly (string alias, int id)[] Aliases =
    {
        ("Inbox", olFolderInbox), ("Sent Items", olFolderSentMail), ("Sent", olFolderSentMail), ("Drafts", olFolderDrafts),
        ("Deleted Items", olFolderDeletedItems), ("Junk Email", olFolderJunk),
    };

    // "Inbox/Projects/X": the first segment may be an English alias of a default folder (names are localized).
    private static dynamic Folder(dynamic ns, string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ns.GetDefaultFolder(olFolderInbox);
        dynamic root = ns.GetDefaultFolder(olFolderInbox).Parent;
        dynamic? cur = null;
        foreach (var (alias, id) in Aliases)
            if (alias.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) { cur = ns.GetDefaultFolder(id); break; }
        cur ??= Child(root, parts[0]);
        for (int i = 1; i < parts.Length; i++) cur = Child(cur, parts[i]);
        return cur!;
    }

    private static dynamic Child(dynamic parent, string name)
    {
        foreach (dynamic f in parent.Folders)
            if (((string)f.Name).Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        throw new ToolException("not_found", $"Outlook folder '{name}' not found; see outlook_folders.");
    }

    private static string PathOf(dynamic folder)
    {
        try { return ((string)folder.FolderPath).TrimStart('\\').Split('\\', 2) is { Length: 2 } p ? p[1].Replace('\\', '/') : (string)folder.Name; }
        catch (COMException) { return (string)folder.Name; }
    }

    public IReadOnlyList<string> Folders() => Sta(ns =>
    {
        var list = new List<string>();
        void Walk(dynamic f, string prefix, int depth)
        {
            foreach (dynamic c in f.Folders)
            {
                if ((int)c.DefaultItemType != olMail) continue;
                string p = prefix.Length == 0 ? (string)c.Name : prefix + "/" + (string)c.Name;
                list.Add(p);
                if (depth < 4 && list.Count < 1000) Walk(c, p, depth + 1);
            }
        }
        Walk(ns.GetDefaultFolder(olFolderInbox).Parent, "", 1);
        return (IReadOnlyList<string>)list;
    });

    // ------------------------------------------------------------------ mail

    private static string Like(string s) => s.Replace("'", "''").Replace("%", "[%]");
    private static string Dasl(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public IReadOnlyList<MailSummary> Search(MailQuery q) => Sta(ns =>
    {
        var cond = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.From))
            cond.Add($"(\"urn:schemas:httpmail:fromname\" LIKE '%{Like(q.From)}%' OR \"urn:schemas:httpmail:fromemail\" LIKE '%{Like(q.From)}%')");
        if (!string.IsNullOrWhiteSpace(q.Subject)) cond.Add($"\"urn:schemas:httpmail:subject\" LIKE '%{Like(q.Subject)}%'");
        if (!string.IsNullOrWhiteSpace(q.Text))
            cond.Add($"(\"urn:schemas:httpmail:subject\" LIKE '%{Like(q.Text)}%' OR \"urn:schemas:httpmail:textdescription\" LIKE '%{Like(q.Text)}%')");
        if (q.SinceUtc is { } since) cond.Add($"\"urn:schemas:httpmail:datereceived\" >= '{Dasl(since)}'");
        if (q.UntilUtc is { } until) cond.Add($"\"urn:schemas:httpmail:datereceived\" < '{Dasl(until)}'");
        if (q.HasAttachments == true) cond.Add("\"urn:schemas:httpmail:hasattachment\" = 1");
        if (q.Unread == true) cond.Add("\"urn:schemas:httpmail:read\" = 0");

        var roots = new List<dynamic>();
        if (string.IsNullOrWhiteSpace(q.Folder))
        {
            roots.Add(ns.GetDefaultFolder(olFolderInbox));
            roots.Add(ns.GetDefaultFolder(olFolderSentMail));
            try { roots.Add(Child(ns.GetDefaultFolder(olFolderInbox).Parent, "Archive")); } catch (ToolException) { }
        }
        else roots.Add(Folder(ns, q.Folder));
        var folders = new List<dynamic>();
        void Add(dynamic f, int depth)
        {
            folders.Add(f);
            if (!q.Subfolders || depth >= 5 || folders.Count >= 300) return;
            foreach (dynamic c in f.Folders)
                if ((int)c.DefaultItemType == olMail) Add(c, depth + 1);
        }
        foreach (dynamic r in roots) Add(r, 1);
        var found = new List<MailSummary>();
        foreach (dynamic f in folders)
        {
            dynamic items = f.Items;
            items.Sort("[ReceivedTime]", true);
            dynamic hits = cond.Count == 0 ? items : items.Restrict("@SQL=" + string.Join(" AND ", cond));
            string fp = PathOf(f);
            int n = 0;
            foreach (dynamic m in hits)
            {
                if ((int)m.Class != olMailItemClass) continue;
                found.Add(Summary(m, fp));
                if (++n >= q.Max) break;
            }
        }
        return (IReadOnlyList<MailSummary>)found.OrderByDescending(m => m.ReceivedUtc).Take(q.Max).ToList();
    });

    private static MailSummary Summary(dynamic m, string folder) => new(
        (string)m.EntryID, folder, Utc(m.ReceivedTime), From(m), (string)(m.Subject ?? ""), (int)m.Attachments.Count, (bool)m.UnRead,
        (string)(m.Categories ?? ""), (string)(m.FlagRequest ?? ""));

    // Distribution-size recipient lists: the first 15 names and a count.
    private static string Short(string list)
    {
        var parts = list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length <= 15 ? string.Join("; ", parts) : string.Join("; ", parts.Take(15)) + $"; … (+{parts.Length - 15} more)";
    }

    private static DateTime? Utc(object? local) => local is DateTime d && d.Year > 1900 ? d.ToUniversalTime() : null;

    private static string From(dynamic m)
    {
        string name = (string)(m.SenderName ?? "");
        string addr = (string)(m.SenderEmailAddress ?? "");
        try
        {
            if ((string)m.SenderEmailType == "EX" && m.Sender?.GetExchangeUser() is { } u) addr = (string)u.PrimarySmtpAddress;
        }
        catch (COMException) { }
        return addr.Length > 0 && !addr.StartsWith("/O=", StringComparison.OrdinalIgnoreCase) ? $"{name} <{addr}>" : name;
    }

    private static dynamic Item(dynamic ns, string id)
    {
        try { return ns.GetItemFromID(id); }
        catch (COMException) { throw new ToolException("not_found", "Mail not found (it may have been moved; search again)."); }
    }

    public MailMessage Read(string id) => Sta(ns =>
    {
        dynamic m = Item(ns, id);
        var atts = new List<MailAttachment>();
        for (int i = 1; i <= (int)m.Attachments.Count; i++)
        {
            dynamic a = m.Attachments.Item(i);
            atts.Add(new MailAttachment(i, (string)a.FileName, (long)(int)a.Size));
        }
        return new MailMessage((string)m.EntryID, PathOf(m.Parent), Utc(m.ReceivedTime), From(m), Short((string)(m.To ?? "")), Short((string)(m.CC ?? "")),
            (string)(m.Subject ?? ""), (string)(m.Body ?? ""), atts, (string)(m.Categories ?? ""));
    });

    public IReadOnlyList<string> SaveAttachments(string id, IReadOnlyCollection<string>? names, string targetDir) => Sta(ns =>
    {
        dynamic m = Item(ns, id);
        var saved = new List<string>();
        for (int i = 1; i <= (int)m.Attachments.Count; i++)
        {
            dynamic a = m.Attachments.Item(i);
            string name = (string)a.FileName;
            if (names != null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (safe.Length == 0) safe = $"attachment-{i}";
            string file = safe;
            for (int n = 2; File.Exists(Path.Combine(targetDir, file)); n++)
                file = Path.GetFileNameWithoutExtension(safe) + $" ({n})" + Path.GetExtension(safe);
            a.SaveAsFile(Path.Combine(targetDir, file));
            saved.Add(file);
        }
        if (saved.Count == 0) throw new ToolException("not_found", "No matching attachments.");
        return (IReadOnlyList<string>)saved;
    });

    public string CreateDraft(DraftRequest d) => Sta(ns =>
    {
        dynamic mail;
        if (d.ReplyToId != null)
        {
            dynamic original = Item(ns, d.ReplyToId);
            mail = d.ReplyAll ? original.ReplyAll() : original.Reply();
            if ((int)mail.BodyFormat == olFormatHTML)
            {
                string html = (string)mail.HTMLBody;
                string para = "<div>" + string.Join("<br>", d.Body.Split('\n').Select(l => WebUtility.HtmlEncode(l.TrimEnd('\r')))) + "</div><br>";
                int at = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
                int gt = at >= 0 ? html.IndexOf('>', at) : -1;
                mail.HTMLBody = gt >= 0 ? html.Insert(gt + 1, para) : para + html;
            }
            else mail.Body = d.Body + "\r\n\r\n" + (string)mail.Body;
        }
        else
        {
            mail = ns.Application.CreateItem(olMail);
            mail.Subject = d.Subject;
            mail.Body = d.Body;
        }
        if (!string.IsNullOrWhiteSpace(d.To)) mail.To = d.To;
        if (!string.IsNullOrWhiteSpace(d.Cc)) mail.CC = d.Cc;
        foreach (string p in d.AttachPaths) mail.Attachments.Add(p);
        mail.Save();
        return (string)mail.EntryID;
    });

    public string Move(string id, string folder) => Sta(ns =>
    {
        dynamic moved = Item(ns, id).Move(Folder(ns, folder));
        return (string)moved.EntryID;
    });

    public void SetCategories(string id, string categories) => Sta<bool>(ns =>
    {
        dynamic m = Item(ns, id);
        m.Categories = categories;
        m.Save();
        return true;
    });

    public void SetFlag(string id, string flag) => Sta<bool>(ns =>
    {
        dynamic m = Item(ns, id);
        if (flag == "clear") m.ClearTaskFlag();
        else if (flag == "complete") { m.MarkAsTask(4); m.TaskCompletedDate = DateTime.Now; m.FlagStatus = 1; }
        else m.MarkAsTask(4);   // olMarkNoDate
        m.Save();
        return true;
    });

    // ------------------------------------------------------------------ calendar

    public IReadOnlyList<CalendarEvent> Calendar(DateTime fromUtc, DateTime toUtc) => Sta(ns =>
    {
        dynamic items = ns.GetDefaultFolder(olFolderCalendar).Items;
        items.Sort("[Start]");
        items.IncludeRecurrences = true;
        // Jet filters take local times in the user's short date format.
        string f = fromUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), t = toUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        dynamic hits = items.Restrict($"[Start] < '{t}' AND [End] > '{f}'");
        var list = new List<CalendarEvent>();
        Me me = CurrentMe(ns);
        foreach (dynamic e in hits)
        {
            list.Add((CalendarEvent)ToEvent(e, (string)e.EntryID, me));
            if (list.Count >= 500) break;
        }
        return (IReadOnlyList<CalendarEvent>)list;
    });

    public CalendarEvent GetEvent(string id) => Sta<CalendarEvent>(ns =>
    {
        Me me = CurrentMe(ns);
        return (CalendarEvent)ToEvent(Event(ns, id), EntryPart(id), me);
    });

    public string CreateEvent(EventRequest r) => Sta<string>(ns =>
    {
        dynamic a = ns.Application.CreateItem(olAppointmentItem);
        a.Subject = r.Subject;
        a.AllDayEvent = r.AllDay;
        a.Start = r.StartUtc.ToLocalTime();
        a.End = r.EndUtc.ToLocalTime();
        if (r.Location != null) a.Location = r.Location;
        if (r.Body != null) a.Body = r.Body;
        a.BusyStatus = BusyCode(r.BusyStatus);
        SetReminder(a, r.ReminderMinutes);

        var attendees = r.Attendees.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        if (attendees.Count == 0)
        {
            // A plain appointment: MeetingStatus stays olNonMeeting, nothing is sent.
            a.Save();
            return (string)a.EntryID;
        }

        a.MeetingStatus = olMeeting;
        foreach (string addr in attendees)
        {
            dynamic rc = a.Recipients.Add(addr);
            rc.Type = olRequired;
        }
        if (!(bool)a.Recipients.ResolveAll())
        {
            var bad = new List<string>();
            foreach (dynamic rc in a.Recipients)
                if (!(bool)rc.Resolved) bad.Add((string)(rc.Name ?? ""));
            throw new ToolException("invalid_args", "Unresolved attendee: " + string.Join("; ", bad));
        }
        a.Save();
        string id = (string)a.EntryID;
        a.Send();
        return id;
    });

    public string UpdateEvent(string id, EventChange c) => Sta<string>(ns =>
    {
        dynamic a = Event(ns, id);
        if (c.Subject != null) a.Subject = c.Subject;
        // Setting Start keeps the duration (Outlook moves End); an explicit End is applied afterwards.
        if (c.StartUtc is DateTime s) a.Start = s.ToLocalTime();
        if (c.EndUtc is DateTime e) a.End = e.ToLocalTime();
        if (c.Location != null) a.Location = c.Location;
        if (c.Body != null) a.Body = c.Body;
        if (c.ReminderMinutes != null) SetReminder(a, c.ReminderMinutes);
        if (c.BusyStatus != null) a.BusyStatus = BusyCode(c.BusyStatus);
        a.Save();
        // An occurrence keeps the series master's EntryID; its id follows the (possibly new) local start.
        return (string)EventId(EntryPart(id), a);
    });

    public void DeleteEvent(string id) => Sta<bool>(ns =>
    {
        dynamic a = Event(ns, id);
        a.Delete();
        return true;
    });

    public string RespondEvent(string id, string response, bool send, string? comment) => Sta<string>(ns =>
    {
        int code = response switch
        {
            "accept" => olMeetingAccepted,
            "tentative" => olMeetingTentative,
            "decline" => olMeetingDeclined,
            _ => throw new ToolException("invalid_args", "response: accept | tentative | decline."),
        };
        dynamic a = Event(ns, id);
        if ((int)a.MeetingStatus != olMeetingReceived)
            throw new ToolException("not_an_invitation", "Only a meeting invitation from someone else can be answered.");

        dynamic reply = a.Respond(code, true /*fNoUI*/, false /*fAdditionalTextDialog*/);
        if (send && reply is not null)
        {
            if (!string.IsNullOrEmpty(comment)) reply.Body = comment + "\r\n\r\n" + (string)(reply.Body ?? "");
            reply.Send();
            return code == olMeetingDeclined ? "" : (string)CurrentId(a, id);
        }
        // Not sent: the unsent reply is removed so it is not left in Drafts or the Outbox; the answer stays on the calendar item.
        if (reply is not null) reply.Delete();
        if (code != olMeetingDeclined) { a.Save(); return (string)CurrentId(a, id); }
        // A declined invitation leaves the calendar (Outlook does that itself only when the reply is sent).
        try { a.Delete(); } catch (COMException) { }
        return "";
    });

    // Outlook may re-key the item after an answer; read the id again, falling back to the one given.
    private static string CurrentId(dynamic a, string fallback)
    {
        try { return (string)EventId((string)a.EntryID, a); }
        catch (COMException) { return fallback; }
    }

    // An event id to the AppointmentItem: the single item, or exactly one occurrence of a series.
    private static dynamic Event(dynamic ns, string id)
    {
        string entry = EntryPart(id);
        dynamic item;
        try { item = ns.GetItemFromID(entry); }
        catch (COMException) { throw EventNotFound(); }
        if ((int)item.Class != olAppointmentClass) throw EventNotFound();

        int bar = id.IndexOf('|');
        if (bar < 0)
        {
            // Never act on a whole series.
            if ((bool)item.IsRecurring)
                throw new ToolException("invalid_args", "This id is a whole recurring series; use calendar_find to get the id of one occurrence.");
            return item;
        }

        if (!DateTime.TryParseExact(id[(bar + 1)..], OccurrenceFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start)
            || !(bool)item.IsRecurring)
            throw EventNotFound();

        // Candidates are matched on their current start: live exceptions, plus the unmodified occurrence at that slot.
        dynamic pattern = item.GetRecurrencePattern();
        var hits = new List<dynamic>();
        foreach (dynamic x in pattern.Exceptions)
        {
            if ((bool)x.Deleted) continue;
            dynamic moved;
            try { moved = x.AppointmentItem; } catch (COMException) { continue; }
            if (ToMinute((DateTime)moved.Start) == start) hits.Add(moved);
        }
        try
        {
            dynamic occ = pattern.GetOccurrence(start);
            if ((int)occ.RecurrenceState == olApptOccurrence) hits.Add(occ);
        }
        catch (COMException) { }

        if (hits.Count == 0) throw EventNotFound();
        if (hits.Count > 1)
            throw new ToolException("ambiguous_event", "Two occurrences of this series start at the same time; change it in Outlook.");
        return hits[0];
    }

    private static string EntryPart(string id)
    {
        int bar = id.IndexOf('|');
        return bar < 0 ? id : id[..bar];
    }

    private static ToolException EventNotFound() =>
        new("not_found", "Calendar event not found (it may have changed; use calendar_find again).");

    private static DateTime ToMinute(DateTime d) => d.AddTicks(-(d.Ticks % TimeSpan.TicksPerMinute));

    private sealed record Me(string Name, string Address, string Smtp);

    private static Me CurrentMe(dynamic ns)
    {
        string name = "", address = "", smtp = "";
        dynamic? u = null;
        try { u = ns.CurrentUser; } catch (COMException) { }
        if (u is not null)
        {
            try { name = (string)(u.Name ?? ""); } catch (COMException) { }
            try { address = (string)(u.Address ?? ""); } catch (COMException) { }
            try { smtp = (string)(u.AddressEntry?.GetExchangeUser()?.PrimarySmtpAddress ?? ""); } catch (COMException) { }
        }
        return new Me(name, address, smtp);
    }

    private static bool Same(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsMe(dynamic r, Me me)
    {
        try { if (Same((string)(r.Address ?? ""), me.Address)) return true; } catch (COMException) { }
        try { if (Same((string)(r.Name ?? ""), me.Name)) return true; } catch (COMException) { }
        if (me.Smtp.Length > 0)
        {
            try { if (Same((string)(r.AddressEntry?.GetExchangeUser()?.PrimarySmtpAddress ?? ""), me.Smtp)) return true; }
            catch (COMException) { }
        }
        return false;
    }

    // Own: a plain appointment, or a meeting whose every recipient (none counts as own) is the current user.
    private static bool IsOwn(dynamic e, int ms, Me me)
    {
        if (ms == olNonMeeting) return true;
        if (ms != olMeeting) return false;
        foreach (dynamic r in e.Recipients)
            if (!(bool)IsMe(r, me)) return false;
        return true;
    }

    private static CalendarEvent ToEvent(dynamic e, string entryId, Me me)
    {
        int ms = (int)e.MeetingStatus;
        return new CalendarEvent(((DateTime)e.Start).ToUniversalTime(), ((DateTime)e.End).ToUniversalTime(), (string)(e.Subject ?? ""),
            (string)(e.Location ?? ""), (string)(e.Organizer ?? ""), (bool)e.AllDayEvent, Busy((int)e.BusyStatus),
            Short((string)(e.RequiredAttendees ?? "")),
            (string)EventId(entryId, e), Response(ms, (int)e.ResponseStatus),
            (bool)IsOwn(e, ms, me),
            (bool)IsOccurrence(e));
    }

    private static bool IsOccurrence(dynamic e) =>
        (bool)e.IsRecurring && (int)e.RecurrenceState is olApptOccurrence or olApptException;

    private static string EventId(string entryId, dynamic e) =>
        (bool)IsOccurrence(e) ? entryId + "|" + ((DateTime)e.Start).ToString(OccurrenceFormat, CultureInfo.InvariantCulture) : entryId;

    private static string Response(int meetingStatus, int responseStatus) => meetingStatus == olNonMeeting ? "none" : responseStatus switch
    {
        olResponseOrganized => "organizer",
        olResponseTentative => "tentative",
        olResponseAccepted => "accepted",
        olResponseDeclined => "declined",
        _ => "notResponded",   // olResponseNone / olResponseNotResponded on a meeting
    };

    private static void SetReminder(dynamic a, int? minutes)
    {
        if (minutes is > 0)
        {
            a.ReminderSet = true;
            a.ReminderMinutesBeforeStart = minutes.Value;
        }
        else a.ReminderSet = false;
    }

    private static int BusyCode(string s) => s switch
    {
        "free" => 0, "tentative" => 1, "busy" => 2, "outOfOffice" => 3, "workingElsewhere" => 4,
        _ => throw new ToolException("invalid_args", "busy: free | tentative | busy | outOfOffice | workingElsewhere."),
    };

    private static string Busy(int s) => s switch { 0 => "free", 1 => "tentative", 2 => "busy", 3 => "outOfOffice", 4 => "workingElsewhere", _ => "busy" };
}
