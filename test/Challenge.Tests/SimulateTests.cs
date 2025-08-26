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
    [InlineData(Temperature.Room)]
    [InlineData(Temperature.Cold)]
    [InlineData(Temperature.Hot)]
    public async Task Puts_SingleOrderInSingleSpot(string expectedTemp)
    {
        // Arrange
        var storage = new Dictionary<string, int>()
        {
            { Simulation.ToTarget(expectedTemp), 1 },
        };
        var rate = 500;
        Order order = new("1", "Banana", expectedTemp, 20, 50);
        var orders = new List<Order>() { order };
        var expectedAction = new Action(_timeProvider.GetLocalNow().DateTime, order.Id, ActionType.Place, Simulation.ToTarget(expectedTemp));

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(expectedAction, actions.First());
    }

    [Theory(Timeout = 20_000)]
    [InlineData(Temperature.Room, Target.Shelf)]
    [InlineData(Temperature.Cold, Target.Cooler)]
    [InlineData(Temperature.Hot, Target.Heater)]
    public async Task PicksUp_SingleOrderFromCorrectTarget(string expectedTemp, string expectedTarget)
    {
        // Arrange
        Dictionary<string, int> storage = new() { { expectedTarget, 1 } };
        var rate = 500;
        Order order = new("1", "Banana", expectedTemp, 20, 50);
        var orders = new List<Order>() { order };

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(2, actions.Count);
        Assert.Equal(order.Id, actions.Last().Id);
        Assert.Equal(ActionType.Pickup, actions.Last().ActionType);
        Assert.Equal(expectedTarget, actions.Last().Target);
    }

    [Theory(Timeout = 20_000)]
    [InlineData(Temperature.Room, Target.Shelf)]
    [InlineData(Temperature.Cold, Target.Cooler)]
    [InlineData(Temperature.Hot, Target.Heater)]
    public async Task PicksUp_SingleOrderAtViablePickupTime(string expectedTemp, string expectedTarget)
    {
        // Arrange
        Dictionary<string, int> storage = new() { { expectedTarget, 1 } };
        var rate = 500;
        Order order = new("1", "Banana", expectedTemp, 20, 50);
        var orders = new List<Order>() { order };

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(2, actions.Count);
        Assert.Equal(order.Id, actions.Last().Id);
        Assert.Equal(ActionType.Pickup, actions.Last().ActionType);
        Assert.Equal(expectedTarget, actions.Last().Target);
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
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Room, 15, 40),
            new("3", "Banana", Temperature.Hot, 10, 30)
        };

        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(Target.Cooler, actions.First(x => x.Id == "1").Target);
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "2").Target);
        Assert.Equal(Target.Heater, actions.First(x => x.Id == "3").Target);
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
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Cold, 20, 50),
            new("3", "Banana", Temperature.Cold, 20, 50),
        };
        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.True(
            actions.Where(x => x.ActionType == ActionType.Place).All(x => x.Target == Target.Cooler),
            $"Not all orders were put in the cooler: {string.Join(",", actions.Where(x => x.ActionType == ActionType.Place).Select(x => x.Target))}");
    }

    [Theory(Timeout = 20_000)]
    [InlineData(Temperature.Cold)]
    [InlineData(Temperature.Hot)]
    public async Task Puts_ColdOrHotOrderOnShelf_WhenCoolerOrHeaterIsFullRespectively(string temperature)
    {
        // Arrange
        var target = Simulation.ToTarget(temperature);
        Dictionary<string, int> storage = new()
        {
            { target, 1 },
            { Target.Shelf, 1 },

        };
        var rate = 500;
        List<Order> orders = new()
        {
            new("1", "Banana", temperature, 20, 50),
            new("2", "Banana", temperature, 20, 50),
        };
        // Act
        var actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "2").Target);
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