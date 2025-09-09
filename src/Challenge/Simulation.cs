using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Challenge;

using RepoEntry = (Order Order, List<Action> Actions);
// todo kb: dont need Target because this DTO is only for uniqueness, not information. Information of target is in the Task itself.
using Command = (Order Order, string ActionType, string Target);

public class Simulation : IDisposable
{
    private readonly List<Order> _pickableOrders = [];
    // this is like read model in CQRS. It should be available for read at all times. Not locked. .ToList() should be used before reading.
    private readonly ConcurrentBag<Action> _actionLedger = [];
    private readonly Dictionary<string, RepoEntry> _orderRepo = new() { };
    private readonly ConcurrentDictionary<Order, RepoSemaphore> _orderRepoSemaphores = new();
    private readonly ConcurrentDictionary<Command, Task> _commandHandlerRepo = new();
    // private readonly Dictionary<string, List<Order>> _storageRepo = new() { };
    // private readonly ConcurrentDictionary<string, RepoSemaphore> _storageRepoSemaphores = new();


    // private async Task<RepoSemaphore> GetOrCreateWaitingStorageRepoSemaphore(string target, CancellationToken ct)
    // {
    //     var semaphore = _storageRepoSemaphores.GetOrAdd(target, new RepoSemaphore());
    //     await semaphore.WaitAsync(ct);
    //     return semaphore;
    // }

    /// <summary>
    /// Meant to lock order related operations. Order mutating operations should happen only in the scope of provided semaphore.
    /// </summary>
    /// <param name="order"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task<RepoSemaphore> GetOrCreateWaitingOrderRepoSemaphore(Order order, CancellationToken ct)
    {
        var semaphore = _orderRepoSemaphores.GetOrAdd(order, new RepoSemaphore());
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

    // todo kb: remove unused methods.
    private static async IAsyncEnumerable<Order> GetOrdersAsAsyncStream(List<Order> orders)
    {
        foreach (var order in orders)
        {
            await Task.Delay(TimeSpan.FromTicks(1));
            yield return order;
        }
    }

    public async Task TryPlaceOrderAt(Config config, Order order, DateTime placementTime, string target, CancellationToken ct)
    {
        // todo kb: export this outside of this fct
        await WaitUntil(placementTime, ct);

        // have consisten local now after waiting
        var localNow = _time.GetLocalNow().DateTime;

        if (IsShelf(target))
        {
            await TryPlaceOnShelf(config, localNow, order, ct);
        }
        else
        {
            if (IsFull(target, config.storageLimits, _orderRepo))
            {
                await TryPlaceOnShelf(config, localNow, order, ct);
            }
            else
            {
                await HardPlace(localNow, target, order, config, ct);
            }
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
            var placementTask = TryPlaceOrderAt(
                config,
                x,
                localNow + TimeSpan.FromMicroseconds(i * config.rate),
                ToTarget(x.Temp),
                ct);
            var key = (x, ActionType: ActionType.Place, Target: ToTarget(x.Temp));
            // Ignore failure. If this fails - it is ok, that means somebody already created the task we need.
            _commandHandlerRepo.TryAdd(key, placementTask);
            return i;
        }).ToList();

        // the condition here could be also be checking list of completed orders.
        while (_commandHandlerRepo.Values.Any(x => !x.IsCompleted))
        {
            IEnumerable<Task> values = _commandHandlerRepo.Values.Where(x => !x.IsCompleted);
            Task completedTask = await Task.WhenAny(values);
            // rethrow exceptions if any
            await completedTask;
        }

        var result = _orderRepo.Select(kvp => kvp.Value.Actions).SelectMany(x => x).OrderBy(x => x.Timestamp).ToList();
        return result;
    }

    private static bool IsOrderProcessed(IEnumerable<Action> actions)
    {
        return IsFinal(actions.Last());
    }

    private async Task TryPickupSingleOrder(Order orderToPickup, DateTime pickupTime, CancellationToken ct)
    {
        await WaitUntil(pickupTime, ct);

        var localNow = _time.GetLocalNow().DateTime;

        using (var semaphore = await GetOrCreateWaitingOrderRepoSemaphore(orderToPickup, ct))
        {
            var orderActions = _orderRepo[orderToPickup.Id].Actions;
            if (IsOrderProcessed(orderActions))
            {
                return;
            }
            if (IsFresh(orderToPickup, orderActions, localNow))
            {
                string pickupFromTarget = orderActions.Last().Target;
                await HardPickup(orderToPickup, pickupFromTarget, localNow, ct);
            }
            else
            {
                string discardFromTarget = orderActions.Last().Target;
                await HardDiscard(orderToPickup, discardFromTarget, localNow, ct);
            }
        }
    }

