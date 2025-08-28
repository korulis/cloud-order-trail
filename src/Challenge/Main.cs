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

        var actions = new List<Action>();

        List<PickableOrder> pickableOrders = [];

        foreach (var order in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(order.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            pickableOrders.Add(new PickableOrder(order, localNow.AddMicroseconds(pickupInMicroseconds)));

            if ((new[] { Target.Cooler, Target.Heater }).Contains(target))
            {
                // checking if == is actually sufficient
                if (config.storage[target] <= actions.Count(x => x.Target == target && x.ActionType == ActionType.Place))
                {
                    actions.Add(new(localNow, order.Id, ActionType.Place, Target.Shelf));
                    Console.WriteLine($"Order placed: {order}");
                }
                else
                {
                    actions.Add(new(localNow, order.Id, ActionType.Place, target));
                    Console.WriteLine($"Order placed: {order}");
                }
            }
            else
            {
                actions.Add(new(localNow, order.Id, ActionType.Place, target));
                Console.WriteLine($"Order placed: {order}");
            }
            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);


            // actions.Add(new(localNow, order.Id, ActionType.Place, target));
            // Console.WriteLine($"Order placed: {order}");
            // await Task.Delay(TimeSpan.FromMilliseconds(rate), _time);
            // pickup
            // Action pickupAction = new(localNow, order.Id, ActionType.Pickup, Target.Heater);
            // actions.Add(pickupAction);
            // Console.WriteLine($"Order picked: {pickupAction}");
            pickupOrders(localNow, ref actions, pickableOrders);

        }

        while (!actions.GroupBy(x => x.Id).All(
            group => group.Select(x => x.ActionType).Contains(ActionType.Discard)
                || group.Select(x => x.ActionType).Contains(ActionType.Pickup)))
        {
            var localNow = _time.GetLocalNow().DateTime;
            pickupOrders(localNow, ref actions, pickableOrders);
            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);
        }


        Console.WriteLine("");
        return actions;
    }

    private static void pickupOrders(DateTime localNow, ref List<Action> actions, IEnumerable<PickableOrder> pickableOrders)
    {
        // v2
        if(true)
        {
            var postPickupTimeOrders = pickableOrders.Where(x => x.PickupTime <= localNow).ToList();

            var lastActionsForUnpickedOrders = actions
                .GroupBy(x => x.Id)
                .Select(group => group.OrderBy(x => x.Timestamp).ToList())
                .Where(group => new[] { ActionType.Place, ActionType.Move }.Contains(group.Last().ActionType))
                .ToList();

            var pickupActions = postPickupTimeOrders.Join(
                lastActionsForUnpickedOrders,
                o => o.Id,
                a => a.Last().Id,
                (o, acts) =>
                {
                    var result = IsFresh(o, acts)
                    ? new Action(localNow, o.Id, ActionType.Pickup, acts.Last().Target)
                    : new Action(localNow, o.Id, ActionType.Discard, acts.Last().Target);
                    return result;
                })
                .ToList();

            if (pickupActions.Count > 0) Console.WriteLine($"Adding some pickups\n {string.Join("\n", pickupActions)}");
            actions.AddRange(pickupActions);
            // todo delete implementation and add one condition at a time.

        }

        // v1
        if(false)
        {

            // preclude collection modification problems in loop
            var oldActions = actions[..actions.Count];



            var placedOrderIds = oldActions.Where(x => x.ActionType == ActionType.Place)
                .Select(x => x.Id)
                .ToList();
            var removedOrderIds = oldActions.Where(x => x.ActionType == ActionType.Pickup || x.ActionType == ActionType.Discard)
                .Select(x => x.Id)
                .ToList();
            var currentOrderIds = placedOrderIds.Except(removedOrderIds).ToList();

            var lastActions = oldActions
                .Where(x => currentOrderIds.Contains(x.Id))
                .GroupBy(x => x.Id)
                .Select(x => x.OrderBy(a => a.Timestamp).Last())
                .ToList();

            var postPickupTimeOrders = pickableOrders.Where(x => x.PickupTime <= localNow);


            var pickupActions = lastActions
                .Join(
                    postPickupTimeOrders,
                    a => a.Id,
                    o => o.Id,
                    (a, o) => new { Action = a, Order = o }
                )
                // if fresh....
                .Select(x => new Action(localNow, x.Order.Id, ActionType.Pickup, x.Action.Target))
                .ToList();

            if (pickupActions.Count > 0) Console.WriteLine($"Adding some pickups {string.Join(", ", pickupActions)}");
            actions.AddRange(pickupActions);
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
