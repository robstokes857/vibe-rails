using System.Globalization;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public static class JobScheduleCalculator
{
    public const int MinimumIntervalMinutes = 5;
    public const int MaximumIntervalMinutes = 30 * 24 * 60;

    public static DateTime ComputeNext(JobTriggerRequest trigger, DateTime nowUtc)
    {
        if (trigger.Kind != JobTriggerKind.Schedule || trigger.ScheduleKind == null)
            throw new ArgumentException("A schedule trigger and schedule kind are required.", nameof(trigger));

        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return trigger.ScheduleKind.Value switch
        {
            JobScheduleKind.Interval => ComputeInterval(trigger.IntervalMinutes, nowUtc),
            JobScheduleKind.Daily => ComputeWallClock(trigger, nowUtc, weekly: false),
            JobScheduleKind.Weekly => ComputeWallClock(trigger, nowUtc, weekly: true),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), "Unsupported schedule kind.")
        };
    }

    public static string? Validate(JobTriggerRequest trigger)
    {
        if (trigger.Kind != JobTriggerKind.Schedule)
            return trigger.ScheduleKind == null
                   && trigger.IntervalMinutes == null
                   && trigger.LocalTime == null
                   && trigger.DaysOfWeekMask == 0
                   && string.IsNullOrWhiteSpace(trigger.TimeZoneId)
                ? null
                : "Only schedule triggers may include schedule fields.";

        if (trigger.ScheduleKind == null)
            return "Schedule kind is required.";

        try
        {
            if (trigger.ScheduleKind == JobScheduleKind.Interval
                && (trigger.LocalTime is not null || trigger.DaysOfWeekMask != 0 || trigger.TimeZoneId is not null))
                return "Interval schedules may only include interval minutes.";
            if (trigger.ScheduleKind == JobScheduleKind.Daily
                && (trigger.IntervalMinutes is not null || trigger.DaysOfWeekMask != 0))
                return "Daily schedules may only include local time and time zone.";
            if (trigger.ScheduleKind == JobScheduleKind.Weekly && trigger.IntervalMinutes is not null)
                return "Weekly schedules cannot include interval minutes.";
            if (trigger.ScheduleKind == JobScheduleKind.Weekly && (trigger.DaysOfWeekMask & ~0x7f) != 0)
                return "Weekly schedules contain an invalid weekday selection.";
            _ = ComputeNext(trigger, DateTime.UtcNow);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return ex.Message;
        }
    }

    private static DateTime ComputeInterval(int? intervalMinutes, DateTime nowUtc)
    {
        if (intervalMinutes is null or < MinimumIntervalMinutes or > MaximumIntervalMinutes)
            throw new ArgumentException($"Interval must be {MinimumIntervalMinutes}–{MaximumIntervalMinutes} minutes.");
        return nowUtc.AddMinutes(intervalMinutes.Value);
    }

    private static DateTime ComputeWallClock(JobTriggerRequest trigger, DateTime nowUtc, bool weekly)
    {
        if (string.IsNullOrWhiteSpace(trigger.TimeZoneId))
            throw new ArgumentException("Time zone is required for daily and weekly schedules.");
        if (!TimeOnly.TryParseExact(trigger.LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new ArgumentException("Local time must use HH:mm format.");
        if (weekly && (trigger.DaysOfWeekMask & 0x7f) == 0)
            throw new ArgumentException("At least one weekday is required.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById(trigger.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);

        for (var offset = 0; offset <= (weekly ? 14 : 2); offset++)
        {
            var date = localNow.Date.AddDays(offset);
            if (weekly && (trigger.DaysOfWeekMask & (1 << (int)date.DayOfWeek)) == 0)
                continue;

            var candidate = DateTime.SpecifyKind(date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(candidate))
                candidate = candidate.AddMinutes(1);

            var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);
            if (utc > nowUtc)
                return utc;
        }

        throw new InvalidOperationException("Unable to compute the next scheduled time.");
    }
}
