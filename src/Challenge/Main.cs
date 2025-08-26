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
            List<Action> actions = await simulation.Simulate(rate, storage, orders);

            // ----------------------------------------------------------------------

            var result = await client.SolveAsync(problem.TestId, TimeSpan.FromMilliseconds(rate), TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max), actions);
            Console.WriteLine($"Result: {result}");

        }
        catch (Exception e)
        {
            Console.WriteLine($"Simulation failed: {e}");
        }
    }

    public static float GenerateRandomFloat(float min, float max)
    {
        var result = (float)(min + (new Random().NextDouble() * (max - min)));
        return result;
    }

}

public class Simulation
{
    private readonly TimeProvider _time;

    /// <summary>
    /// Simulation representing module
    /// </summary>
    /// <param name="time"> Date and time measure providing object </param>
    public Simulation(TimeProvider time)
    {
        _time = time;
    }

    public async Task<List<Action>> Simulate(int rate, Dictionary<string, int> storage, List<Order> orders)
    {
        DateTime localNow = _time.GetLocalNow().DateTime;

        var actions = new List<Action>();

        foreach (var order in orders)
        {
            var target = ToTarget(order.Temp);
            if ((new[] { Target.Cooler, Target.Heater }).Contains(target))
            {
                // checking if == is actually sufficient
                if (storage[target] <= actions.Count(x => x.Target == target && x.ActionType == ActionType.Place))
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
            await Task.Delay(TimeSpan.FromMilliseconds(rate), _time);
            //pickup
            Action pickupAction = new(localNow, order.Id, ActionType.Pickup, target);
            actions.Add(pickupAction);
            Console.WriteLine($"Order picked: {pickupAction}");

            // actions.Add(new(localNow, order.Id, ActionType.Place, target));
            // Console.WriteLine($"Order placed: {order}");
            // await Task.Delay(TimeSpan.FromMilliseconds(rate), _time);
            // pickup
            // Action pickupAction = new(localNow, order.Id, ActionType.Pickup, Target.Heater);
            // actions.Add(pickupAction);
            // Console.WriteLine($"Order picked: {pickupAction}");

        }

        Console.WriteLine("");
        return actions;
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
