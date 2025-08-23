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

            var actions = new List<Action>();

            foreach (var order in problem.Orders)
            {
                Console.WriteLine($"Received: {order}");

                actions.Add(new Action(DateTime.Now, order.Id, Action.Place, Action.Cooler));
                await Task.Delay(rate);
            }

            // ----------------------------------------------------------------------

            var result = await client.SolveAsync(problem.TestId, TimeSpan.FromMilliseconds(rate), TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max), actions);
            Console.WriteLine($"Result: {result}");

        }
        catch (Exception e)
        {
            Console.WriteLine($"Simulation failed: {e}");
        }
    }
}
