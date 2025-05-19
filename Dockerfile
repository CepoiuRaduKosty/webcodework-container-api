
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

COPY *.csproj .
COPY . .
RUN dotnet publish -o /app/publish


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final


RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc \
    libc6-dev \
    coreutils \
    diffutils \
    && rm -rf /var/lib/apt/lists/*

ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox

WORKDIR /app
COPY appsettings.json /app/appsettings.json
COPY appsettings.json /app/publish/appsettings.json
COPY appsettings.json /sandbox/appsettings.json
COPY appsettings.json /source/appsettings.json
COPY appsettings.json /appsettings.json
COPY appsettings.json /home/appsettings.json
COPY --from=build /app/publish .

USER coder
WORKDIR /sandbox
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="c" \
    DOTNET_RUNNING_IN_CONTAINER=true 

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]