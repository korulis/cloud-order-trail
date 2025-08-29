using System.Collections.Concurrent;

namespace Challenge;

class Challenge
{
    /// <summary>
    /// Challenge harness
    /// </summary>
    /// <param name="auth">Authentication token (required)</param>
    /// <param name="endpoint">Problem server endpoint</param>
    /// <param name="name">Problem name. Leave blank (optional)</param>
    /// <param name="seed">Problem seed (random if zero)</param>
    /// <param name="rate">Inverse order rate (in milliseconds)</param>
    /// <param name="min">Minimum pickup time (in seconds)</param>
    /// <param name="max">Maximum pickup time (in seconds)</param>
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 450, int min = 1, int max = 2)
    {
        try
        {
            var client = new Client(endpoint, auth);
            var problem = await client.NewProblemAsync(name, seed);

            // ------ Simulation harness logic goes here using rate, min and max ----
            var storage = new Dictionary<string, int>()
            {
                { Target.Cooler, 6 },
                { Target.Shelf, 12 },
                { Target.Heater, 6 }
            };
            var orders = problem.Orders;
            var simulation = new Simulation(TimeProvider.System);
            List<Action> actions = await simulation.Simulate(
                new Simulation.Config(rate * 1000, min * 1000_000, max * 1000_000, storage),
                orders,
                CancellationToken.None);

            // ----------------------------------------------------------------------

            var result = await client.SolveAsync(
                problem.TestId,
                TimeSpan.FromMilliseconds(rate),
                TimeSpan.FromSeconds(min),
                TimeSpan.FromSeconds(max),
                actions);
            Console.WriteLine($"Result: {result}");

        }
        catch (Exception e)
        {
            Console.WriteLine($"Simulation failed: {e}");
        }
    }
}

public class Simulation : IDisposable
{
    private static readonly List<PickableOrder> _pickableOrders = [];
    private static readonly Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo = new() { };


    /// <summary>
    /// Stores constants required for order handling simulation.
    /// </summary>
    /// <param name="rate">rate of order arrival in microseconds</param>
    /// <param name="min">minimum amount of microseconds before order is picked</param>
    /// <param name="max">maximum amount of microseconds before order is picked</param>
    /// <param name="storage"> storage limits for different kinds of storage</param>
    public record Config(long rate, long min, long max, Dictionary<string, int> storage);

    private readonly TimeProvider _time;

    /// <summary>
    /// Simulation representing module
    /// </summary>
    /// <param name="time"> Date and time measure providing object </param>
    public Simulation(TimeProvider time)
    {
        _time = time;
    }

    public async Task<List<Action>> Simulate(Config config, List<Order> orders, CancellationToken ct)
    {
        var orderPlacementTask = PlaceOrders(config, orders, ct);

        var orderPickupsTask = PickupOrders(config, orders.Count, ct);

        await Task.WhenAll([orderPickupsTask, orderPlacementTask]);

        var result = _actionRepo.Select(kvp => kvp.Value.Actions).SelectMany(x => x).OrderBy(x => x.Timestamp).ToList();
        return result;
    }

    private int processedOrderCount() => _actionRepo
                .Select(kvp => kvp.Value.Actions)
                .Count(group => group.Select(x => x.ActionType).Contains(ActionType.Discard)
                    || group.Select(x => x.ActionType).Contains(ActionType.Pickup));

