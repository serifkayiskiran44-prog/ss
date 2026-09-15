using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2637: Daily/Weekly automation scheduling must have an
/// explicit, deterministic DST policy - a spring-forward local instant that
/// doesn't exist must never throw and crash the scheduler poll, and a
/// fall-back local instant that occurs twice must always resolve to the same
/// single occurrence, never duplicate or flip between restarts.
///
/// These tests use an explicit US Eastern-time zone (via NextRunUtc's
/// testability overload) rather than TimeZoneInfo.Local, because this host's
/// own local zone (Turkey) has observed no DST since 2016 and so has no
/// transitions to exercise.
[TestClass]
public sealed class AutomationScheduleDstTests
{
    static TimeZoneInfo Eastern => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    static DateTime NthWeekdayOfMonth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateTime(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (n - 1) * 7);
    }

    static DateTime SpringForwardDate(int year) => NthWeekdayOfMonth(year, 3, DayOfWeek.Sunday, 2); // US: 2nd Sunday of March
    static DateTime FallBackDate(int year) => NthWeekdayOfMonth(year, 11, DayOfWeek.Sunday, 1); // US: 1st Sunday of November

    static AutomationJob DailyJob(string runAtLocal) => new() { Kind = AutomationKind.Stock, ScheduleMode = "Daily", RunAtLocal = runAtLocal, IntervalMinutes = 30 };

    static DateTime UtcJustBeforeLocalMidnight(DateTime localDate, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified), zone).AddSeconds(-1);

    [TestMethod]
    public void NormalNonTransitionDayWorksAsExpected()
    {
        var zone = Eastern;
        var job = DailyJob("09:00");
        var normalDay = new DateTime(2026, 6, 15);
        var nowUtc = UtcJustBeforeLocalMidnight(normalDay, zone).AddSeconds(1); // just after local midnight
        var next = AutomationSchedule.NextRunUtc(job, nowUtc, zone);
        var expectedLocal = normalDay.Add(TimeSpan.FromHours(9));
        Assert.AreEqual(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(expectedLocal, DateTimeKind.Unspecified), zone), next);
    }

    [TestMethod]
    public void SpringForwardGapIsSkippedNotThrown()
    {
        var zone = Eastern;
        var springForward = SpringForwardDate(2026); // 02:00 -> 03:00 local instants in [02:00,03:00) don't exist
        var job = DailyJob("02:30");
        var nowUtc = UtcJustBeforeLocalMidnight(springForward, zone).AddSeconds(1);

        DateTime next = default;
        try { next = AutomationSchedule.NextRunUtc(job, nowUtc, zone); }
        catch (Exception ex) { Assert.Fail($"NextRunUtc must never throw on a DST gap; got {ex}"); }

        // The invalid occurrence on the transition day is skipped entirely - the
        // next valid 02:30 local instant is the following day.
        var expectedLocal = springForward.AddDays(1).Add(TimeSpan.FromHours(2.5));
        Assert.AreEqual(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(expectedLocal, DateTimeKind.Unspecified), zone), next);
    }

    [TestMethod]
    public void FallBackAmbiguousTimeResolvesToASingleDeterministicOccurrence()
    {
        var zone = Eastern;
        var fallBack = FallBackDate(2026); // 01:00-02:00 local occurs twice
        var job = DailyJob("01:30");
        var nowUtc = UtcJustBeforeLocalMidnight(fallBack, zone).AddSeconds(1);

        var first = AutomationSchedule.NextRunUtc(job, nowUtc, zone);
        var second = AutomationSchedule.NextRunUtc(job, nowUtc, zone);
        Assert.AreEqual(first, second, "The same schedule computed twice (e.g. across a restart) must pick the same occurrence.");

        var candidateLocal = DateTime.SpecifyKind(fallBack.Add(TimeSpan.FromHours(1.5)), DateTimeKind.Unspecified);
        Assert.IsTrue(zone.IsAmbiguousTime(candidateLocal), "Test setup sanity check: 01:30 on the fall-back date must actually be ambiguous in this zone.");
        var offsets = zone.GetAmbiguousTimeOffsets(candidateLocal);
        var earlierUtc = new DateTimeOffset(candidateLocal, offsets.Max()).UtcDateTime;
        var laterUtc = new DateTimeOffset(candidateLocal, offsets.Min()).UtcDateTime;
        Assert.AreEqual(earlierUtc, first, "Policy: always resolve an ambiguous fall-back instant to the earlier UTC occurrence.");
        Assert.AreNotEqual(laterUtc, first);
    }

    [TestMethod]
    public void WeeklyDayOfWeekBoundaryStaysCorrectAcrossTheSpringForwardTransition()
    {
        var zone = Eastern;
        var springForward = SpringForwardDate(2026);
        var job = new AutomationJob { Kind = AutomationKind.Stock, ScheduleMode = "Weekly", RunAtLocal = "09:00", DaysOfWeek = springForward.DayOfWeek.ToString(), IntervalMinutes = 30 };
        var nowUtc = UtcJustBeforeLocalMidnight(springForward, zone).AddSeconds(1);
        var next = AutomationSchedule.NextRunUtc(job, nowUtc, zone);
        var localResult = TimeZoneInfo.ConvertTimeFromUtc(next, zone);
        Assert.AreEqual(springForward.DayOfWeek, localResult.DayOfWeek, "The weekly day-of-week gate must still land on the correct local calendar day across a DST transition.");
        Assert.AreEqual(springForward.Date, localResult.Date);
    }

    [TestMethod]
    public void SpringForwardNeverBlocksLaterValidDaysFromBeingScheduled()
    {
        var zone = Eastern;
        var springForward = SpringForwardDate(2026);
        var job = DailyJob("02:30");
        var nowUtc = UtcJustBeforeLocalMidnight(springForward.AddDays(2), zone).AddSeconds(1);
        var next = AutomationSchedule.NextRunUtc(job, nowUtc, zone);
        Assert.AreEqual(springForward.AddDays(2).Date, TimeZoneInfo.ConvertTimeFromUtc(next, zone).Date);
    }
}
