#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

# .NET 8 changed the default container port from 80 to 8080, so this must be set
# explicitly to match the port exposed below. Without it the app listens on 8080
# while the host routes to 80, and every request fails with a 502.
ENV ASPNETCORE_HTTP_PORTS=80

EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Server/AIChef.Server.csproj", "Server/"]
COPY ["Client/AIChef.Client.csproj", "Client/"]
COPY ["Shared/AIChef.Shared.csproj", "Shared/"]
RUN dotnet restore "Server/AIChef.Server.csproj"
COPY . .
WORKDIR "/src/Server"
RUN dotnet build "AIChef.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AIChef.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AIChef.Server.dll"]