    private async Task PickupOrders(Config config, int orderCount, CancellationToken ct)
    {
        while (processedOrderCount() != orderCount)
        {
            var localNow = _time.GetLocalNow().DateTime;

            pickupOrders2(localNow, _actionRepo);
            // todo delay exactly until next order is picked up.
            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);

            // var ddd = new ConcurrentDictionary<string, List<Action>>() { };
            // ddd.AddOrUpdate("1", (key, arg) => new List<Action>(), (key, currentValue, arg) => new List<Action>(), factoryArgument: 13);
            // ddd.GetOrAdd("1", (key, arg) => new List<Action>(), factoryArgument: 13);
        }
    }

    private async Task PlaceOrders(Config config, List<Order> orders, CancellationToken ct)
    {
        foreach (var order in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(order.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            PickableOrder pickableOrder = new(order, localNow.AddMicroseconds(pickupInMicroseconds));
            _pickableOrders.Add(pickableOrder);

            var actions = _actionRepo.Values.Select(x => x.Actions).SelectMany(x => x).ToList();
            if ((new[] { Target.Cooler, Target.Heater }).Contains(target))
            {
                // checking if == is actually sufficient
                if (config.storage[target] <= actions.Count(x => x.Target == target && x.ActionType == ActionType.Place))
                {
                    _actionRepo[order.Id] = (pickableOrder, new() { });
                    _actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, Target.Shelf));
                    Console.WriteLine($"Order placed: {order}");
                }
                else
                {
                    _actionRepo[order.Id] = (pickableOrder, new() { });
                    _actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, target));
                    Console.WriteLine($"Order placed: {order}");
                }
            }
            else
            {
                _actionRepo[order.Id] = (pickableOrder, new() { });
                _actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, target));
                Console.WriteLine($"Order placed: {order}");
            }

            // todo kb: use this
            // Use this to make thread wake up times more consistent, because order placement operations might have taken some time.
            // TimeSpan delay = targetTime - DateTime.Now;

            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);

        }
    }

    private static void pickupOrders2(DateTime localNow, Dictionary<string, (PickableOrder Order, List<Action> Actions)> actionRepo)
    {
        // v3 works with actions repo
        if (true)
        {
            // todo: error Collection was modified; enumeration operation may not execute.
            var pickupActions = actionRepo
            .Where(kvp => kvp.Value.Order.PickupTime <= localNow)
            .Select(kvp =>
            {
                kvp.Value.Actions.Sort((x, y) => Convert.ToInt32(x.Timestamp - y.Timestamp));
                return kvp;
            })
            .Where(kvp => new[] { ActionType.Place, ActionType.Move }.Contains(kvp.Value.Actions.Last().ActionType))
            .Select(kvp =>
            {
                if (IsFresh(kvp.Value.Order, kvp.Value.Actions))
                {
                    Action item = new(localNow, kvp.Value.Order.Id, ActionType.Pickup, kvp.Value.Actions.Last().Target);
                    Console.WriteLine($"Picking up order {item}");
                    kvp.Value.Actions.Add(item);
                }
                else
                {
                    Action item = new(localNow, kvp.Value.Order.Id, ActionType.Discard, kvp.Value.Actions.Last().Target);
                    Console.WriteLine($"Discarding order {item}");
                    kvp.Value.Actions.Add(item);
                }
                return kvp;
            })
            .ToList();
        }

    }

    private static bool IsFresh(PickableOrder o, List<Action> a)
    {
        // temp implementation
        return true;
    }

    public static string ToTarget(string temp)
    {
        return temp switch
        {
            "room" => Target.Shelf,
            "cold" => Target.Cooler,
            "hot" => Target.Heater,
            _ => throw new Exception("Unknow temperature option: " + temp)
        };
    }

    public void Dispose()
    {
        _pickableOrders.Clear();
        _actionRepo.Clear();
    }
}

record PickableOrder : Order
{
    public DateTime PickupTime { get; init; }

    public PickableOrder(string id, string name, string temp, long price, long freshness, DateTime pickupTime) : base(id, name, temp, price, freshness)
    {
        PickupTime = pickupTime;
    }

    public PickableOrder(Order order, DateTime pickupTime) : this(order.Id, order.Name, order.Temp, order.Price, order.Freshness, pickupTime)
    {
    }
    public static long RandomBetween(long min, long max)
    {
        var result = new Random().NextInt64(min, max);
        return result;
    }

}
