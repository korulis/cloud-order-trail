# README

Author: `Karolis Blazevicius`

## How to run

The `Dockerfile` defines a self-contained C# reference environment.
Build and run the program using [Docker](https://docs.docker.com/get-started/get-docker/):
```
$ docker build -t challenge .
$ docker run --rm -it challenge --auth <token>
```
Feel free to modify the `Dockerfile` as you see fit.

If dotnet `8.0` or later is locally installed, run the program directly for convenience:
```
$ cd Challenge
$ dotnet run -- --auth <token>
```

## Discard criteria

- Choosing first order in the list of orders in Shelf. This should usually conicide with the earliest placed order among them - as such the oldest one.

## Notes

- Consider the period to be an half closed interval [min, max]
- Chose generate pickup time at microseconds granularity, because this is the granularity at which timestamps are suplied to the test server.
- todo: replace all local time with UTC
- todo: optimize dockerfile
- regarding asynchronicity model. As it stands the application can only process incoming orders (place orders) with one thread (the parallelism parameter is hardcoded to be 1). 
- It took me a while to realize that due to the fact that pickup times are random and the fact that there is not much certainty at which point which Task will be picked up or which thread will process it - it is not possible to solve the simulation with 100% reproducible results (even if the same seed was used to generate pickup times). By that time I had invested quite a lot of work into the current asynchronicity model (using semaphores and concurent dictionary up to an extent). Preferred path onwards for the code would be to 1. make PickupSingleOrder and PlaceOrders become rentrant. That would enable next step : 2. chop processing pipeline into events/commands and event/command handlers (naturally, idempotent). That would enable releasing semaphores earlier (more opportunistically), have less nested semaphores - better performance, and even more impresively, create a dictionary that tracks state of storage `Dict<temp, List<Order>>` . For as long as the order in which different entries of this dictionary are being locked is respected there should be no deadlocks. The side effect of this would be that the simulation would from time to time produce move and discard actions that are (always possible but) not always necessary, but that is a nature of a system operating in events as opposed to current - semi-rpc approach. Next would be to 3. try use `AddOrUpdate()` and `GetOrCreate()` from `ConcurentDictionary` heavily relying on overloads with create, add and update factory parameters/funcions/callbacs and putting most if not all business logic into these callbacks. I do not have a clear vision how (or if at all) this would work, but if it did I would expect that no semaphores would be needed (the performance concerns would be completely outsourced to .NET framework team ;) ).