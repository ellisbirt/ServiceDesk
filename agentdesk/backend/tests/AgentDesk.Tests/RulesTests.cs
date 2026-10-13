using AgentDesk.Core.Domain;
using AgentDesk.Core.Rules;

namespace AgentDesk.Tests;

public class RulesTests
{
    [Theory]
    [InlineData(TicketStatuses.New, TicketStatuses.Open, true)]
    [InlineData(TicketStatuses.Open, TicketStatuses.Resolved, true)]
    [InlineData(TicketStatuses.Resolved, TicketStatuses.Open, true)]
    [InlineData(TicketStatuses.Resolved, TicketStatuses.Closed, true)]
    [InlineData(TicketStatuses.Open, TicketStatuses.Closed, false)]
    [InlineData(TicketStatuses.Closed, TicketStatuses.Open, false)]
    [InlineData(TicketStatuses.Cancelled, TicketStatuses.Open, false)]
    public void StateMachine_AllowsOnlyDefinedTransitions(string from, string to, bool allowed) =>
        Assert.Equal(allowed, TicketStateMachine.CanTransition(from, to));

    [Fact]
    public void StateMachine_TerminalStatesHaveNoExits()
    {
        Assert.True(TicketStateMachine.IsTerminal(TicketStatuses.Closed));
        Assert.True(TicketStateMachine.IsTerminal(TicketStatuses.Cancelled));
        Assert.Empty(TicketStateMachine.AllowedFrom(TicketStatuses.Closed));
    }

    [Theory]
    [InlineData(1, 1, "P1")]
    [InlineData(1, 2, "P2")]
    [InlineData(2, 2, "P3")]
    [InlineData(3, 3, "P4")]
    public void PriorityMatrix_CombinesImpactAndUrgency(int impact, int urgency, string expected) =>
        Assert.Equal(expected, PriorityMatrix.Compute(impact, urgency));

    [Fact]
    public void PriorityMatrix_RejectsOutOfRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityMatrix.Compute(0, 2));

    [Fact]
    public void Numbering_IsZeroPadded() => Assert.Equal("KB0000042", Numbering.Kb(42));
}
