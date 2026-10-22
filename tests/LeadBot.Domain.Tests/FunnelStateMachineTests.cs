using LeadBot.Domain.Leads;
using Xunit;

namespace LeadBot.Domain.Tests;

public class FunnelStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(10));

    private static Lead NewLead() => new(new ChannelIdentity(ChannelKind.Telegram, "1", "1"), Now);

    [Fact]
    public void Bot_cannot_enter_discovery_without_task_data()
    {
        var lead = NewLead();
        lead.MoveTo(LeadStage.Qualifying, TransitionSource.System, Now);

        var r = lead.MoveTo(LeadStage.Discovery, TransitionSource.Bot, Now);

        Assert.False(r.Success);
        Assert.Contains("service_type", r.Error);
        Assert.Equal(LeadStage.Qualifying, lead.Stage);
    }

    [Fact]
    public void Happy_path_up_to_call_scheduled()
    {
        var lead = NewLead();
        var p = lead.Profile;
        lead.MoveTo(LeadStage.Qualifying, TransitionSource.System, Now);

        p.ServiceType = ServiceType.AiIntegration;
        p.ProblemSummary = "ИИ-бот для отдела продаж";
        Assert.True(lead.MoveTo(LeadStage.Discovery, TransitionSource.Bot, Now).Success);

        p.BudgetRange = "до 500 тыс.";
        p.Timeline = "к январю";
        p.Name = "Иван";
        Assert.True(lead.MoveTo(LeadStage.Qualified, TransitionSource.Bot, Now).Success);

        p.CallSlot = Now.AddDays(1);
        Assert.True(lead.MoveTo(LeadStage.CallScheduled, TransitionSource.Bot, Now).Success);
    }

    [Theory]
    [InlineData(LeadStage.ProposalSent)]
    [InlineData(LeadStage.Won)]
    public void Bot_cannot_set_manager_only_stages(LeadStage target)
    {
        var lead = NewLead();
        Assert.False(lead.MoveTo(target, TransitionSource.Bot, Now).Success);
    }

    [Fact]
    public void Crm_is_source_of_truth_and_same_stage_is_noop()
    {
        var lead = NewLead();
        Assert.True(lead.MoveTo(LeadStage.Negotiation, TransitionSource.Crm, Now).Success);
        var again = lead.MoveTo(LeadStage.Negotiation, TransitionSource.Crm, Now);
        Assert.True(again.Success);
        Assert.Equal(again.From, again.To);
    }

    [Fact]
    public void Lost_requires_reason()
    {
        var lead = NewLead();
        Assert.False(lead.MoveTo(LeadStage.Lost, TransitionSource.Bot, Now).Success);
        lead.Profile.LostReason = "выбрали другого подрядчика";
        Assert.True(lead.MoveTo(LeadStage.Lost, TransitionSource.Bot, Now).Success);
    }

    [Fact]
    public void Client_message_resets_followups()
    {
        var lead = NewLead();
        lead.ScheduleFollowUp(Now.AddHours(1));
        lead.RegisterFollowUpSent(Now);
        lead.AddClientMessage("ответ", Now, "x1");
        Assert.Equal(0, lead.FollowUpCount);
        Assert.Null(lead.NextFollowUpAt);
        Assert.True(lead.HasMessage("x1"));
    }
}
