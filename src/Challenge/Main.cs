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
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 50, int min = 0, int max = 8)
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

public class Simulation
{
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

        Dictionary<string, (PickableOrder Order, List<Action> Actions)> actionRepo = new() { };


        List<PickableOrder> pickableOrders = [];

        foreach (var order in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(order.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            PickableOrder pickableOrder = new(order, localNow.AddMicroseconds(pickupInMicroseconds));
            pickableOrders.Add(pickableOrder);

            var actions = actionRepo.Values.Select(x => x.Actions).SelectMany(x => x).ToList();
            if ((new[] { Target.Cooler, Target.Heater }).Contains(target))
            {
                // checking if == is actually sufficient
                if (config.storage[target] <= actions.Count(x => x.Target == target && x.ActionType == ActionType.Place))
                {
                    actionRepo[order.Id] = (pickableOrder, new() { });
                    actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, Target.Shelf));
                    Console.WriteLine($"Order placed: {order}");
                }
                else
                {
                    actionRepo[order.Id] = (pickableOrder, new() { });
                    actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, target));
                    Console.WriteLine($"Order placed: {order}");
                }
            }
            else
            {
                actionRepo[order.Id] = (pickableOrder, new() { });
                actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, target));
                Console.WriteLine($"Order placed: {order}");
            }

            // todo kb: use this
            // Use this to make thread wake up times more consistent, because order placement operations might have taken some time.
            // TimeSpan delay = targetTime - DateTime.Now;

            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);


            // actions.Add(new(localNow, order.Id, ActionType.Place, target));
            // Console.WriteLine($"Order placed: {order}");
            // await Task.Delay(TimeSpan.FromMilliseconds(rate), _time);
            // pickup
            // Action pickupAction = new(localNow, order.Id, ActionType.Pickup, Target.Heater);
            // actions.Add(pickupAction);
            // Console.WriteLine($"Order picked: {pickupAction}");
            pickupOrders(localNow, ref actionRepo, pickableOrders);

        }

        while (!actionRepo.Select(kvp => kvp.Value.Actions).All(
            group => group.Select(x => x.ActionType).Contains(ActionType.Discard)
                || group.Select(x => x.ActionType).Contains(ActionType.Pickup)))
        {
            var localNow = _time.GetLocalNow().DateTime;
            pickupOrders(localNow, ref actionRepo, pickableOrders);
            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);
        }


        Console.WriteLine("");
        return actionRepo.Select(kvp => kvp.Value.Actions).SelectMany(x => x).ToList();
    }

    private static void pickupOrders(DateTime localNow, ref Dictionary<string, (PickableOrder Order, List<Action> Actions)> actionRepo, List<PickableOrder> pickableOrders)
    {
        // v3 works with actions repo
        if (true)
        {
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
