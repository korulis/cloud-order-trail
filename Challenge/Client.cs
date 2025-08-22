using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Challenge;

/// <summary>
/// Order is a json-friendly representation of an order.
/// </summary>
/// <param name="Id">order id</param>
/// <param name="Name">food name</param>
/// <param name="Temp">ideal temperature</param>
/// <param name="Price">price in dollars</param>
/// <param name="Freshness">freshness in seconds</param>
record Order(string Id, string Name, string Temp, long Price, long Freshness);

record Problem(string TestId, List<Order> Orders);

/// <summary>
/// Action is a json-friendly representation of an action.
/// </summary>
/// <param name="timestamp">action timestamp</param>
/// <param name="id">order id</param>
/// <param name="action">place, move, pickup or discard</param>
/// <param name="target">heater, cooler or shelf. Target is the destination for move</param>
class Action(DateTime timestamp, string id, string action, string target) {
    public static readonly string Place = "place";
    public static readonly string Move = "move";
    public static readonly string Pickup = "pickup";
    public static readonly string Discard = "discard";

    public static readonly string Heater = "heater";
    public static readonly string Cooler = "cooler";
    public static readonly string Shelf = "shelf";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; } = (long)timestamp.Subtract(DateTime.UnixEpoch).TotalMicroseconds;
    [JsonPropertyName("id")]
    public string Id { get; init; } = id;
    [JsonPropertyName("action")]
    public string Action_ { get; init; } = action;
    [JsonPropertyName("target")]
    public string Target { get; init; } = target;
};

/// <summary>
/// Client is a client for fetching and solving challenge test problems
/// </summary>
class Client(string endpoint, string auth) {
    private readonly string endpoint = endpoint, auth = auth;
    private readonly HttpClient client = new();
    
    /// <summary>
    ///  NewProblemAsync fetches a new test problem from the server. The URL also works in a browser for convenience.
    /// </summary>
    public async Task<Problem> NewProblemAsync(string name, long seed = 0) {
        if (seed == 0) {
            seed = new Random().NextInt64();
        }

        var url = $"{endpoint}/interview/challenge/new?auth={auth}&name={name}&seed={seed}";         
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"{url}: {response.StatusCode}");
        }

        var id = response.Headers.GetValues("x-test-id").First();
        Console.WriteLine($"Fetched new test problem, id={id}: {url}");

        var orders = await response.Content.ReadFromJsonAsync<List<Order>>();
        return new Problem(id, orders ?? []);
    }

    class Options(TimeSpan rate, TimeSpan min, TimeSpan max) {
        [JsonPropertyName("rate")]
        public long Rate { get; init; } = (long)rate.TotalMicroseconds;
        [JsonPropertyName("min")]
        public long Min { get; init; } = (long)min.TotalMicroseconds;
        [JsonPropertyName("max")]
        public long Max { get; init; } = (long)max.TotalMicroseconds;
    };

    class Solution(Options options, List<Action> actions) {
        [JsonPropertyName("options")]
        public Options Options { get; init; } = options;
        [JsonPropertyName("actions")]
        public List<Action> Actions { get; init; } = actions;
    }

    /// <summary>
    /// SolveAsync submits a sequence of actions and parameters as a solution to a test problem. Returns test result.
    /// </summary>
    public async Task<string> SolveAsync(string testId, TimeSpan rate, TimeSpan min, TimeSpan max, List<Action> actions) {    
        var solution = new Solution(new Options(rate, min, max), actions);

        var url = $"{endpoint}/interview/challenge/solve?auth={auth}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-test-id", testId);
        request.Content = new StringContent(JsonSerializer.Serialize(solution), Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) {
            throw new Exception($"{url}: {response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync();
    }
}
