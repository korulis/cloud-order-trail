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
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 50, int min = 4, int max = 8)
    {
        try
        {
            var client = new Client(endpoint, auth);
            var problem = await client.NewProblemAsync(name, seed);

            // ------ Simulation harness logic goes here using rate, min and max ----
            var storage = new Dictionary<string, int>()
            {
                { Action.Cooler, 6 },
                { Action.Shelf, 12 },
                { Action.Heater, 6 }
            };
            var orders = problem.Orders;
            var simulation = new Simulation();
            List<Action> actions = await simulation.Simulate(TimeProvider.System, rate, storage, orders);

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
    public async Task<List<Action>> Simulate(TimeProvider time, int rate, Dictionary<string, int> storage, List<Order> orders)
    {

        var actions = new List<Action>();

        foreach (var order in orders)
        {
            Console.WriteLine($"Received: {order}");
            //   Console.WriteLine($"Received: {order}, expiring {expiringOrder.Expiration:HH:mm:ss.fff}");


            actions.Add(new Action(time.GetLocalNow().DateTime, order.Id, Action.Place, Action.Shelf));
            await Task.Delay(rate);
        }

        return actions;
    }

}
