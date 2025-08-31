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
    static async Task Main(string auth, string endpoint = "https://api.cloudkitchens.com", string name = "", long seed = 0, int rate = 49, int min = 1, int max = 2)
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

    public Task WaitAsync(CancellationToken ct)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(RepoSemaphore), "Cannot perform work on a disposed object.");
        }
        return _slim.WaitAsync(ct);
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

    private async Task<RepoSemaphore> GetOrCreateWaitingRepoSemaphore(PickableOrder order, CancellationToken ct)
    {
        var semaphore = _repoSemaphores.GetOrAdd(order, new RepoSemaphore());
        await semaphore.WaitAsync(ct);
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
        // can up this number once PlaceOrders is made reentrant.
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

    private static bool IsOrderProcessed(IEnumerable<Action> actions)
    {
        return new[] { ActionType.Discard, ActionType.Pickup }.Contains(actions.Select(x => x.ActionType).Last());
    }

    private async Task PickupSingleOrder(PickableOrder order, CancellationToken ct)
    {
        await WaitUntil(order.PickupTime, _time.GetLocalNow().DateTime, ct);

        var localNow = _time.GetLocalNow().DateTime;
        if (localNow != order.PickupTime)
        {
            // todo delete this
            // for debug / dev purposes
            // throw new Exception($"Current time does not coincide with designated order pickup time: {order.PickupTime:hh:mm:ss.fff} now: {localNow:hh:mm:ss.fff}");
        }

        using (var semaphore = await GetOrCreateWaitingRepoSemaphore(order, ct))
        {
            var orderActions = _actionRepo[order.Id].Actions;
            // it would actually be wrong to sort by time stamp from operational stand point 
            // ..even if output is sorted by timestamp
            // if need to sort should do that whennever action is added (always under semaphore)
            // orderActions.Sort((x, y) => Convert.ToInt32(x.Timestamp - y.Timestamp));
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
                // Action item = new(order.PickupTime, order.Id, ActionType.Discard, orderActions.Last().Target);
                // orderActions.Add(item);
                // Console.WriteLine($"Discarding order {item}");
            }
        }
    }

    private async IAsyncEnumerable<Task> PlaceOrders(Config config, List<Order> orders, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var simpleOrder in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            if (simpleOrder.Id == "o2")
            {
                Console.WriteLine($"AAAAAAAAAAAa Starting pickup {new Action(localNow, simpleOrder.Id, ActionType.Pickup, simpleOrder.Temp)}");
            }
            var target = ToTarget(simpleOrder.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            PickableOrder pickableOrder = new(simpleOrder, localNow.AddMicroseconds(pickupInMicroseconds));
            Task task = Task.CompletedTask;

            using (var placeSemaphore = await GetOrCreateWaitingRepoSemaphore(pickableOrder, ct))
            {
                // Console.WriteLine($"Starting pickup2 {new Action(localNow, order.Id, ActionType.Pickup, order.Temp)}");
                _pickableOrders.Add(pickableOrder);

                var actions = _actionRepo.Values.Select(x => x.Actions).SelectMany(x => x).ToList();
                if (IsShelf(target))
                {
                    await TryPlaceOnShelf(config, localNow, pickableOrder, ct);
                }
                else
                {
                    if (IsFull(target, config.storageLimits, _actionRepo))
                    {
                        await TryPlaceOnShelf(config, localNow, pickableOrder, ct);
                    }
                    else
                    {
                        PlaceOrderOnTarget(localNow, target, pickableOrder);
                    }
                }

                task = PickupSingleOrder(pickableOrder, ct);
            }

            if (task == Task.CompletedTask)
            {
                throw new Exception($"Task was not created properly {JsonSerializer.Serialize(pickableOrder)}");
            }
            yield return task;

            await WaitUntil(localNow.AddMicroseconds(config.rate), localNow, ct);
        }
    }

    private static void PlaceOrderOnTarget(DateTime localNow, string target, PickableOrder pickableOrder)
    {
        _actionRepo[pickableOrder.Id] = (pickableOrder, new() { });
        Action action = new(localNow, pickableOrder.Id, ActionType.Place, target);
        _actionRepo[pickableOrder.Id].Actions.Add(action);
        Console.WriteLine($"Placing order: {action}");
    }

    private async Task TryPlaceOnShelf(Config config, DateTime localNow, PickableOrder pickableOrder, CancellationToken ct)
    {
        var target = Target.Shelf;
        if (IsFull(target, config.storageLimits, _actionRepo))
        {
            // move / discard
            var kvpsWithOrdersOnTarget = KvpsWithOrdersOnTarget(_actionRepo, target);
            if (!kvpsWithOrdersOnTarget.Any())
            {
                // handle error from race condition
                throw new Exception("the list is migh be empty bcs of race condition");
            }
            var entriesForOrdersToMove = EntriesForForeignOrdersOnTarget(kvpsWithOrdersOnTarget);
            PickableOrder? orderToMove = null;

            foreach (var entryForOrderToMove in entriesForOrdersToMove)
            {

                //         // try move from shelf
                var targetToMoveTo = ToTarget(entryForOrderToMove.Order.Temp);
                // Console.WriteLine("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAa " + entryForOrderToMove.Order.Id);
                if (!IsFull(targetToMoveTo, config.storageLimits, _actionRepo))
                {
                    orderToMove = entryForOrderToMove.Order;
                    break;
                }

            } //end for

            if (orderToMove is not null)
            {
                using var moveSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToMove, ct);
                // check if can move first
                // if can not move handle error from race condition.
                string moveToTarget = ToTarget(orderToMove.Temp);
                MoveOrderToTarget(orderToMove, moveToTarget);
            }
            else
            {
                var kvpToDiscard = CalculateOrderToDiscard(kvpsWithOrdersOnTarget);
                // need to discard
                var orderToDiscard = kvpToDiscard.Value.Order;
                using var discardSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToDiscard, ct);
                // check if can discard
                // if can not discard ... assume moved or processed .. therefore do nothing.
                string discardFromTarget = kvpToDiscard.Value.Actions.Last().Target;
                DiscardOrderFromTarget(orderToDiscard, discardFromTarget);
            }


        }

        // todo kb: should exit placement and reenter instead.
        // place after move/discard
        PlaceOrderOnTarget(localNow, target, pickableOrder);
    }

    private void DiscardOrderFromTarget(PickableOrder orderToDiscard, string discardFromTarget)
    {
        Action discardAction = new(_time.GetLocalNow().DateTime, orderToDiscard.Id, ActionType.Discard, discardFromTarget);
        _actionRepo[orderToDiscard.Id].Actions.Add(discardAction);
        Console.WriteLine($"Discarding order: {discardAction}");
    }

    private void MoveOrderToTarget(PickableOrder orderToMove, string target1)
    {
        Action moveAction = new(_time.GetLocalNow().DateTime, orderToMove.Id, ActionType.Move, target1);
        _actionRepo[orderToMove.Id].Actions.Add(
                          moveAction);
        Console.WriteLine($"Moving order: {moveAction}");
    }


    private KeyValuePair<string, (PickableOrder Order, List<Action> Actions)> CalculateOrderToDiscard(
              List<KeyValuePair<string, (PickableOrder Order, List<Action> Actions)>> kvpsWithOrdersOnTarget)
    {
        // temp implementation
        return kvpsWithOrdersOnTarget.First();
    }

    private async Task WaitUntil(DateTime targetTime, DateTime baseLocalNow, CancellationToken ct)
    {
        // new local now might be significantly different
        var newLocalNow = _time.GetLocalNow().DateTime;
        TimeSpan delay = targetTime - newLocalNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
            Console.WriteLine($"WARNING: forced to override delay time. {(targetTime, newLocalNow)}");
        }
        await Task.Delay(delay, _time, ct);
    }

    private static bool IsShelf(string target)
    {
        return target == Target.Shelf;
    }


    private static List<(PickableOrder Order, List<Action> Actions)> EntriesForForeignOrdersOnTarget(
    List<KeyValuePair<string, (PickableOrder Order, List<Action> Actions)>> kvpsWithOrdersOnTarget)
    {
        var result = kvpsWithOrdersOnTarget
        .Where(kvp => kvp.Value.Actions.Last().Target != ToTarget(kvp.Value.Order.Temp))
        .Select(kvp => kvp.Value)
        .ToList();
        return result;
    }

    private static bool IsFull(
        string target,
        Dictionary<string, int> storageLimits,
        Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo)
    {
        var entriesWithOrdersOnTarget = KvpsWithOrdersOnTarget(_actionRepo, target).ToList();
        // checking if == is actually sufficient
        return storageLimits[target] <= entriesWithOrdersOnTarget.Count;
    }

    private static List<KeyValuePair<string, (PickableOrder Order, List<Action> Actions)>> KvpsWithOrdersOnTarget(
    Dictionary<string, (PickableOrder Order, List<Action> Actions)> _actionRepo,
    string target)
    {
        var result = _actionRepo.Where(kvp =>
        {
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

// need this to be record, so it can be created on different threads and still be recognised as the same entity while being use as a key in dictionary
public record PickableOrder
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
