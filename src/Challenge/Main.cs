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
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 500, int min = 4, int max = 8)
    {
        try
        {
            var client = new Client(endpoint, auth);
            var problem = await client.NewProblemAsync(name, seed);

            // ------ Simulation harness logic goes here using rate, min and max ----
            var storageLimits = new Dictionary<string, int>()
            {
                { Target.Cooler, 6 },
                { Target.Shelf, 12 },
                { Target.Heater, 6 }
            };
            var orders = problem.Orders;
            var simulation = new Simulation(TimeProvider.System);
            List<Action> actions = await simulation.Simulate(
                new Simulation.Config(rate * 1000, min * 1000_000, max * 1000_000, storageLimits),
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

