using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 49, int min = 0, int max = 1)
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

public class RepoSemaphore : IDisposable
{
    private readonly SemaphoreSlim _slim;
    private bool _isDisposed = false;

    public RepoSemaphore()
    {
        _slim = new SemaphoreSlim(1, 1);
    }

    public Task WaitAsync()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(RepoSemaphore), "Cannot perform work on a disposed object.");
        }
        return _slim.WaitAsync();
    }
    public void Dispose()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(RepoSemaphore), "Cannot perform work on a disposed object.");
        }
        _slim.Release();
    }

    public void DisposeUndelying()
    {
        _slim.Dispose();
        _isDisposed = true;
    }
}

public class Simulation : IDisposable
{
    private static readonly List<PickableOrder> _pickableOrders = [];
    private static readonly Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo = new() { };

    private readonly ConcurrentDictionary<PickableOrder, RepoSemaphore> _repoSemaphores = new();

    private async Task<RepoSemaphore> GetOrCreateWaitingRepoSemaphore(PickableOrder order)
    {
        var semaphore = _repoSemaphores.GetOrAdd(order, new RepoSemaphore());
        await semaphore.WaitAsync();
        return semaphore;
    }


    /// <summary>
    /// Stores constants required for order handling simulation.
    /// </summary>
    /// <param name="rate">rate of order arrival in microseconds</param>
    /// <param name="min">minimum amount of microseconds before order is picked</param>
    /// <param name="max">maximum amount of microseconds before order is picked</param>
    /// <param name="storageLimits"> storage limits for different kinds of storage</param>
    public record Config(long rate, long min, long max, Dictionary<string, int> storageLimits);

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
        var placeOrderParallelism = 1;
        var orderPlacementTasks = new int[placeOrderParallelism].Select(x => PlaceOrders(config, orders, ct)).ToArray();
        var orderPickupTaskStream = orderPlacementTasks.Merge().WithCancellation(ct);

        var orderPickupTasks = new List<Task>();
        await foreach (var orderPickupTask in orderPickupTaskStream)
        {
            orderPickupTasks.Add(orderPickupTask);
        }

        await Task.WhenAll(orderPickupTasks);

