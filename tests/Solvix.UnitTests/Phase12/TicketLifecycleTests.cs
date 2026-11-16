namespace Solvix.UnitTests.Phase12;

using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Exceptions;

public sealed class TicketLifecycleTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static SlaPolicy Policy() => SlaPolicy.Create(Tenant, "Standard", true, false, "UTC", 540, 1020, 62,
    [
        (TicketPriority.Critical, 30, 240),
        (TicketPriority.Medium, 60, 480),
    ]);

    private static Ticket NewTicket(TicketPriority priority = TicketPriority.Medium)
    {
        var t = Ticket.Create(Tenant, 1, "Printer jams", "The printer on floor 2 jams constantly.", priority, TicketSource.Web, null, Guid.NewGuid(), "a@b.test", "A", null, null, T0);
        t.CreatedAtUtc = T0;
        t.ApplySla(Policy(), T0);
        return t;
    }

    [Fact]
    public void Sla_due_dates_follow_the_priority_targets()
    {
        var medium = NewTicket();
        Assert.Equal(T0.AddMinutes(60), medium.FirstResponseDueAtUtc);
        Assert.Equal(T0.AddMinutes(480), medium.ResolutionDueAtUtc);
        var critical = NewTicket(TicketPriority.Critical);
        Assert.Equal(T0.AddMinutes(30), critical.FirstResponseDueAtUtc);
    }

    [Fact]
    public void New_tickets_without_a_category_need_triage()
    {
        var t = NewTicket();
        Assert.True(t.NeedsTriage);
        t.SetCategory(Guid.NewGuid(), T0);
        Assert.False(t.NeedsTriage);
    }

    [Fact]
    public void Waiting_on_requester_pauses_the_clock_and_resuming_keeps_the_remaining_time()
    {
        var t = NewTicket();
        // 100 minutes elapse (380 minutes of resolution budget left), then wait for 3 hours.
        var pausedAt = T0.AddMinutes(100);
        t.ChangeStatus(TicketStatus.WaitingOnRequester, pausedAt, SlaCalendar.AlwaysOn);
        Assert.NotNull(t.SlaPausedAtUtc);
        var resumeAt = pausedAt.AddHours(3);
        t.ChangeStatus(TicketStatus.Open, resumeAt, SlaCalendar.AlwaysOn);
        Assert.Null(t.SlaPausedAtUtc);
        Assert.Equal(resumeAt.AddMinutes(380), t.ResolutionDueAtUtc);
    }

    [Fact]
    public void Resuming_never_shaves_seconds_off_the_remaining_time()
    {
        var t = NewTicket();
        var due = t.ResolutionDueAtUtc!.Value;
        var pausedAt = T0.AddSeconds(90);
        t.ChangeStatus(TicketStatus.WaitingOnRequester, pausedAt, SlaCalendar.AlwaysOn);
        t.ChangeStatus(TicketStatus.Open, pausedAt.AddMilliseconds(250), SlaCalendar.AlwaysOn);
        Assert.Equal(due.AddMilliseconds(250), t.ResolutionDueAtUtc);
    }

    [Fact]
    public void Resolving_after_the_due_date_flags_a_breach_and_before_it_does_not()
    {
        var late = NewTicket();
        late.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(481), SlaCalendar.AlwaysOn);
        Assert.True(late.ResolutionBreached);

        var onTime = NewTicket();
        onTime.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(479), SlaCalendar.AlwaysOn);
        Assert.False(onTime.ResolutionBreached);
    }

    [Fact]
    public void First_response_after_the_target_is_a_breach()
    {
        var t = NewTicket();
        t.RecordStaffResponse(T0.AddMinutes(61));
        Assert.True(t.FirstResponseBreached);
        Assert.Equal(T0.AddMinutes(61), t.FirstRespondedAtUtc);
        t.RecordStaffResponse(T0.AddMinutes(500));
        Assert.Equal(T0.AddMinutes(61), t.FirstRespondedAtUtc); // only the first counts
    }

    [Fact]
    public void Reopening_counts_and_clears_resolution_timestamps()
    {
        var t = NewTicket();
        t.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(10), null);
        t.ChangeStatus(TicketStatus.Open, T0.AddMinutes(20), null);
        Assert.Equal(1, t.ReopenCount);
        Assert.Null(t.ResolvedAtUtc);
    }

    [Fact]
    public void Invalid_transitions_are_rejected()
    {
        var t = NewTicket();
        t.ChangeStatus(TicketStatus.Open, T0.AddMinutes(1), null);
        Assert.Throws<ConflictException>(() => t.ChangeStatus(TicketStatus.New, T0, null));
        t.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(5), null);
        Assert.Throws<ConflictException>(() => t.ChangeStatus(TicketStatus.WaitingOnRequester, T0.AddMinutes(6), null));
    }

    [Fact]
    public void Assigning_a_new_ticket_opens_it()
    {
        var t = NewTicket();
        t.Assign(Guid.NewGuid(), T0);
        Assert.Equal(TicketStatus.Open, t.Status);
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(70, "ok")]
    [InlineData(80, "at_risk")]
    [InlineData(100, "breached")]
    public void Sla_state_reflects_elapsed_time(int percent, string expected)
    {
        var t = NewTicket();
        t.RecordStaffResponse(T0.AddMinutes(1)); // first response met, isolate the resolution clock
        var now = T0.AddMinutes(480 * percent / 100.0 + (percent == 100 ? 1 : 0));
        Assert.Equal(expected, TicketService.SlaStateOf(t, now));
    }

    [Fact]
    public void Paused_and_closed_tickets_have_their_own_sla_state()
    {
        var t = NewTicket();
        t.ChangeStatus(TicketStatus.WaitingOnRequester, T0.AddMinutes(5), null);
        Assert.Equal("paused", TicketService.SlaStateOf(t, T0.AddDays(5)));
        t.ChangeStatus(TicketStatus.Open, T0.AddMinutes(10), null);
        t.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(20), null);
        Assert.Equal("met", TicketService.SlaStateOf(t, T0.AddDays(5)));
    }

    [Fact]
    public void Sla_policy_validation()
    {
        Assert.Throws<ValidationException>(() => SlaPolicy.Create(Tenant, "x", false, false, "UTC", 0, 1440, 127, [(TicketPriority.Low, 60, 30)]));
        Assert.Throws<ValidationException>(() => SlaPolicy.Create(Tenant, "x", false, false, "UTC", 0, 1440, 127, [(TicketPriority.Low, 10, 30), (TicketPriority.Low, 20, 40)]));
        Assert.Throws<ValidationException>(() => SlaPolicy.Create(Tenant, "x", false, true, "Not/AZone", 540, 1020, 62, [(TicketPriority.Low, 10, 30)]));
    }
}

