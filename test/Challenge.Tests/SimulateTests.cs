using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace Challenge.Tests;

public class SimulateTests
{
    private readonly ITestOutputHelper _output;
    private readonly FakeTimeProvider _timeProvider;
    private readonly Simulation _sut;

    public SimulateTests(ITestOutputHelper output)
    {
        _output = output;
        _timeProvider = new FakeTimeProvider();
        _sut = new Simulation(_timeProvider);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(Target.Shelf)]
    [InlineData(Target.Cooler)]
    [InlineData(Target.Heater)]
    public async Task Puts_SingleOrderInSingleSpot(string expectedTarget)
    {
        // Arrange
        var storage = new Dictionary<string, int>()
        {
            { expectedTarget, 1 },
        };
        var rate = 500;
        Order order = new("1", "Banana", expectedTarget, 20, 50);
        var orders = new List<Order>() { order };
        var expectedAction = new Action(_timeProvider.GetLocalNow().DateTime, order.Id, ActionType.Place, expectedTarget);

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Single(actions);
        Assert.Equal(expectedAction, actions[0]);
    }

    [Fact(Timeout = 20_000)]
    public async Task Puts_SingleOrderInEachSpot()
    {
        // Arrange
        var storage = new Dictionary<string, int>()
        {
            { Target.Cooler, 1 },
            { Target.Shelf, 1 },
            { Target.Heater, 1 },

        };
        var rate = 500;
        var orders = new List<Order>()
        {
            new("1", "Banana", Target.Cooler, 20, 50),
            new("2", "Banana", Target.Shelf, 15, 40),
            new("3", "Banana", Target.Heater, 10, 30)
        };

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(3, actions.Count);
        Assert.Equal(Target.Cooler, actions.Single(x => x.Id == "1").Target);
        Assert.Equal(Target.Shelf, actions.Single(x => x.Id == "2").Target);
        Assert.Equal(Target.Heater, actions.Single(x => x.Id == "3").Target);
    }

    [Fact(Timeout = 20_000)]
    public async Task Puts_SeveralOrdersIntoCooler()
    {
        // Arrange
        var storage = new Dictionary<string, int>()
        {
            { Target.Cooler, 3 },

        };
        var rate = 500;
        var orders = new List<Order>()
        {
            new("1", "Banana", Target.Cooler, 20, 50),
            new("2", "Banana", Target.Cooler, 20, 50),
            new("3", "Banana", Target.Cooler, 20, 50),
        };
        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(3, actions.Count);
        Assert.True(actions.All(x => x.Target == Target.Cooler), "Not all orders were put in the cooler.");
    }

    [Theory(Timeout = 20_000)]
    [InlineData(Target.Cooler)]
    [InlineData(Target.Heater)]
    public async Task Puts_CoolOrHotOrderOnShelf_WhenCoolerOrHeaterIsFullRespectively(string target)
    {
        // Arrange
        Dictionary<string, int> storage = new()
        {
            { target, 1 },
            { Target.Shelf, 1 },

        };
        var rate = 500;
        List<Order> orders = new()
        {
            new("1", "Banana", target, 20, 50),
            new("2", "Banana", target, 20, 50),
        };
        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(Target.Shelf, actions.Single(x => x.Id == "2").Target);
    }

    private async Task<List<Action>> SimulateToTheEnd(Dictionary<string, int> storage, int rate, List<Order> orders)
    {
        var actionsTask = _sut.Simulate(rate, storage, orders);
        while (actionsTask.IsCompleted == false)
        {
            // make time increment steps slightly more granular than simulation steps.
            _timeProvider.Advance(TimeSpan.FromMilliseconds(rate) / 3);
        }
        var actions = await actionsTask;
        return actions;
    }
}