        var result = _actionRepo.Select(kvp => kvp.Value.Actions).SelectMany(x => x).OrderBy(x => x.Timestamp).ToList();
        return result;
    }

    private int processedOrderCount() => _actionRepo
                .Select(kvp => kvp.Value.Actions)
                .Count(IsOrderProcessed);

    private static bool IsOrderProcessed(IEnumerable<Action> actions)
    {
        // todo could be optimised if we can afford to assume actions are sorted by timestamp
        return actions.Select(x => x.ActionType).Contains(ActionType.Discard)
                            || actions.Select(x => x.ActionType).Contains(ActionType.Pickup);
    }

    private async Task PickupSingleOrder(PickableOrder order, CancellationToken ct)
    {
        DateTime localNow = _time.GetLocalNow().DateTime;
        var delayAmount = order.PickupTime - localNow;
        // for tests, because of discrete time flow
        if (delayAmount < TimeSpan.Zero)
        {
            // for debug / dev purposes
            // if (TimeSpan.FromMilliseconds(1) - delayAmount > TimeSpan.Zero) throw new Exception($"Process is late to initiate order pickup process by more than 1 millisecond. Pickup time: {order.PickupTime:hh:mm:ss.fff} now: {localNow:hh:mm:ss.fff}");
            delayAmount = TimeSpan.Zero;
        }
        await Task.Delay(delayAmount, _time, ct);
        localNow = _time.GetLocalNow().DateTime;
        if (localNow != order.PickupTime)
        {
            // todo delete this
            // for debug / dev purposes
            // throw new Exception($"Current time does not coincide with designated order pickup time: {order.PickupTime:hh:mm:ss.fff} now: {localNow:hh:mm:ss.fff}");
        }

        using (var semaphore = await GetOrCreateWaitingRepoSemaphore(order))
        {
            var orderActions = _actionRepo[order.Id].Actions;
            orderActions.Sort((x, y) => Convert.ToInt32(x.Timestamp - y.Timestamp));
            if (IsOrderProcessed(orderActions))
            {
                return;
            }
            if (IsFresh(order, orderActions))
            {
                Action item = new(order.PickupTime, order.Id, ActionType.Pickup, orderActions.Last().Target);
                Console.WriteLine($"Picking up order {item}");
                orderActions.Add(item);
            }
            else
            {
                Action item = new(order.PickupTime, order.Id, ActionType.Discard, orderActions.Last().Target);
                Console.WriteLine($"Discarding order {item}");
                orderActions.Add(item);
            }
        }
    }

    private async IAsyncEnumerable<Task> PlaceOrders(Config config, List<Order> orders, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var order in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(order.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            PickableOrder pickableOrder = new(order, localNow.AddMicroseconds(pickupInMicroseconds));
            Task task = Task.CompletedTask;

            using (var placeSemaphore = await GetOrCreateWaitingRepoSemaphore(pickableOrder))
            {
                _pickableOrders.Add(pickableOrder);

                var actions = _actionRepo.Values.Select(x => x.Actions).SelectMany(x => x).ToList();
                if (IsShelf(target))
                {
                    _actionRepo[order.Id] = (pickableOrder, new() { });
                    _actionRepo[order.Id].Actions.Add(new(localNow, order.Id, ActionType.Place, target));
                    Console.WriteLine($"Order placed: {order}");
                }
                else
                {
                    if (IsFull(target, config.storageLimits, actions))
                    {
                        // add action repo entry
                        _actionRepo[order.Id] = (pickableOrder, new() { });
                        // update action repo entry
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
                
                task = PickupSingleOrder(pickableOrder, ct);
            }

            if (task == Task.CompletedTask)
            {
                throw new Exception($"Task was not created properly {JsonSerializer.Serialize(pickableOrder)}");
            }
            yield return task;
            // todo kb: use this
            // Use this to make thread wake up times more consistent, because order placement operations might have taken some time.
            // TimeSpan delay = targetTime - DateTime.Now;
            await Task.Delay(TimeSpan.FromMicroseconds(config.rate), _time, ct);

        }
    }

    private static bool IsShelf(string target)
    {
        return target == Target.Shelf;
    }

    private static bool IsFull(string target, Dictionary<string, int> storageLimits, List<Action> actions)
    {
        // checking if == is actually sufficient
        return storageLimits[target] <= actions.Count(x => x.Target == target && x.ActionType == ActionType.Place);
    }

    private static bool IsFull_Flawed(
        string target,
        Dictionary<string, int> storageLimits,
        Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo)
    {
        var entriesWithOrdersOnTarget = EntriesWithOrdersOnTarget_Flawed(_actionRepo, target).ToList();
        // checking if == is actually sufficient
        return storageLimits[target] <= entriesWithOrdersOnTarget.Count;
    }

    private static IEnumerable<KeyValuePair<string, (PickableOrder Order, List<Action> Actions)>> EntriesWithOrdersOnTarget_Flawed(
    Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo,
    string target)
    {
        var result = _actionRepo.Where(kvp =>
        {
            // kvp.Value.Actions.Sort((x, y) => Convert.ToInt32(x.Timestamp - y.Timestamp));
            var lastOrderAction = kvp.Value.Actions.Last();
            return lastOrderAction.Target == target && !IsFinal(lastOrderAction);
        }).ToList();
        return result;
    }

    private static bool IsFinal(Action lastOrderAction)
    {
        return lastOrderAction.ActionType == ActionType.Discard || lastOrderAction.ActionType == ActionType.Pickup;
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
        foreach (var item in _repoSemaphores)
        {
            item.Value.DisposeUndelying();
        }
        _repoSemaphores.Clear();
    }
}

// need this to be a reference type, so I can use it in code locking logic - key for dict of sempahores for actionsRepo
public class PickableOrder
{
    public DateTime PickupTime { get; init; }
    public string Id { get; init; }
    public string Name { get; init; }
    public string Temp { get; init; }
    public long Price { get; init; }
    public long Freshness { get; init; }

    public PickableOrder(string id, string name, string temp, long price, long freshness, DateTime pickupTime)
    {
        PickupTime = pickupTime;
        Id = id;
        Name = name;
        Temp = temp;
        Price = price;
        Freshness = freshness;
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
