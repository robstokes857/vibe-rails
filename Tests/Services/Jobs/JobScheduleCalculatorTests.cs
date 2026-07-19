using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobScheduleCalculatorTests
{
    [Fact]
    public void ComputeNext_Interval_UsesCurrentUtcAndConfiguredMinutes()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Interval,
            IntervalMinutes: 15);

        Assert.Equal(now.AddMinutes(15), JobScheduleCalculator.ComputeNext(trigger, now));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(43201)]
    public void Validate_RejectsIntervalsOutsideSupportedRange(int minutes)
    {
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Interval,
            IntervalMinutes: minutes);

        Assert.NotNull(JobScheduleCalculator.Validate(trigger));
    }

    [Fact]
    public void ComputeNext_Daily_UsesStrictlyFutureWallClockTime()
    {
        var now = new DateTime(2026, 7, 19, 9, 0, 0, DateTimeKind.Utc);
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Daily,
            LocalTime: "09:00",
            TimeZoneId: "UTC");

        Assert.Equal(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc),
            JobScheduleCalculator.ComputeNext(trigger, now));
    }

    [Fact]
    public void ComputeNext_Weekly_UsesSelectedWeekdays()
    {
        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc); // Sunday
        var mondayMask = 1 << (int)DayOfWeek.Monday;
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Weekly,
            LocalTime: "08:30",
            DaysOfWeekMask: mondayMask,
            TimeZoneId: "UTC");

        Assert.Equal(new DateTime(2026, 7, 20, 8, 30, 0, DateTimeKind.Utc),
            JobScheduleCalculator.ComputeNext(trigger, now));
    }

    [Fact]
    public void Validate_RejectsScheduleFieldsOnGitTrigger()
    {
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Vca,
            TimeZoneId: "UTC");

        Assert.Equal("Only schedule triggers may include schedule fields.",
            JobScheduleCalculator.Validate(trigger));
    }

    [Fact]
    public void Validate_RejectsBitsOutsideSevenWeekdays()
    {
        var trigger = new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Weekly,
            LocalTime: "09:00",
            DaysOfWeekMask: 1 << 8,
            TimeZoneId: "UTC");

        Assert.Equal(
            "Weekly schedules contain an invalid weekday selection.",
            JobScheduleCalculator.Validate(trigger));
    }
}