    private async Task HardPickup(Order order, string pickupFromTarget, DateTime localNow, CancellationToken ct)
    {
        // todo kb: use default scope syntax
        using (var semaphore = await GetOrCreateWaitingOrderRepoSemaphore(order, ct))
        {
            Action pickupAction = new(localNow, order.Id, ActionType.Pickup, pickupFromTarget);
            _actionLedger.Add(pickupAction);
            _orderRepo[order.Id].Actions.Add(pickupAction);
            Console.WriteLine($"Picking up order: {pickupAction,100}");
        }
    }

    private async IAsyncEnumerable<Task> PlaceOrders(Config config, List<Order> orders, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var order in orders)
        {
            var localNow = _time.GetLocalNow().DateTime;
            var target = ToTarget(order.Temp);

            Task task = Task.CompletedTask;

            using (var placeSemaphore = await GetOrCreateWaitingOrderRepoSemaphore(order, ct))
            {
                _pickableOrders.Add(order);

                var actions = _orderRepo.Values.Select(x => x.Actions).SelectMany(x => x).ToList();
                if (IsShelf(target))
                {
                    await TryPlaceOnShelf(config, localNow, order, ct);
                }
                else
                {
                    if (IsFull(target, config.storageLimits, _orderRepo))
                    {
                        await TryPlaceOnShelf(config, localNow, order, ct);
                    }
                    else
                    {
                        var placemenTime = localNow;
                        await HardPlace(placemenTime, target, order, config, ct);
                    }
                }

            }

            if (task == Task.CompletedTask)
            {
                throw new Exception($"Task was not created properly {JsonSerializer.Serialize(order)}");
            }
            yield return task;

            await WaitUntil(localNow.AddMicroseconds(config.rate), ct);
        }
    }

    private async Task HardPlace(
        // todo kb: think about why shouldnt all placementTimes and pickupTimes just be continuously sourced from provided instead.
        DateTime placementTime,
        string target,
        Order order,
        Config config,
        CancellationToken ct)
    {
        using (var semaphore = await GetOrCreateWaitingOrderRepoSemaphore(order, ct))
        // todo kb: storage repo target
        {
            // todo kb: do required checks here again
            // todo kb: use storage repo here instead
            // todo kb: fix this ridiculous signature
            // if (!IsFull(target, config.storageLimits, _orderRepo))
            // {
            _orderRepo[order.Id] = (order, new() { });
            Action action = new(placementTime, order.Id, ActionType.Place, target);
            _orderRepo[order.Id].Actions.Add(action);
            _actionLedger.Add(action);
            _pickableOrders.Add(order);
            Console.WriteLine($"Placing order: {action,100}");

            // schedule followup
            var pickupTime = GetPickupTime(config, placementTime);
            var pickupTask = TryPickupSingleOrder(order, pickupTime, ct);
            _commandHandlerRepo[(order, ActionType.Pickup, Target.FromWherever)] = pickupTask;
            // }
            // else
            // {

            //     var placementRetryTask = TryPlaceOrderAt(config, order, placementTime, target, ct);
            //     _commandHandlerRepo.TryAdd((order, ActionType.Place, target), placementRetryTask);
            // }

            //todo kb: need to put actions into both order repo for locking and action ledger for readpurposes.. order repo is not always avialable full which is bad for some calculations

        }

    }

    private async Task TryPlaceOnShelf(Config config, DateTime localNow, Order order, CancellationToken ct)
    {
        if (IsFull(Target.Shelf, config.storageLimits, _orderRepo))
        {
            Command followupCommand = (order, ActionType.Place, Target.Shelf);
            await TrySolveFullShelf(config, localNow, followupCommand, ct);
        }
        var placementTime = localNow;
        await HardPlace(placementTime, Target.Shelf, order, config, ct);
    }

    private async Task TrySolveFullShelf(Config config, DateTime localNow, Command followup, CancellationToken ct)
    {
        var kvpsWithOrdersOnShelf = KvpsWithOrdersOnTarget(_orderRepo, Target.Shelf);
        var entriesForOrdersToMove = EntriesForForeignOrdersOnShelf(kvpsWithOrdersOnShelf);
        Order? orderToMove = null;

        foreach (var entryForOrderToMove in entriesForOrdersToMove)
        {
            var targetToMoveTo = ToTarget(entryForOrderToMove.Order.Temp);
            if (!IsFull(targetToMoveTo, config.storageLimits, _orderRepo))
            {
                orderToMove = entryForOrderToMove.Order;
                break;
            }
        }

        if (orderToMove is not null)
        {
            string moveToTarget = ToTarget(orderToMove.Temp);
            await HardMove(config, orderToMove, moveToTarget, localNow, followup, ct);
        }
        else
        {
            var kvpToDiscard = CalculateOrderToDiscard(kvpsWithOrdersOnShelf);
            var orderToDiscard = kvpToDiscard.Value.Order;
            using var discardSemaphore = await GetOrCreateWaitingOrderRepoSemaphore(orderToDiscard, ct);
            string discardFromTarget = kvpToDiscard.Value.Actions.Last().Target;
            // todo kb: pass followup as parameter here too.
            await HardDiscard(orderToDiscard, discardFromTarget, localNow, ct);
        }
    }

    private async Task HardDiscard(Order order, string discardFromTarget, DateTime discardAt, CancellationToken ct)
    {
        using (var semaphore = await GetOrCreateWaitingOrderRepoSemaphore(order, ct))
        {
            Action action = new(discardAt, order.Id, ActionType.Discard, discardFromTarget);
            _actionLedger.Add(action);
            _orderRepo[order.Id].Actions.Add(action);
            Console.WriteLine($"Discarding order: {action,100}");
        }
    }

    private async Task HardMove(
        Config config,
        Order order,
        string target,
        DateTime moveAt,
        Command followup,
        CancellationToken ct)
    {
        using (var semaphore = await GetOrCreateWaitingOrderRepoSemaphore(order, ct))
        {
            // todo kb: do required checks here again
            // if allow, then move and schedule followup, else .. only schedule followup

            Action action = new(moveAt, order.Id, ActionType.Move, target);
            _orderRepo[order.Id].Actions.Add(action);
            _actionLedger.Add(action);
            Console.WriteLine($"Moving order: {action,100}");

            // schedule followup
            var orderToPlace = followup.Order;
            var placementTime = moveAt;
            var placementTask = TryPlaceOrderAt(config, orderToPlace, placementTime, followup.Target, ct);
            _commandHandlerRepo.TryAdd((orderToPlace, ActionType.Pickup, Target.FromWherever), placementTask);

        }
    }


    private KeyValuePair<string, RepoEntry> CalculateOrderToDiscard(
              List<KeyValuePair<string, RepoEntry>> kvpsWithOrdersOnTarget)
    {
        return kvpsWithOrdersOnTarget.First();
    }

    private async Task WaitUntil(DateTime targetWaitTime, CancellationToken ct)
    {
        // new local now might be significantly different
        var localNow = _time.GetLocalNow().DateTime;
        TimeSpan delay = targetWaitTime - localNow;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
            Console.WriteLine($"WARNING: forced to override delay time. {(targetWaitTime, localNow)}");
        }
        await Task.Delay(delay, _time, ct);
    }

    private static bool IsShelf(string target)
    {
        return target == Target.Shelf;
    }


    private static List<RepoEntry> EntriesForForeignOrdersOnShelf(
    List<KeyValuePair<string, RepoEntry>> kvpsWithOrdersOnShelf)
    {
        var result = kvpsWithOrdersOnShelf
        .Where(kvp => Target.Shelf != ToTarget(kvp.Value.Order.Temp))
        .Select(kvp => kvp.Value)
        .ToList();
        return result;
    }

    private static bool IsFull(
        string target,
        Dictionary<string, int> storageLimits,
        Dictionary<string, RepoEntry> actionRepo)
    {
        var entriesWithOrdersOnTarget = KvpsWithOrdersOnTarget(actionRepo, target).ToList();
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

    private static bool IsFresh(Order order, List<Action> orderActions, DateTime localNow)
    {
        var spoilage = Spoilage(order, orderActions, localNow);
        var result = spoilage <= TimeSpan.FromSeconds(order.Freshness);
        return result;
    }

    private static TimeSpan Spoilage(Order order, List<Action> orderActions, DateTime localNow)
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
        _orderRepo.Clear();
        foreach (var item in _orderRepoSemaphores)
        {
            item.Value.DisposeUndelying();
        }
        _orderRepoSemaphores.Clear();
        _commandHandlerRepo.Clear();

        // _storageRepo.Clear();
        // foreach (var item in _storageRepoSemaphores)
        // {
        //     item.Value.DisposeUndelying();
        // }
        // _storageRepoSemaphores.Clear();

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

// // need this to be record, so it can be created on different threads and still be recognised as the same entity while being use as a key in dictionary
// public record PickableOrder
// {
//     public DateTime PickupTime { get; init; }
//     public Order Order { get; init; }
//     public string Id => Order.Id;
//     public string Name => Order.Name;
//     public string Temp => Order.Temp;
//     public long Price => Order.Price;
//     public long Freshness => Order.Freshness;

//     public PickableOrder(Order order, DateTime pickupTime)
//     {
//         PickupTime = pickupTime;
//         Order = order;
//     }

//     // todo kb: delete
//     public static long RandomBetween(long min, long max)
//     {
//         var result = new Random().NextInt64(min, max);
//         return result;
//     }

// }