public sealed class EscalationEvaluatorTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static Ticket Open(TicketPriority priority = TicketPriority.High)
    {
        var t = Ticket.Create(Tenant, 5, "s", "d", priority, TicketSource.Web, null, null, "a@b.test", null, null, null, T0);
        t.CreatedAtUtc = T0;
        t.ApplySla(SlaPolicy.Create(Tenant, "p", true, false, "UTC", 0, 1440, 127, [(TicketPriority.High, 60, 600), (TicketPriority.Low, 60, 600)]), T0);
        return t;
    }

    private static EscalationRule Rule(EscalationTrigger trigger, int percent, TicketPriority? min = null) =>
        EscalationRule.Create(Tenant, "r", trigger, percent, EscalationAction.FlagOnly, null, min);

    [Fact]
    public void Resolution_rule_fires_once_the_threshold_is_reached()
    {
        var t = Open();
        var rule = Rule(EscalationTrigger.ResolutionElapsedPercent, 50);
        Assert.False(EscalationEvaluator.ShouldApply(rule, t, T0.AddMinutes(299)));
        Assert.True(EscalationEvaluator.ShouldApply(rule, t, T0.AddMinutes(300)));
    }

    [Fact]
    public void First_response_rule_stops_once_a_response_exists()
    {
        var t = Open();
        var rule = Rule(EscalationTrigger.FirstResponseElapsedPercent, 80);
        Assert.True(EscalationEvaluator.ShouldApply(rule, t, T0.AddMinutes(50)));
        t.RecordStaffResponse(T0.AddMinutes(10));
        Assert.False(EscalationEvaluator.ShouldApply(rule, t, T0.AddMinutes(50)));
    }

    [Fact]
    public void Minimum_priority_filters_tickets()
    {
        var low = Open(TicketPriority.Low);
        Assert.False(EscalationEvaluator.ShouldApply(Rule(EscalationTrigger.ResolutionElapsedPercent, 10, TicketPriority.High), low, T0.AddMinutes(500)));
        Assert.True(EscalationEvaluator.ShouldApply(Rule(EscalationTrigger.ResolutionElapsedPercent, 10, TicketPriority.Low), low, T0.AddMinutes(500)));
    }

    [Fact]
    public void Paused_resolved_and_inactive_never_escalate()
    {
        var rule = Rule(EscalationTrigger.ResolutionElapsedPercent, 10);
        var paused = Open();
        paused.ChangeStatus(TicketStatus.WaitingOnRequester, T0.AddMinutes(1), null);
        Assert.False(EscalationEvaluator.ShouldApply(rule, paused, T0.AddMinutes(500)));

        var resolved = Open();
        resolved.ChangeStatus(TicketStatus.Resolved, T0.AddMinutes(2), null);
        Assert.False(EscalationEvaluator.ShouldApply(rule, resolved, T0.AddMinutes(500)));

        var inactive = Rule(EscalationTrigger.ResolutionElapsedPercent, 10);
        inactive.Update("r", false, EscalationTrigger.ResolutionElapsedPercent, 10, EscalationAction.FlagOnly, null, null);
        Assert.False(EscalationEvaluator.ShouldApply(inactive, Open(), T0.AddMinutes(500)));
    }

    [Fact]
    public void Reassign_needs_a_target()
    {
        Assert.Throws<Solvix.SharedKernel.Exceptions.ValidationException>(() =>
            EscalationRule.Create(Tenant, "r", EscalationTrigger.ResolutionElapsedPercent, 50, EscalationAction.Reassign, null, null));
    }
}
