namespace EasyService.Core;

/// <summary>When the supervisor restarts the application on its own.</summary>
public enum RestartScheduleMode
{
    /// <summary>Never. The application runs until it exits or somebody stops it.</summary>
    None = 0,

    /// <summary>At a time of day, on the chosen weekdays.</summary>
    AtTime = 1,

    /// <summary>After a span of uptime, regardless of the clock.</summary>
    Interval = 2,
}

/// <summary>
/// Works out when the next scheduled restart is due.
///
/// Why this exists: plenty of applications leak - a handle here, a few megabytes there - and
/// the cheapest cure has always been to restart them nightly. Everybody builds that with a
/// scheduled task calling "net stop" and "net start", which takes the whole service down,
/// drags its dependencies with it and leaves no trace anybody can alert on. The supervisor
/// can do it better: it restarts only the application behind the service, the Service Control
/// Manager never sees a stop, and the event log gets an entry.
///
/// Times are local wall clock on purpose. An administrator who picks 03:00 means three in the
/// morning as the clock in the server room shows it, including the night the clocks change.
/// The arithmetic therefore never converts to UTC and back; it asks the calendar.
/// </summary>
public static class RestartSchedule
{
    /// <summary>All seven days, the sensible default for "every night at three".</summary>
    public const int AllDays = 0b111_1111;

    /// <summary>
    /// A restart that should have happened while the machine was off is not caught up later.
    /// Waking up at 09:00 and immediately restarting because 03:00 has passed would be a
    /// surprise, not a service.
    /// </summary>
    public static readonly TimeSpan MissedWindow = TimeSpan.FromHours(1);

    public static bool IsDaySelected(int mask, DayOfWeek day) => (mask & (1 << (int)day)) != 0;

    public static int WithDay(int mask, DayOfWeek day, bool selected) =>
        selected ? mask | (1 << (int)day) : mask & ~(1 << (int)day);

    /// <summary>
    /// The next moment the application is due, or null when nothing is scheduled.
    /// </summary>
    /// <param name="afterLocal">Everything before this is in the past.</param>
    /// <param name="startedLocal">
    /// When the application was last started. Only <see cref="RestartScheduleMode.Interval"/>
    /// needs it; without it there is nothing to count from and the answer is null.
    /// </param>
    public static DateTime? Next(RestartScheduleMode mode, int atMinutes, int days, int everyMinutes,
                                 DateTime afterLocal, DateTime? startedLocal)
    {
        switch (mode)
        {
            case RestartScheduleMode.AtTime:
            {
                var mask = days == 0 ? AllDays : days;
                var minutes = Math.Clamp(atMinutes, 0, 24 * 60 - 1);
                var midnight = afterLocal.Date;

                // Heute und die kommende Woche durchgehen: hoechstens acht Kandidaten, und
                // der erste, der in der Zukunft liegt und auf einen gewaehlten Tag faellt,
                // gewinnt.
                for (var offset = 0; offset <= 7; offset++)
                {
                    var candidate = midnight.AddDays(offset).AddMinutes(minutes);
                    if (candidate <= afterLocal) continue;
                    if (IsDaySelected(mask, candidate.DayOfWeek)) return candidate;
                }
                return null;   // nur erreichbar, wenn die Maske keinen Tag enthaelt
            }

            case RestartScheduleMode.Interval:
            {
                if (startedLocal is not { } started) return null;
                var every = Math.Max(1, everyMinutes);
                var due = started.AddMinutes(every);

                // Lief die Anwendung laenger als ein Intervall - etwa weil der Rechner
                // schlief - dann ist sie faellig, nicht ueberfaellig gestapelt.
                return due <= afterLocal ? afterLocal : due;
            }

            default:
                return null;
        }
    }

    /// <summary>Convenience overload for the configuration object.</summary>
    public static DateTime? Next(ServiceConfig cfg, DateTime afterLocal, DateTime? startedLocal) =>
        Next(cfg.RestartScheduleMode, cfg.RestartAtMinutes, cfg.RestartDays, cfg.RestartEveryMinutes,
             afterLocal, startedLocal);

    /// <summary>
    /// True when <paramref name="dueLocal"/> has arrived and is still worth acting on.
    /// A due time that slipped past by more than <see cref="MissedWindow"/> is skipped.
    /// </summary>
    public static bool IsDue(RestartScheduleMode mode, DateTime dueLocal, DateTime nowLocal) =>
        mode switch
        {
            RestartScheduleMode.None => false,

            // Beim Intervall gibt es kein verpasstes Fenster: gemessen wird Laufzeit, und die
            // ist auch dann abgelaufen, wenn niemand hingesehen hat.
            RestartScheduleMode.Interval => nowLocal >= dueLocal,

            _ => nowLocal >= dueLocal && nowLocal - dueLocal <= MissedWindow,
        };

    /// <summary>"03:00" from minutes after midnight, for labels and log lines.</summary>
    public static string FormatTime(int atMinutes)
    {
        var minutes = Math.Clamp(atMinutes, 0, 24 * 60 - 1);
        return new DateTime(2000, 1, 1, minutes / 60, minutes % 60, 0).ToString("t");
    }
}
