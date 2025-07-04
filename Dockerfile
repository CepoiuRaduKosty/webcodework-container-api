
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-dotnet-api
WORKDIR /source

COPY *.csproj .
COPY . .
RUN dotnet publish InternalApi.csproj -o /app/publish


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
COPY --from=build-dotnet-api /app/publish .

RUN chown -R root:root /app 
RUN chmod -R 755 /app 
# r-x for others


USER coder
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="c" \
    TMPDIR="/sandbox" \
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

ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox

WORKDIR /app
COPY appsettings.json /app/appsettings.json

RUN chown -R root:root /app 
RUN chmod -R 755 /app 
# r-x for others


COPY --from=build-dotnet-api /app/publish .

USER coder
WORKDIR /app

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="python" \
    TMPDIR="/sandbox" \
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

RUN chown -R root:root /app 
RUN chmod -R 755 /app 
# r-x for others


COPY --from=build-dotnet-api /app/publish .

USER coder
WORKDIR /app

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="java" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TMPDIR="/sandbox" \
    JAVA_HOME="/usr/lib/jvm/java-17-openjdk-amd64" 

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]


# --- Final Stage for Rust Runner ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS rust-runner
LABEL runner.language="rust"

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    build-essential \
    ca-certificates \
    coreutils \
    diffutils \
    && rm -rf /var/lib/apt/lists/*

ENV CARGO_HOME=/usr/local/cargo
ENV RUSTUP_HOME=/usr/local/rustup
ENV PATH=/usr/local/cargo/bin:$PATH
RUN curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y --default-toolchain stable --no-modify-path && \
    chmod -R 777 $CARGO_HOME && chmod -R 777 $RUSTUP_HOME # Ensure accessible

ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox && \
    ln -s /usr/local/cargo/bin/rustc /usr/local/bin/rustc && \
    ln -s /usr/local/cargo/bin/cargo /usr/local/bin/cargo


WORKDIR /app
COPY appsettings.json /app/appsettings.json
COPY --from=build-dotnet-api /app/publish .

RUN chown -R root:root /app 
RUN chmod -R 755 /app 
# r-x for others


USER coder
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="rust" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TMPDIR="/sandbox" \
    PATH="/home/coder/.cargo/bin:/usr/local/cargo/bin:${PATH}"

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]


# --- Final Stage for Go Runner ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS go-runner
LABEL runner.language="go"
RUN apt-get update && apt-get install -y --no-install-recommends \
    golang-go \
    ca-certificates \
    coreutils \
    diffutils \
    && rm -rf /var/lib/apt/lists/*

ARG USER_UID=1001
ARG USER_GID=1001
RUN groupadd --gid $USER_GID coder && \
    useradd --uid $USER_UID --gid $USER_GID -m coder && \
    mkdir /sandbox && \
    chown coder:coder /sandbox && \
    chmod -R 777 /sandbox

WORKDIR /app
COPY appsettings.json /app/appsettings.json
COPY --from=build-dotnet-api /app/publish .

RUN chown -R root:root /app 
RUN chmod -R 755 /app 
# r-x for others


USER coder
WORKDIR /app

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000 \
    Execution__Language="go" \
    DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_ENVIRONMENT=Production \
    TMPDIR="/sandbox" \
    GOCACHE="/sandbox/go-build-cache" \
    PATH="${PATH}:/usr/lib/go/bin"

ENTRYPOINT ["dotnet", "/app/InternalApi.dll"]
