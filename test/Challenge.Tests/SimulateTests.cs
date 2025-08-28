using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace Challenge.Tests;

public class SimulateTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly FakeTimeProvider _timeProvider;
    private readonly Simulation _sut;
    private readonly Simulation.Config _defaultConfig;
    private readonly CancellationTokenSource _cts;

    public SimulateTests(ITestOutputHelper output)
    {
        _output = output;
        _timeProvider = new FakeTimeProvider();
        _sut = new Simulation(_timeProvider);
        var storage = new Dictionary<string, int>() {
            { Target.Cooler, 3 },
            { Target.Shelf, 3 },
            { Target.Heater, 3 }};
        _defaultConfig = new Simulation.Config(50_000, 4_000_000, 8_000_000, storage);
        _cts = new CancellationTokenSource(5_000);

    }

    [Theory()]
    [InlineData(Temperature.Room)]
    [InlineData(Temperature.Cold)]
    [InlineData(Temperature.Hot)]
    public async Task Puts_SingleOrder(string expectedTemp)
    {
        // Arrange
        Order order = new("1", "Banana", expectedTemp, 20, 50);
        var expectedAction = new Action(_timeProvider.GetLocalNow().DateTime, order.Id, ActionType.Place, Simulation.ToTarget(expectedTemp));

        // Act
        var actions = await SimulateToTheEnd(_defaultConfig, [order], _cts.Token);

        // Assert
        Assert.Equal(expectedAction, actions.First());
    }

    [Theory()]
    [InlineData(Temperature.Room, Target.Shelf)]
    [InlineData(Temperature.Cold, Target.Cooler)]
    [InlineData(Temperature.Hot, Target.Heater)]
    public async Task PicksUp_SingleOrderFromCorrectTarget(string expectedTemp, string expectedTarget)
    {
        // Arrange
        Order order = new("1", "Banana", expectedTemp, 20, 50);

        // Act
        var actions = await SimulateToTheEnd(_defaultConfig, [order], _cts.Token);

        // Assert
        Assert.Equal(2, actions.Count);
        Assert.Equal(order.Id, actions.Last().Id);
        Assert.Equal(ActionType.Pickup, actions.Last().ActionType);
        Assert.Equal(expectedTarget, actions.Last().Target);
    }

    [Fact()]
    public async Task PicksUp_SingleOrderAtViablePickupTime()
    {
        // Arrange
        var config = _defaultConfig;
        Order order = new("1", "Banana", Temperature.Room, 20, 50);

        // Act
        var actions = await SimulateToTheEnd(config, [order], _cts.Token);

        // Assert
        Assert.Equal(2, actions.Count);
        var pickupAction = actions.Last();
        Assert.Equal(order.Id, pickupAction.Id);
        Assert.True(ActionType.Pickup == pickupAction.ActionType, $"{(actions[0], actions[1])}");
        var pickupTime = pickupAction.GetOriginalTimestamp();
        var placingTime = actions.First().GetOriginalTimestamp();
        var minPickupTime = placingTime.AddMicroseconds(config.min);
        var maxPickupTime = placingTime.AddMicroseconds(config.max);
        Assert.True(
            pickupTime >= minPickupTime && pickupTime <= maxPickupTime,
            $"Pick up happenned {pickupTime:hh:mm:ss.fff} outside of expected pickup interval [{minPickupTime:hh:mm:ss.fff}, {maxPickupTime:hh:mm:ss.fff}]");
    }

    [Fact()]
    public async Task Puts_SingleOrderInEachSpot()
    {
        // Arrange
        List<Order> orders = [
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Room, 15, 40),
            new("3", "Banana", Temperature.Hot, 10, 30)
        ];

        // Act
        var actions = await SimulateToTheEnd(_defaultConfig, orders, _cts.Token);

        // Assert
        Assert.Equal(Target.Cooler, actions.First(x => x.Id == "1").Target);
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "2").Target);
        Assert.Equal(Target.Heater, actions.First(x => x.Id == "3").Target);
    }

    [Fact()]
    public async Task Puts_SeveralOrdersIntoCooler()
    {
        // Arrange
        List<Order> orders =
        [
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Cold, 20, 50),
            new("3", "Banana", Temperature.Cold, 20, 50),
        ];
        // Act
        var actions = await SimulateToTheEnd(_defaultConfig, orders, _cts.Token);

        // Assert
        Assert.True(
            actions.Where(x => x.ActionType == ActionType.Place).All(x => x.Target == Target.Cooler),
            $"Not all orders were put in the cooler: {string.Join(",", actions.Where(x => x.ActionType == ActionType.Place).Select(x => x.Target))}");
    }

    [Theory()]
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
        List<Order> orders =
        [
            new("1", "Banana", temperature, 20, 50),
            new("2", "Banana", temperature, 20, 50),
        ];
        // Act
        var actions = await SimulateToTheEnd(_defaultConfig with { storage = storage }, orders, _cts.Token);

        // Assert
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "2").Target);
    }

    private async Task<List<Action>> SimulateToTheEnd(Simulation.Config config, List<Order> orders, CancellationToken ct)
    {
        // make time increment steps slightly more granular than simulation steps or pickup interval.
        var minStep = Math.Min(config.rate, config.max - config.min) / 2;

        var actionsTask = _sut.Simulate(config, orders, ct);
        while (actionsTask.IsCompleted == false)
        {
            _timeProvider.Advance(TimeSpan.FromMicroseconds(minStep));
        }
        var actions = await actionsTask;
        return actions;
    }

    public void Dispose()
    {
        _cts.Dispose();
    }
}