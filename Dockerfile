FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["NpmDockerSync.csproj", "./"]
RUN dotnet restore "NpmDockerSync.csproj"
COPY . .
RUN dotnet build "NpmDockerSync.csproj" -c Release -o /app/build
RUN dotnet publish "NpmDockerSync.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "NpmDockerSync.dll"]
