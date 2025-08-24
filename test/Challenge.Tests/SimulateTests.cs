using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace Challenge.Tests;

public class SimulateTests
{
    [Fact]
    public async Task Puts_SingleOrderInSingleSpot()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider();
        var storage = new Dictionary<string, int>()
        {
            { Action.Shelf, 1 },
        };
        var rate = 500;
        Order order = new("1", "Banana", Action.Shelf, 20, 50);
        var orders = new List<Order>() { order };
        var sut = new Simulation();
        var expectedAction = new Action(timestamp: timeProvider.GetLocalNow().DateTime, id: order.Id, actionType: Action.Place, target: Action.Shelf);

        // Act
        var actions = await sut.Simulate(timeProvider, rate, storage, orders);
        timeProvider.Advance(TimeSpan.FromMinutes(rate * orders.Count / 1000 + 1));

        // Assert
        Assert.Single(actions);
        Assert.Equal(expectedAction, actions[0]);
    }
}