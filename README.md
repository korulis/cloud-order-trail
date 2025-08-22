# README

Author: `<your name here>`

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

`<your chosen discard criteria and rationale here>`
