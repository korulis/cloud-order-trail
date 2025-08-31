using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace Challenge.Tests;

[CollectionDefinition("SequentialTest", DisableParallelization = true)]
public class SequentialTestCollection { }

[Collection("SequentialTest")]
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
        Dictionary<string, int> storageLimits = new() {
            { Target.Cooler, 3 },
            { Target.Shelf, 3 },
            { Target.Heater, 3 }};
        _defaultConfig = new Simulation.Config(500_000, 6_000_000, 8_000_000, storageLimits);
        _cts = new CancellationTokenSource(5_000);
        // _cts = new CancellationTokenSource(5000_000);

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
    public async Task PicksUp_SingleOrderFromEachTarget()
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
        var pickupActionTargets = actions
        .Where(x => x.ActionType == ActionType.Pickup)
        .Select(x => x.Target)
        .ToList();
        Assert.True(pickupActionTargets.Count == 3);
        Assert.Contains(Target.Shelf, pickupActionTargets);
        Assert.Contains(Target.Cooler, pickupActionTargets);
        Assert.Contains(Target.Heater, pickupActionTargets);

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
        Assert.True(2 == actions.Count, $"Expected 2 actions, received : {JsonSerializer.Serialize(actions)}");
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
    public async Task PicksUp_SeveralOrdersFromSeveralTargetsAtViablePickupTime()
    {
        // Arrange
        var config = _defaultConfig;
        List<Order> orders = [
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Room, 15, 40),
            new("3", "Banana", Temperature.Hot, 10, 30),
            new("4", "Banana", Temperature.Cold, 20, 50),
            new("5", "Banana", Temperature.Room, 15, 40),
            new("6", "Banana", Temperature.Hot, 10, 30),
            new("7", "Banana", Temperature.Cold, 20, 50),
            new("8", "Banana", Temperature.Room, 15, 40),
            new("9", "Banana", Temperature.Hot, 10, 30),

        ];

        // Act
        var actions = await SimulateToTheEnd(config, orders, _cts.Token);

        // Assert
        foreach (var order in orders)
        {
            var pickupActions = actions.Where(x => x.ActionType == ActionType.Pickup && x.Id == order.Id).ToList();
            Assert.True(1 == pickupActions.Count, $"Expected 1 pickup action for order {order.Id}, received : {JsonSerializer.Serialize(pickupActions)}");
            var actualPickupTime = pickupActions[0].GetOriginalTimestamp();
            var placingTime = actions.Single(x => x.ActionType == ActionType.Place && x.Id == order.Id).GetOriginalTimestamp();
            var minPickupTime = placingTime.AddMicroseconds(config.min);
            var maxPickupTime = placingTime.AddMicroseconds(config.max);
            Assert.True(
                actualPickupTime >= minPickupTime && actualPickupTime <= maxPickupTime,
                $"Pick up happenned {actualPickupTime:hh:mm:ss.fff} outside of expected pickup interval [{minPickupTime:hh:mm:ss.fff}, {maxPickupTime:hh:mm:ss.fff}]");
        }
    }

    [Fact()]
    public async Task Puts_SingleOrderInEachTarget()
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

    [Fact()]
    public async Task Puts_SeveralOrdersIntoCorrespondingTargets()
    {
        // Arrange
        var config = _defaultConfig;
        List<Order> orders = [
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Room, 15, 40),
            new("3", "Banana", Temperature.Hot, 10, 30),
            new("4", "Banana", Temperature.Cold, 20, 50),
            new("5", "Banana", Temperature.Room, 15, 40),
            new("6", "Banana", Temperature.Hot, 10, 30),
            new("7", "Banana", Temperature.Cold, 20, 50),
            new("8", "Banana", Temperature.Room, 15, 40),
            new("9", "Banana", Temperature.Hot, 10, 30),

        ];

        // Act
        var actions = await SimulateToTheEnd(config, orders, _cts.Token);

        // Assert
        foreach (var order in orders)
        {
            var pickupTarget = actions.Single(x => x.ActionType == ActionType.Pickup && x.Id == order.Id).Target;
            var placementTarget = actions.Single(x => x.ActionType == ActionType.Place && x.Id == order.Id).Target;
            Assert.Equal(placementTarget, pickupTarget);
        }
    }

    [Theory()]
    [InlineData(Temperature.Cold)]
    [InlineData(Temperature.Hot)]
    public async Task Puts_NonShelfOrderOnShelf_WhenRespectiveNonShelfTargetIsFull(string temperature)
    {
        // Arrange
        var target = Simulation.ToTarget(temperature);
        Dictionary<string, int> storageLimits = new()
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
        var actions = await SimulateToTheEnd(_defaultConfig with { storageLimits = storageLimits }, orders, _cts.Token);

        // Assert
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "2").Target);
    }

    [Fact()]
    public async Task Puts_AllColdOrdersOnShelf_WhenCoolerIsFull()
    {
        // Arrange
        var config = _defaultConfig;
        List<Order> coolerFillingOrders = [
            new("1", "Banana", Temperature.Cold, 20, 50),
            new("2", "Banana", Temperature.Cold, 15, 40),
            new("3", "Banana", Temperature.Cold, 10, 30),

        ];

        List<Order> extraOrders = [
            new("4", "Banana", Temperature.Cold, 20, 50),
            new("5", "Banana", Temperature.Cold, 15, 40),
            new("6", "Banana", Temperature.Cold, 10, 30),

        ];
        List<Order> orders = [.. coolerFillingOrders, .. extraOrders];

        // Act
        var actions = await SimulateToTheEnd(config, orders, _cts.Token);

        // Assert
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "4").Target);
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "5").Target);
        Assert.Equal(Target.Shelf, actions.First(x => x.Id == "6").Target);
    }

    // [Fact()]
    // public async Task Moves_NonShelfOrderToNonShelfStorage_WhenShelfOrderArrives()
    // {
    //     // Arrange
    //     var testTarget = Target.Cooler;
    //     var oppositeTarget = Target.Heater;

    //     Dictionary<string, int> storageLimits = new() {
    //         { Target.Cooler, 1 },
    //         { Target.Shelf, 1 },
    //         { Target.Heater, 999 }};
    //     Simulation.Config expireInFiveOrdersConfig = new(1_000_000, 5_000_000, 5_000_000, storageLimits);

    //     Order orderToExpireBeforeShelfArrives = new("x1", "Banana", Temperature.Cold, 20, 50);
    //     List<Order> timeFillingOrders1 = Enumerable
    //         .Range(2, 3)
    //         .Select(x => new Order("f1" + x.ToString(), "Banana", Temperature.Hot, 20, 60))
    //         .ToList();
    //     Order orderForcedToShelf = new("x2", "Banana", Temperature.Cold, 20, 50);
    //     List<Order> timeFillingOrders2 = Enumerable
    //         .Range(2, 3)
    //         .Select(x => new Order("f2" + x.ToString(), "Banana", Temperature.Hot, 20, 60))
    //         .ToList();
    //     Order shelfOrder = new("x3", "Banana", Temperature.Room, 20, 50);

    //     List<Order> orders = [orderToExpireBeforeShelfArrives, .. timeFillingOrders1, orderForcedToShelf, .. timeFillingOrders2, shelfOrder];

    //     // Act
    //     var actions = await SimulateToTheEnd(expireInFiveOrdersConfig, orders, _cts.Token);

    //     // Assert
    //     var moveActions = actions.Where(x => x.Id == "x2" && x.ActionType == ActionType.Move).ToList();
    //     Assert.True(moveActions.Count == 1, $"Expected single {ActionType.Move} action for {"x2"} order, but found {moveActions.Count}");
    //     Assert.Equal(Target.Cooler, moveActions.Single().Target);
    // }


    private async Task<List<Action>> SimulateToTheEnd(Simulation.Config config, List<Order> orders, CancellationToken ct)
    {
        // make time increment steps slightly more granular than simulation steps or pickup interval.
        var minStep = Math.Min(config.rate, config.max - config.min) / 2;
        minStep = minStep == 0 ? 1 : minStep;

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
        _sut.Dispose();
    }
}