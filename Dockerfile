
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-dotnet-api
WORKDIR /source

COPY *.csproj .
COPY . .
RUN dotnet publish -o /app/publish


# --- Final Stage for C Runner ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS c-runner
LABEL runner.language="c"


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
COPY --from=build-dotnet-api /app/publish .

USER coder
WORKDIR /sandbox
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="c" \
    DOTNET_RUNNING_IN_CONTAINER=true 

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]


# --- Final Stage for Python Runner ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS python-runner
LABEL runner.language="python"

# Install Python3, pip, and necessary utilities
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-pip \
    coreutils \
    diffutils \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user and sandbox directory (can be repetitive, consider a common base if stages get complex)
ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox

WORKDIR /app
# Copy appsettings.json (respecting your multiple copies)
COPY appsettings.json /app/appsettings.json
COPY appsettings.json /app/publish/appsettings.json
COPY appsettings.json /sandbox/appsettings.json
COPY appsettings.json /source/appsettings.json
COPY appsettings.json /appsettings.json
COPY appsettings.json /home/appsettings.json

# Copy the published ASP.NET Core application from the build stage
COPY --from=build-dotnet-api /app/publish .

USER coder
WORKDIR /sandbox

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="python" \
    DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS java-runner
LABEL runner.language="java"

RUN apt-get update && apt-get install -y --no-install-recommends \
    openjdk-17-jdk-headless \
    ca-certificates \
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

COPY --from=build-dotnet-api /app/publish .

USER coder
WORKDIR /sandbox

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="java" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    JAVA_HOME="/usr/lib/jvm/java-17-openjdk-amd64" 

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]