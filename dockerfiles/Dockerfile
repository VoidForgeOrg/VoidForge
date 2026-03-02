FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/global.json src/Directory.Build.props src/Voidforge.slnx ./
COPY src/Voidforge.Api/Voidforge.Api.csproj Voidforge.Api/
RUN dotnet restore Voidforge.slnx

COPY src/ .
RUN dotnet publish Voidforge.Api/Voidforge.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Voidforge.Api.dll"]
