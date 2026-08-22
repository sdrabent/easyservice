using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the planned restart. The arithmetic looks trivial until it is midnight, or the
/// chosen weekday is tomorrow, or the machine was switched off through the whole maintenance
/// window - so those are the cases here.
/// </summary>
internal static class ScheduleTests
{
    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("Täglich um drei findet den nächsten Termin", DailyFindsNextOccurrence);
        yield return ("Nach dem Termin zählt der nächste Tag", AfterTimeMovesToTomorrow);
        yield return ("Nur gewählte Wochentage kommen infrage", OnlySelectedWeekdays);
        yield return ("Ein einzelner Wochentag springt eine Woche weiter", SingleWeekdayWrapsAround);
        yield return ("Ein verpasster Termin wird nicht nachgeholt", MissedWindowIsSkipped);
        yield return ("Das Intervall zählt Laufzeit, kein Wanduhr", IntervalCountsUptime);
        yield return ("Ohne Plan ist nie etwas fällig", NoneIsNeverDue);
    }

    private static DateTime Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);

    private static DateTime? NextAtTime(int atMinutes, int days, DateTime after) =>
        RestartSchedule.Next(RestartScheduleMode.AtTime, atMinutes, days, 0, after, null);

    // ------------------------------------------------------------------ Uhrzeit ---

    private static void DailyFindsNextOccurrence()
    {
        // Montag, 22:15 - der nächste Termin ist in derselben Nacht um 03:00.
        var now = Local(2026, 8, 24, 22, 15);
        var next = NextAtTime(3 * 60, RestartSchedule.AllDays, now);

        Assert(next == Local(2026, 8, 25, 3, 0), $"erwartet 25.08. 03:00, geliefert: {next}");
    }

    private static void AfterTimeMovesToTomorrow()
    {
        // Genau auf der Minute gilt der Termin als vergeben: sonst feuert er zweimal.
        var atThree = Local(2026, 8, 25, 3, 0);
        var next = NextAtTime(3 * 60, RestartSchedule.AllDays, atThree);

        Assert(next == Local(2026, 8, 26, 3, 0), $"erwartet 26.08. 03:00, geliefert: {next}");

        // Und eine Minute davor eben nicht.
        var before = NextAtTime(3 * 60, RestartSchedule.AllDays, Local(2026, 8, 25, 2, 59));
        Assert(before == atThree, $"erwartet 25.08. 03:00, geliefert: {before}");
    }

    private static void OnlySelectedWeekdays()
    {
        // Nur Samstag und Sonntag, gefragt an einem Mittwoch.
        var weekend = RestartSchedule.WithDay(RestartSchedule.WithDay(0, DayOfWeek.Saturday, true),
                                              DayOfWeek.Sunday, true);
        var wednesday = Local(2026, 8, 26, 12, 0);
        var next = NextAtTime(2 * 60, weekend, wednesday);

        Assert(next == Local(2026, 8, 29, 2, 0), $"erwartet Samstag 29.08. 02:00, geliefert: {next}");
        Assert(RestartSchedule.IsDaySelected(weekend, DayOfWeek.Sunday), "Sonntag fehlt in der Maske");
        Assert(!RestartSchedule.IsDaySelected(weekend, DayOfWeek.Monday), "Montag steht zu Unrecht in der Maske");
    }

    private static void SingleWeekdayWrapsAround()
    {
        // Nur sonntags, gefragt an einem Sonntag nach dem Termin: eine Woche weiter.
        var sunday = RestartSchedule.WithDay(0, DayOfWeek.Sunday, true);
        var afterIt = Local(2026, 8, 23, 4, 0);           // 23.08.2026 ist ein Sonntag
        var next = NextAtTime(3 * 60, sunday, afterIt);

        Assert(next == Local(2026, 8, 30, 3, 0), $"erwartet 30.08. 03:00, geliefert: {next}");
    }

    private static void MissedWindowIsSkipped()
    {
        var due = Local(2026, 8, 25, 3, 0);

        // Kurz danach: faellig.
        Assert(RestartSchedule.IsDue(RestartScheduleMode.AtTime, due, due.AddMinutes(5)),
            "fuenf Minuten nach dem Termin gilt er als nicht faellig");

        // Sechs Stunden spaeter war der Rechner aus - nicht nachholen.
        Assert(!RestartSchedule.IsDue(RestartScheduleMode.AtTime, due, due.AddHours(6)),
            "ein sechs Stunden alter Termin wurde nachgeholt");

        // Davor natuerlich auch nicht.
        Assert(!RestartSchedule.IsDue(RestartScheduleMode.AtTime, due, due.AddMinutes(-1)),
            "ein Termin wurde vor der Zeit ausgeloest");
    }

    // ----------------------------------------------------------------- Intervall ---

    private static void IntervalCountsUptime()
    {
        var started = Local(2026, 8, 25, 8, 0);
        var sixHours = 6 * 60;

        var next = RestartSchedule.Next(RestartScheduleMode.Interval, 0, 0, sixHours,
                                        started.AddMinutes(30), started);
        Assert(next == Local(2026, 8, 25, 14, 0), $"erwartet 14:00, geliefert: {next}");

        // Ohne laufende Anwendung gibt es nichts zu zaehlen.
        var withoutStart = RestartSchedule.Next(RestartScheduleMode.Interval, 0, 0, sixHours,
                                                started, null);
        Assert(withoutStart is null, $"ohne Startzeit wurde ein Termin erfunden: {withoutStart}");

        // Laeuft die Anwendung laenger als das Intervall, ist sie jetzt faellig - und nicht
        // etwa mehrfach hintereinander.
        var overdue = RestartSchedule.Next(RestartScheduleMode.Interval, 0, 0, sixHours,
                                           started.AddHours(20), started);
        Assert(overdue == started.AddHours(20), $"ein ueberfaelliger Termin wurde falsch gelegt: {overdue}");

        Assert(RestartSchedule.IsDue(RestartScheduleMode.Interval, started.AddHours(6), started.AddHours(9)),
            "beim Intervall wurde ein alter Termin faelschlich uebersprungen");
    }

    private static void NoneIsNeverDue()
    {
        var now = Local(2026, 8, 25, 3, 0);
        Assert(RestartSchedule.Next(RestartScheduleMode.None, 180, RestartSchedule.AllDays, 60, now, now) is null,
            "ohne Plan wurde ein Termin geliefert");
        Assert(!RestartSchedule.IsDue(RestartScheduleMode.None, now, now.AddHours(1)),
            "ohne Plan wurde etwas faellig");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
