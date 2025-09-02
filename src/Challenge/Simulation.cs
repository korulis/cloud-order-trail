using System.Collections.Concurrent;
using System.CommandLine.Parsing;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Challenge;

using RepoEntry = (PickableOrder Order, List<Action> Actions);
using ActionMarker = (string Id, string ActionType, string Target);

public class Simulation : IDisposable
{
    private readonly List<Order> _pickableOrders = [];
    private readonly Dictionary<string, RepoEntry> _actionRepo = new() { };
    private readonly ConcurrentDictionary<Order, RepoSemaphore> _repoSemaphores = new();
    private readonly ConcurrentDictionary<ActionMarker, Task> _actionTaskRepo = new();


    private async Task<RepoSemaphore> GetOrCreateWaitingRepoSemaphore(Order order, CancellationToken ct)
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
    private static async IAsyncEnumerable<Order> GetOrdersAsAsyncStream(List<Order> orders)
    {
        foreach (var order in orders)
        {
            await Task.Delay(TimeSpan.FromTicks(1));
            yield return order;
        }
    }

    public async Task PlaceSingleOrderAt(Config config, Order order, DateTime placementTime, CancellationToken ct)
    {
        var pickupTime = GetPickupTime(config, placementTime);
        await WaitUntil(placementTime, ct);

        // have consisten local now after waiting
        var localNow = _time.GetLocalNow().DateTime;
        using (var placeSemaphore = await GetOrCreateWaitingRepoSemaphore(order, ct))
        {
            _pickableOrders.Add(order);

        }
    }

    private static DateTime GetPickupTime(Config config, DateTime placementTime)
    {
        return placementTime + TimeSpan.FromMicroseconds(new Random().NextInt64(config.min, config.max));
    }

    public async Task<List<Action>> Simulate(Config config, List<Order> orders, CancellationToken ct)
    {
        var localNow = _time.GetLocalNow().DateTime;

        // todo kb: convert orders to IAsyncEnumerable<Order> stream and try to use that instead of List
        // that would demonstrate that orders can even come as an async stream from, say, a http multipart request handling
        // this would probably require splitting incoming orders (order placement tasks) 
        // and tasks being spawned (followup order actions) into two separate lists
        // ...not lists, but IAsyncEnumerable streams, then merge them and then await foreach to completion

        orders.Select((Order x, int i) =>
        {
            var placementTask = PlaceSingleOrderAt(config, x, localNow + TimeSpan.FromMicroseconds(i * config.rate), ct);
            var key = (x.Id, ActionType: ActionType.Place, Target: ToTarget(x.Temp));
            _actionTaskRepo.TryAdd(key, placementTask);
            return i;
        }).ToList();

        // the condition here could be also be checking list of completed orders.
        while (_actionTaskRepo.Values.Any(x => !x.IsCompleted))
        {
            IEnumerable<Task> values = _actionTaskRepo.Values.Where(x => !x.IsCompleted);
            Task completedTask = await Task.WhenAny(values);
            // rethrow exceptions if any
            await completedTask;
        }



        // // can up this number once PlaceOrders is made reentrant.
        // var placeOrderParallelism = 1;
        // var orderPlacementTasks = new int[placeOrderParallelism].Select(x => PlaceOrders(config, orders, ct)).ToArray();
        // var orderPickupTaskStream = orderPlacementTasks.Merge().WithCancellation(ct);

        // var orderPickupTasks = new List<Task>();
        // await foreach (var orderPickupTask in orderPickupTaskStream)
        // {
        //     orderPickupTasks.Add(orderPickupTask);
        // }

        // await Task.WhenAll(orderPickupTasks);

        var result = _actionRepo.Select(kvp => kvp.Value.Actions).SelectMany(x => x).OrderBy(x => x.Timestamp).ToList();
        return result;
    }

    private static bool IsOrderProcessed(IEnumerable<Action> actions)
    {
        return IsFinal(actions.Last());
    }

    private async Task PickupSingleOrder(PickableOrder orderToPickup, CancellationToken ct)
    {
        await WaitUntil(orderToPickup.PickupTime, ct);

        var localNow = _time.GetLocalNow().DateTime;

        using (var semaphore = await GetOrCreateWaitingRepoSemaphore(orderToPickup.Order, ct))
        {
            var orderActions = _actionRepo[orderToPickup.Id].Actions;
            if (IsOrderProcessed(orderActions))
            {
                return;
            }
            if (IsFresh(orderToPickup, orderActions, localNow))
            {
                string pickupFromTarget = orderActions.Last().Target;
                PickupOrderFromTargetAt(orderToPickup, pickupFromTarget, orderToPickup.PickupTime);
            }
            else
            {
                string discardFromTarget = orderActions.Last().Target;
                DiscardOrderFromTargetAt(orderToPickup, discardFromTarget, orderToPickup.PickupTime);
            }
        }
    }

    private void PickupOrderFromTargetAt(PickableOrder order, string pickupFromTarget, DateTime pickupTime)
    {
        Action pickupAction = new(pickupTime, order.Id, ActionType.Pickup, pickupFromTarget);
        _actionRepo[order.Id].Actions.Add(pickupAction);
        Console.WriteLine($"Picking up order: {pickupAction,100}");
    }

    private async IAsyncEnumerable<Task> PlaceOrders(Config config, List<Order> orders, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var simpleOrder in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(simpleOrder.Temp);

            var pickupInMicroseconds = PickableOrder.RandomBetween(config.min, config.max);
            PickableOrder pickableOrder = new(simpleOrder, localNow.AddMicroseconds(pickupInMicroseconds));
            Task task = Task.CompletedTask;

            using (var placeSemaphore = await GetOrCreateWaitingRepoSemaphore(simpleOrder, ct))
            {
                _pickableOrders.Add(simpleOrder);

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

            await WaitUntil(localNow.AddMicroseconds(config.rate), ct);
        }
    }

