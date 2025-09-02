using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Challenge;

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
        if (actions.Any(x => x.Id == "s1.1"))
        {
            Console.WriteLine("LKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLKLK");
        }
        return IsFinal(actions.Last());
    }

    private async Task PickupSingleOrder(PickableOrder order, CancellationToken ct)
    {
        await WaitUntil(order.PickupTime, _time.GetLocalNow().DateTime, ct);

        var localNow = _time.GetLocalNow().DateTime;

        using (var semaphore = await GetOrCreateWaitingRepoSemaphore(order, ct))
        {
            var orderActions = _actionRepo[order.Id].Actions;
            if (IsOrderProcessed(orderActions))
            {
                return;
            }
            if (IsFresh(order, orderActions, localNow))
            {
                string pickupFromTarget = orderActions.Last().Target;
                PickupOrderFromTargetAt(order, pickupFromTarget, order.PickupTime);
            }
            else
            {
                string discardFromTarget = orderActions.Last().Target;
                DiscardOrderFromTargetAt(order, discardFromTarget, order.PickupTime);
            }
        }
    }

    private static void PickupOrderFromTargetAt(PickableOrder order, string pickupFromTarget, DateTime pickupTime)
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

            using (var placeSemaphore = await GetOrCreateWaitingRepoSemaphore(pickableOrder, ct))
            {
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
                using var moveSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToMove, ct);
                string moveToTarget = ToTarget(orderToMove.Temp);
                MoveOrderToTargetAt(orderToMove, moveToTarget, localNow);
            }
            else
            {
                var kvpToDiscard = CalculateOrderToDiscard(kvpsWithOrdersOnTarget);
                var orderToDiscard = kvpToDiscard.Value.Order;
                using var discardSemaphore = await GetOrCreateWaitingRepoSemaphore(orderToDiscard, ct);
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


    private KeyValuePair<string, (PickableOrder Order, List<Action> Actions)> CalculateOrderToDiscard(
              List<KeyValuePair<string, (PickableOrder Order, List<Action> Actions)>> kvpsWithOrdersOnTarget)
    {
        // Console.WriteLine("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA " + string.Join(", ", kvpsWithOrdersOnTarget.Select(x => x.Value.Order.Id)));
        var localNow = _time.GetLocalNow().DateTime;
        var first3 = kvpsWithOrdersOnTarget.Take(3);
        var notFresh = first3.Where(x => !IsFresh(x.Value.Order, x.Value.Actions, localNow));
        // Console.WriteLine("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA " + string.Join(", ", notFresh.Select(x => x.Value.Order.Id)));
        if (notFresh.Any())
        {
            return notFresh.First();
        }
        var first3WithSpoilage = first3
            .Select(kvp =>
            {
                var spoilage = Spoilage(kvp.Value.Order, kvp.Value.Actions, localNow);
                var overSpoilage = spoilage - TimeSpan.FromSeconds(kvp.Value.Order.Freshness);
                var expectedFutureSpoilage = kvp.Value.Order.PickupTime - localNow;
                // Console.WriteLine("VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV " + (kvp.Value.Order.Id, overSpoilage + expectedFutureSpoilage, overSpoilage, expectedFutureSpoilage));
                if (kvp.Value.Actions.Last().Target != ToTarget(kvp.Value.Order.Temp))
                {
                    expectedFutureSpoilage = expectedFutureSpoilage * 2;
                }


                return (spoilage: overSpoilage + expectedFutureSpoilage, kvp);
            }).ToList();
        // Console.WriteLine("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ " + string.Join(", ", first3WithSpoilage.Select(x => (x.kvp.Value.Order.Id, x.spoilage))));
        var worstKvpWithSpoilage = first3WithSpoilage.Aggregate((x, y) =>
        {
            Console.WriteLine("WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW " + (x.kvp.Value.Order.Id, x.spoilage) + " vs " + (y.kvp.Value.Order.Id, y.spoilage) + " sds " + (x.spoilage > y.spoilage) + " " + (x.spoilage > y.spoilage ? x : y));
            return x.spoilage > y.spoilage ? x : y;
        });
        // Console.WriteLine("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX " + (worstKvpWithSpoilage.kvp.Value.Order.Id, worstKvpWithSpoilage.spoilage));
        return worstKvpWithSpoilage.kvp;
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
