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
                { StorageTemp.Cooler, 6 },
                { StorageTemp.Shelf, 12 },
                { StorageTemp.Heater, 6 }
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

        var actions = new List<Action>();

        foreach (var order in orders)
        {
            Console.WriteLine($"Received: {order}");

            actions.Add(new Action(_time.GetLocalNow().DateTime, order.Id, ActionType.Place, order.Temp));
            await Task.Delay(TimeSpan.FromMilliseconds(rate), _time);
        }

        return actions;
    }

}