    private void PlaceOrderOnTarget(DateTime localNow, string target, PickableOrder pickableOrder)
    {
        _actionRepo[pickableOrder.Id] = (pickableOrder, new() { });
        Action action = new(localNow, pickableOrder.Id, ActionType.Place, target);
        _actionRepo[pickableOrder.Id].Actions.Add(action);
        Console.WriteLine($"Placing order: {action,100}");
    }

    private async Task TryPlaceOnShelf(Config config, DateTime localNow, PickableOrder pickableOrder, CancellationToken ct)
    {
        var target = Target.Shelf;
        if (IsFull(target, config.storageLimits, _actionRepo))
        {
            var kvpsWithOrdersOnTarget = KvpsWithOrdersOnTarget(_actionRepo, target);
            var entriesForOrdersToMove = EntriesForForeignOrdersOnTarget(kvpsWithOrdersOnTarget);
            PickableOrder? orderToMove = null;

            foreach (var entryForOrderToMove in entriesForOrdersToMove)
            {
                var targetToMoveTo = ToTarget(entryForOrderToMove.Order.Temp);
                if (!IsFull(targetToMoveTo, config.storageLimits, _actionRepo))
                {
                    orderToMove = entryForOrderToMove.Order;
                    break;
                }
            }

            if (orderToMove is not null)
            {
                using var moveSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToMove.Order, ct);
                string moveToTarget = ToTarget(orderToMove.Temp);
                MoveOrderToTargetAt(orderToMove, moveToTarget, localNow);
            }
            else
            {
                var kvpToDiscard = CalculateOrderToDiscard(kvpsWithOrdersOnTarget);
                var orderToDiscard = kvpToDiscard.Value.Order;
                using var discardSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToDiscard.Order, ct);
                string discardFromTarget = kvpToDiscard.Value.Actions.Last().Target;
                DiscardOrderFromTargetAt(orderToDiscard, discardFromTarget, localNow);
            }


        }

        PlaceOrderOnTarget(localNow, target, pickableOrder);
    }

    private void DiscardOrderFromTargetAt(PickableOrder orderToDiscard, string discardFromTarget, DateTime discardAt)
    {
        Action discardAction = new(discardAt, orderToDiscard.Id, ActionType.Discard, discardFromTarget);
        _actionRepo[orderToDiscard.Id].Actions.Add(discardAction);
        Console.WriteLine($"Discarding order: {discardAction,100}");
    }

    private void MoveOrderToTargetAt(PickableOrder orderToMove, string target1, DateTime moveAt)
    {
        Action moveAction = new(moveAt, orderToMove.Id, ActionType.Move, target1);
        _actionRepo[orderToMove.Id].Actions.Add(
                          moveAction);
        Console.WriteLine($"Moving order: {moveAction,100}");
    }


    private KeyValuePair<string, RepoEntry> CalculateOrderToDiscard(
              List<KeyValuePair<string, RepoEntry>> kvpsWithOrdersOnTarget)
    {
        return kvpsWithOrdersOnTarget.First();
    }

    private async Task WaitUntil(DateTime targetTime, CancellationToken ct)
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


    private static List<RepoEntry> EntriesForForeignOrdersOnTarget(
    List<KeyValuePair<string, RepoEntry>> kvpsWithOrdersOnTarget)
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
        Dictionary<string, RepoEntry> _actionRepo)
    {
        var entriesWithOrdersOnTarget = KvpsWithOrdersOnTarget(_actionRepo, target).ToList();
        return storageLimits[target] <= entriesWithOrdersOnTarget.Count;
    }

    private static List<KeyValuePair<string, RepoEntry>> KvpsWithOrdersOnTarget(
    Dictionary<string, RepoEntry> _actionRepo,
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

    private static bool IsFresh(PickableOrder order, List<Action> orderActions, DateTime localNow)
    {
        var spoilage = Spoilage(order, orderActions, localNow);
        var result = spoilage <= TimeSpan.FromSeconds(order.Freshness);
        return result;
    }

    private static TimeSpan Spoilage(PickableOrder order, List<Action> orderActions, DateTime localNow)
    {
        TimeSpan spoilage;
        var placementTime = orderActions.First().GetOriginalTimestamp();
        if (!IsShelf(ToTarget(order.Temp)))
        {
            if (IsShelf(orderActions.First().Target))
            {
                if (orderActions.Count >= 2 && orderActions[1].ActionType == ActionType.Move)
                {
                    var moveTime = orderActions[1].GetOriginalTimestamp();
                    spoilage = (localNow - moveTime) + (moveTime - placementTime) * 2;
                }
                else
                {
                    spoilage = (localNow - placementTime) * 2;
                }
            }
            else
            {
                spoilage = localNow - placementTime;
            }
        }
        else
        {
            spoilage = localNow - placementTime;
        }

        return spoilage;
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

// need this to be record, so it can be created on different threads and still be recognised as the same entity while being use as a key in dictionary
public record PickableOrder
{
    public DateTime PickupTime { get; init; }
    public Order Order { get; init; }
    public string Id => Order.Id;
    public string Name => Order.Name;
    public string Temp => Order.Temp;
    public long Price => Order.Price;
    public long Freshness => Order.Freshness;

    public PickableOrder(Order order, DateTime pickupTime)
    {
        PickupTime = pickupTime;
        Order = order;
    }

    // todo kb: delete
    public static long RandomBetween(long min, long max)
    {
        var result = new Random().NextInt64(min, max);
        return result;
    }

}
