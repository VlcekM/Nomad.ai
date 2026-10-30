# Build from the repository root: docker build -f Nomad.Hub/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props CHANGELOG.md ./
COPY Nomad.Hub.Contracts/Nomad.Hub.Contracts.csproj Nomad.Hub.Contracts/
COPY Nomad.Hub/Nomad.Hub.csproj Nomad.Hub/
RUN dotnet restore Nomad.Hub/Nomad.Hub.csproj -r linux-x64
COPY Nomad.Hub.Contracts/ Nomad.Hub.Contracts/
COPY Nomad.Hub/ Nomad.Hub/
ARG VERSION
RUN dotnet publish Nomad.Hub/Nomad.Hub.csproj -c Release -r linux-x64 --self-contained false --no-restore \
    -o /app ${VERSION:+-p:Version=$VERSION} \
    && mkdir /data

# Chiseled: no shell, no package manager, runs as the non-root "app" user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /app .
# Owned by "app", so a named volume starts writable.
COPY --from=build --chown=1654:1654 /data /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Hub__DataPath=/data
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nomad.Hub.dll"]
