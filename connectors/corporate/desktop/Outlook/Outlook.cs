// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace CO.Corporate.Outlook;

// What the user lets the agent do with Outlook (CO Desktop → Connectors → Outlook). Turning it on is the consent:
// results are then released without a prompt, like files of a connected folder.
public sealed class OutlookSettings
{
    public bool Read { get; set; }    // search, read mail, calendar
    public bool Write { get; set; }   // before 0.6.1: drafts, attachments, move, flag, categories; kept = Create || Edit
    public bool? Create { get; set; } // 0.6.1: drafts, saved attachments (null: as Write)
    public bool? Edit { get; set; }   // 0.6.1: move, flag, categories, calendar events (null: as Write)
    public bool? Delete { get; set; } // 0.8.6: the user's own calendar events (mail is never deleted)

    [System.Text.Json.Serialization.JsonIgnore] public bool CanCreate => Create ?? Write;
    [System.Text.Json.Serialization.JsonIgnore] public bool CanEdit => Edit ?? Write;
    [System.Text.Json.Serialization.JsonIgnore] public bool CanDelete => Delete ?? false;

    private string? _file;

    public static OutlookSettings Load(string? file)
    {
        var s = file != null && File.Exists(file) ? JsonSerializer.Deserialize<OutlookSettings>(File.ReadAllText(file)) ?? new() : new();
        s._file = file;
        return s;
    }

    public void Save()
    {
        if (_file == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record MailQuery(string? Folder, string? From, string? Subject, string? Text, DateTime? SinceUtc, DateTime? UntilUtc,
    bool? HasAttachments, bool? Unread, int Max, bool Subfolders = true);

public sealed record MailSummary(string Id, string Folder, DateTime? ReceivedUtc, string From, string Subject,
    int Attachments, bool Unread, string Categories, string Flag);

public sealed record MailAttachment(int Index, string Name, long Size);

public sealed record MailMessage(string Id, string Folder, DateTime? ReceivedUtc, string From, string To, string Cc, string Subject,
    string Body, IReadOnlyList<MailAttachment> Attachments, string Categories);

public sealed record DraftRequest(string? To, string? Cc, string Subject, string Body, string? ReplyToId, bool ReplyAll, IReadOnlyList<string> AttachPaths);

// Id: EntryID, or "<EntryID>|<yyyyMMddTHHmm>" (local start) for one occurrence of a recurring series.
// Response: none (not a meeting) | organizer | accepted | tentative | declined | notResponded.
// Own: not a meeting, or a meeting the user organizes without other recipients — the only events the agent changes or deletes.
public sealed record CalendarEvent(DateTime StartUtc, DateTime EndUtc, string Subject, string Location, string Organizer, bool AllDay,
    string BusyStatus, string Attendees, string Id = "", string Response = "", bool Own = false, bool Recurring = false);

// ReminderMinutes null or <= 0: no reminder. Attendees non-empty: a meeting request is mailed (class E in the tool).
public sealed record EventRequest(string Subject, DateTime StartUtc, DateTime EndUtc, string? Location, string? Body,
    int? ReminderMinutes, string BusyStatus, bool AllDay, IReadOnlyList<string> Attendees);

// null = unchanged; ReminderMinutes <= 0 = reminder off.
public sealed record EventChange(string? Subject, DateTime? StartUtc, DateTime? EndUtc, string? Location, string? Body,
    int? ReminderMinutes, string? BusyStatus);

// Classic Outlook through COM on the device (TZ §7.3). New Outlook has no automation interface: the implementation
// throws ToolException("unsupported_client").
public interface IOutlook
{
    IReadOnlyList<string> Folders();
    IReadOnlyList<MailSummary> Search(MailQuery q);
    MailMessage Read(string id);
    IReadOnlyList<string> SaveAttachments(string id, IReadOnlyCollection<string>? names, string targetDir);
    string CreateDraft(DraftRequest d);
    string Move(string id, string folder);
    void SetCategories(string id, string categories);
    void SetFlag(string id, string flag);   // "flag" | "complete" | "clear"
    IReadOnlyList<CalendarEvent> Calendar(DateTime fromUtc, DateTime toUtc);
    CalendarEvent GetEvent(string id);                       // not_found if missing
    string CreateEvent(EventRequest r);                      // the new id
    string UpdateEvent(string id, EventChange c);            // the id after the change (an occurrence id follows its start)
    void DeleteEvent(string id);                             // to Deleted Items; an occurrence only, never the series
    string RespondEvent(string id, string response, bool send, string? comment);   // accept | tentative | decline; the id after the answer ("" when declined)
}

public static class FreeSlots
{
    // Free intervals of at least `minutes` inside working hours (local time, Mon–Fri), around busy events.
    public static List<(DateTime start, DateTime end)> Find(IEnumerable<CalendarEvent> events, DateTime fromLocal, DateTime toLocal,
        int minutes, int dayStartHour = 9, int dayEndHour = 17, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var busy = events.Where(e => e.BusyStatus is not ("free" or "workingElsewhere"))
            .Select(e => (s: TimeZoneInfo.ConvertTimeFromUtc(e.StartUtc, zone), e: TimeZoneInfo.ConvertTimeFromUtc(e.EndUtc, zone)))
            .OrderBy(b => b.s).ToList();
        var slots = new List<(DateTime, DateTime)>();
        for (var day = fromLocal.Date; day <= toLocal.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var cursor = Max(day.AddHours(dayStartHour), fromLocal);
            var dayEnd = Min(day.AddHours(dayEndHour), toLocal);
            foreach (var b in busy.Where(b => b.e > cursor && b.s < dayEnd))
            {
                if ((b.s - cursor).TotalMinutes >= minutes) slots.Add((cursor, b.s));
                if (b.e > cursor) cursor = b.e;
            }
            if ((dayEnd - cursor).TotalMinutes >= minutes) slots.Add((cursor, dayEnd));
        }
        return slots;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
