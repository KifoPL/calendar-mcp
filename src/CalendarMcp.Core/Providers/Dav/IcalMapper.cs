using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CalendarMcp.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using IcalCalendar = Ical.Net.Calendar;
using IcsCalendarEvent = Ical.Net.CalendarComponents.CalendarEvent;
using ModelCalendarEvent = CalendarMcp.Core.Models.CalendarEvent;

namespace CalendarMcp.Core.Providers.Dav;

/// <summary>
/// Maps between Ical.Net VEVENT components and Adjutant's <see cref="ModelCalendarEvent"/>.
/// </summary>
public static class IcalMapper
{
    public static ModelCalendarEvent ToCalendarEvent(
        IcsCalendarEvent icsEvent,
        string accountId,
        string calendarId,
        string eventId,
        Occurrence? occurrence = null)
    {
        DateTimeOffset evtStart, evtEnd;

        if (occurrence is not null)
        {
            evtStart = new DateTimeOffset(occurrence.Period.StartTime.AsUtc, TimeSpan.Zero);
            var occEnd = occurrence.Period.EndTime?.AsUtc
                         ?? occurrence.Period.StartTime.AsUtc
                         + (icsEvent.DtEnd?.AsUtc - icsEvent.DtStart?.AsUtc ?? TimeSpan.FromHours(1));
            evtEnd = new DateTimeOffset(occEnd, TimeSpan.Zero);
        }
        else
        {
            evtStart = icsEvent.DtStart != null
                ? new DateTimeOffset(icsEvent.DtStart.AsUtc, TimeSpan.Zero)
                : DateTimeOffset.MinValue;
            evtEnd = icsEvent.DtEnd != null
                ? new DateTimeOffset(icsEvent.DtEnd.AsUtc, TimeSpan.Zero)
                : evtStart;
        }

        var attendees = new List<string>();
        var attendeeDetails = new List<EventAttendee>();
        foreach (var att in icsEvent.Attendees)
        {
            var email = ExtractMailto(att.Value);
            if (string.IsNullOrEmpty(email))
                continue;
            attendees.Add(email);
            attendeeDetails.Add(new EventAttendee
            {
                Email = email,
                Name = att.CommonName ?? string.Empty,
                ResponseStatus = MapPartStat(att.ParticipationStatus),
                Type = att.Role?.ToUpperInvariant() switch
                {
                    "REQ-PARTICIPANT" => "required",
                    "OPT-PARTICIPANT" => "optional",
                    "NON-PARTICIPANT" => "resource",
                    _ => "required"
                }
            });
        }

        return new ModelCalendarEvent
        {
            Id = eventId,
            AccountId = accountId,
            CalendarId = calendarId,
            Subject = icsEvent.Summary ?? string.Empty,
            Start = evtStart,
            End = evtEnd,
            Location = icsEvent.Location ?? string.Empty,
            Body = icsEvent.Description ?? string.Empty,
            BodyFormat = "text",
            Organizer = ExtractMailto(icsEvent.Organizer?.Value),
            OrganizerName = icsEvent.Organizer?.CommonName ?? string.Empty,
            Attendees = attendees,
            AttendeeDetails = attendeeDetails,
            IsAllDay = icsEvent.IsAllDay,
            ShowAs = icsEvent.Transparency == TransparencyType.Transparent ? "free" : "busy",
            Sensitivity = icsEvent.Class?.ToUpperInvariant() switch
            {
                "PRIVATE" => "private",
                "CONFIDENTIAL" => "confidential",
                _ => "normal"
            },
            IsCancelled = icsEvent.Status?.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true,
            IsRecurring = icsEvent.RecurrenceRules?.Count > 0 || icsEvent.RecurrenceDates?.Count > 0,
            RecurrencePattern = icsEvent.RecurrenceRules?.FirstOrDefault()?.ToString(),
            Categories = icsEvent.Categories?.ToList() ?? [],
            CreatedDateTime = icsEvent.Created?.AsUtc,
            LastModifiedDateTime = icsEvent.LastModified?.AsUtc
        };
    }

