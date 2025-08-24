# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app

# todo kb: optimize docker file.
COPY . .

WORKDIR /app/src/Challenge
ENTRYPOINT ["dotnet", "run", "--"]
