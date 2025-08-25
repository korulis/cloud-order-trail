using System.Threading.Tasks;
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
    [InlineData(StorageTemp.Shelf)]
    [InlineData(StorageTemp.Cooler)]
    [InlineData(StorageTemp.Heater)]
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
            { StorageTemp.Cooler, 1 },
            { StorageTemp.Shelf, 1 },
            { StorageTemp.Heater, 1 },

        };
        var rate = 500;
        var orders = new List<Order>()
        {
            new("1", "Banana", StorageTemp.Cooler, 20, 50),
            new("2", "Banana", StorageTemp.Shelf, 15, 40),
            new("3", "Banana", StorageTemp.Heater, 10, 30)
        };

        // Act
        List<Action> actions = await SimulateToTheEnd(storage, rate, orders);

        // Assert
        Assert.Equal(3, actions.Count);
        Assert.Equal(StorageTemp.Cooler, actions.Single(x => x.Id == "1").Target);
        Assert.Equal(StorageTemp.Shelf, actions.Single(x => x.Id == "2").Target);
        Assert.Equal(StorageTemp.Heater, actions.Single(x => x.Id == "3").Target);
    }

    private async Task<List<Action>> SimulateToTheEnd(Dictionary<string, int> storage, int rate, List<Order> orders)
    {
        var actionsTask = _sut.Simulate(rate, storage, orders);
        while (actionsTask.IsCompleted == false)
        {
            _timeProvider.Advance(TimeSpan.FromMilliseconds(rate) / 3);
        }
        var actions = await actionsTask;
        return actions;
    }
}