    public static IEnumerable<(IcsCalendarEvent Master, Occurrence? Occurrence)> ExpandInRange(
        IcalCalendar calendar, DateTime start, DateTime end)
    {
        foreach (var evt in calendar.Events)
        {
            if (evt.RecurrenceRules?.Count > 0 || evt.RecurrenceDates?.Count > 0)
            {
                foreach (var occurrence in evt.GetOccurrences(new CalDateTime(start), new CalDateTime(end)))
                    yield return (evt, occurrence);
            }
            else
            {
                var evtStart = evt.DtStart?.AsUtc ?? DateTime.MinValue;
                var evtEnd = evt.DtEnd?.AsUtc ?? evtStart;
                if (evtStart < end && evtEnd > start)
                    yield return (evt, null);
            }
        }
    }

    public static string BuildNewEventIcs(
        string uid,
        string subject,
        DateTime start,
        DateTime end,
        string? location,
        IEnumerable<string>? attendees,
        string? body,
        string? timeZone,
        bool isAllDay = false)
    {
        var calendar = new IcalCalendar();
        var evt = new IcsCalendarEvent
        {
            Uid = uid,
            Summary = subject,
            Location = location,
            Description = body,
            DtStamp = new CalDateTime(DateTime.UtcNow)
        };

        if (isAllDay)
        {
            evt.DtStart = new CalDateTime(start.Date);
            evt.DtEnd = new CalDateTime(end.Date);
            evt.IsAllDay = true;
        }
        else if (!string.IsNullOrWhiteSpace(timeZone))
        {
            evt.DtStart = new CalDateTime(start, timeZone);
            evt.DtEnd = new CalDateTime(end, timeZone);
        }
        else
        {
            evt.DtStart = new CalDateTime(DateTime.SpecifyKind(start, DateTimeKind.Utc));
            evt.DtEnd = new CalDateTime(DateTime.SpecifyKind(end, DateTimeKind.Utc));
        }

        if (attendees is not null)
        {
            foreach (var email in attendees.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                evt.Attendees.Add(new Attendee($"mailto:{email.Trim()}")
                {
                    Role = "REQ-PARTICIPANT",
                    ParticipationStatus = "NEEDS-ACTION",
                    Rsvp = true
                });
            }
        }

        calendar.Events.Add(evt);
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    public static string ApplyUpdates(
        string existingIcs,
        string? subject,
        DateTime? start,
        DateTime? end,
        string? location,
        List<string>? attendees,
        string? timeZone)
    {
        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("Failed to parse existing calendar data.");
        var evt = calendar.Events.FirstOrDefault()
            ?? throw new InvalidOperationException("Existing calendar object has no VEVENT.");

        if (subject is not null)
            evt.Summary = subject;
        if (location is not null)
            evt.Location = location;
        if (start is not null)
        {
            evt.DtStart = string.IsNullOrWhiteSpace(timeZone)
                ? new CalDateTime(DateTime.SpecifyKind(start.Value, DateTimeKind.Utc))
                : new CalDateTime(start.Value, timeZone);
        }
        if (end is not null)
        {
            evt.DtEnd = string.IsNullOrWhiteSpace(timeZone)
                ? new CalDateTime(DateTime.SpecifyKind(end.Value, DateTimeKind.Utc))
                : new CalDateTime(end.Value, timeZone);
        }
        if (attendees is not null)
        {
            evt.Attendees.Clear();
            foreach (var email in attendees.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                evt.Attendees.Add(new Attendee($"mailto:{email.Trim()}")
                {
                    Role = "REQ-PARTICIPANT",
                    ParticipationStatus = "NEEDS-ACTION",
                    Rsvp = true
                });
            }
        }

        evt.LastModified = new CalDateTime(DateTime.UtcNow);
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    public static string ApplyPartStat(string existingIcs, string attendeeEmail, string response)
    {
        var calendar = IcalCalendar.Load(existingIcs)
            ?? throw new InvalidOperationException("Failed to parse existing calendar data.");
        var evt = calendar.Events.FirstOrDefault()
            ?? throw new InvalidOperationException("Existing calendar object has no VEVENT.");

        var partStat = response.ToLowerInvariant() switch
        {
            "accepted" => "ACCEPTED",
            "tentative" => "TENTATIVE",
            "declined" => "DECLINED",
            _ => throw new ArgumentException($"Unsupported response '{response}'.", nameof(response))
        };

        var attendee = evt.Attendees.FirstOrDefault(a =>
            string.Equals(ExtractMailto(a.Value), attendeeEmail, StringComparison.OrdinalIgnoreCase));
        if (attendee is null)
        {
            attendee = new Attendee($"mailto:{attendeeEmail}")
            {
                Role = "REQ-PARTICIPANT",
                Rsvp = true
            };
            evt.Attendees.Add(attendee);
        }

        attendee.ParticipationStatus = partStat;
        evt.LastModified = new CalDateTime(DateTime.UtcNow);
        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    public static IcalCalendar? LoadCalendar(string? ics) =>
        string.IsNullOrWhiteSpace(ics) ? null : IcalCalendar.Load(ics);

    private static string MapPartStat(string? partStat) => partStat?.ToUpperInvariant() switch
    {
        "ACCEPTED" => "accepted",
        "TENTATIVE" => "tentative",
        "DECLINED" => "declined",
        "NEEDS-ACTION" => "notResponded",
        _ => "notResponded"
    };

    private static string ExtractMailto(Uri? uri)
    {
        if (uri is null)
            return string.Empty;

        if (uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
        {
            var s = uri.OriginalString;
            var idx = s.IndexOf(':');
            return idx >= 0 ? Uri.UnescapeDataString(s[(idx + 1)..]) : uri.AbsolutePath;
        }

        return uri.Authority ?? uri.ToString();
    }
}

/// <summary>
/// Minimal vCard 3.0/4.0 mapper covering fields already on <see cref="Contact"/>.
/// </summary>
public static partial class VCardMapper
{
    public static Contact ToContact(string vcard, string accountId, string contactId, string? etag)
    {
        var lines = Unfold(vcard);
        var emails = new List<ContactEmail>();
        var phones = new List<ContactPhone>();
        var addresses = new List<ContactAddress>();
        string displayName = "", given = "", surname = "", job = "", company = "", notes = "";
        DateTime? birthday = null;

        foreach (var line in lines)
        {
            var (name, paramsPart, value) = SplitLine(line);
            var upper = name.ToUpperInvariant();

            switch (upper)
            {
                case "FN":
                    displayName = Unescape(value);
                    break;
                case "N":
                    var parts = value.Split(';');
                    surname = Unescape(parts.ElementAtOrDefault(0) ?? "");
                    given = Unescape(parts.ElementAtOrDefault(1) ?? "");
                    break;
                case "EMAIL":
                    emails.Add(new ContactEmail
                    {
                        Address = Unescape(value),
                        Label = TypeFromParams(paramsPart) ?? "other"
                    });
                    break;
                case "TEL":
                    phones.Add(new ContactPhone
                    {
                        Number = Unescape(value),
                        Label = TypeFromParams(paramsPart) ?? "other"
                    });
                    break;
                case "TITLE":
                    job = Unescape(value);
                    break;
                case "ORG":
                    company = Unescape(value.Split(';')[0]);
                    break;
                case "NOTE":
                    notes = Unescape(value);
                    break;
                case "BDAY":
                    if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var bd)
                        || DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out bd))
                        birthday = bd.Date;
                    break;
                case "ADR":
                    var adr = value.Split(';');
                    addresses.Add(new ContactAddress
                    {
                        Street = Unescape(adr.ElementAtOrDefault(2) ?? ""),
                        City = Unescape(adr.ElementAtOrDefault(3) ?? ""),
                        State = Unescape(adr.ElementAtOrDefault(4) ?? ""),
                        PostalCode = Unescape(adr.ElementAtOrDefault(5) ?? ""),
                        Country = Unescape(adr.ElementAtOrDefault(6) ?? ""),
                        Label = TypeFromParams(paramsPart) ?? "other"
                    });
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = $"{given} {surname}".Trim();

        return new Contact
        {
            Id = contactId,
            AccountId = accountId,
            DisplayName = displayName,
            GivenName = given,
            Surname = surname,
            EmailAddresses = emails,
            PhoneNumbers = phones,
            JobTitle = job,
            CompanyName = company,
            Notes = notes,
            Addresses = addresses,
            Birthday = birthday,
            Etag = etag
        };
    }

    public static string ToVCard(
        string uid,
        string displayName,
        string? givenName,
        string? surname,
        IEnumerable<string>? emails,
        IEnumerable<string>? phones,
        string? jobTitle,
        string? companyName,
        string? notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCARD");
        sb.AppendLine("VERSION:3.0");
        sb.AppendLine($"UID:{Escape(uid)}");
        sb.AppendLine($"FN:{Escape(displayName)}");
        sb.AppendLine($"N:{Escape(surname ?? "")};{Escape(givenName ?? "")};;;");
        if (emails is not null)
        {
            foreach (var email in emails.Where(e => !string.IsNullOrWhiteSpace(e)))
                sb.AppendLine($"EMAIL;TYPE=INTERNET:{Escape(email.Trim())}");
        }
        if (phones is not null)
        {
            foreach (var phone in phones.Where(p => !string.IsNullOrWhiteSpace(p)))
                sb.AppendLine($"TEL;TYPE=VOICE:{Escape(phone.Trim())}");
        }
        if (!string.IsNullOrWhiteSpace(jobTitle))
            sb.AppendLine($"TITLE:{Escape(jobTitle)}");
        if (!string.IsNullOrWhiteSpace(companyName))
            sb.AppendLine($"ORG:{Escape(companyName)}");
        if (!string.IsNullOrWhiteSpace(notes))
            sb.AppendLine($"NOTE:{Escape(notes)}");
        sb.AppendLine("END:VCARD");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    public static string ApplyUpdates(
        string existingVcard,
        string? displayName,
        string? givenName,
        string? surname,
        List<string>? emails,
        List<string>? phones,
        string? jobTitle,
        string? companyName,
        string? notes)
    {
        var contact = ToContact(existingVcard, accountId: "_", contactId: "_", etag: null);
        var uid = ExtractUid(existingVcard) ?? Guid.NewGuid().ToString("N");

        return ToVCard(
            uid,
            displayName ?? contact.DisplayName,
            givenName ?? contact.GivenName,
            surname ?? contact.Surname,
            emails ?? contact.EmailAddresses.Select(e => e.Address).ToList(),
            phones ?? contact.PhoneNumbers.Select(p => p.Number).ToList(),
            jobTitle ?? contact.JobTitle,
            companyName ?? contact.CompanyName,
            notes ?? contact.Notes);
    }

    private static string? ExtractUid(string vcard)
    {
        foreach (var line in Unfold(vcard))
        {
            var (name, _, value) = SplitLine(line);
            if (name.Equals("UID", StringComparison.OrdinalIgnoreCase))
                return Unescape(value);
        }
        return null;
    }

    private static List<string> Unfold(string vcard)
    {
        var normalized = vcard.Replace("\r\n", "\n").Replace('\r', '\n');
        var raw = normalized.Split('\n');
        var lines = new List<string>();
        foreach (var line in raw)
        {
            if (line.Length == 0)
                continue;
            if ((line[0] is ' ' or '\t') && lines.Count > 0)
                lines[^1] += line[1..];
            else
                lines.Add(line);
        }
        return lines;
    }

    private static (string Name, string Params, string Value) SplitLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0)
            return (line, "", "");

        var left = line[..colon];
        var value = line[(colon + 1)..];
        var semi = left.IndexOf(';');
        if (semi < 0)
            return (left, "", value);
        return (left[..semi], left[(semi + 1)..], value);
    }

    private static string? TypeFromParams(string paramsPart)
    {
        var match = TypeParam().Match(paramsPart);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace(",", "\\,").Replace(";", "\\;");

    private static string Unescape(string value) =>
        value.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    [GeneratedRegex(@"TYPE=([^;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeParam();
}
