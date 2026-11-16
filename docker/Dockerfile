# syntax=docker/dockerfile:1

# Builds SyncedNotes straight from its Git repository, so this folder is all a server needs.
#
# The source is fetched with ADD from a Git URL. For a private repository, pass a GitHub personal access token as
# the build secret GIT_AUTH_TOKEN (docker-compose.yml does this with GIT_PAT from .env). BuildKit uses it only to
# fetch the repository, and it never ends up in an image layer or the image history:
#
#   GIT_PAT=<token> docker build --secret id=GIT_AUTH_TOKEN,env=GIT_PAT -t syncednotes .
#
# BuildKit checks the branch for new commits on every build, so building again picks up the latest code.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
# A Git URL ending in .git, optionally followed by #<branch, tag or commit>.
ARG GIT_URL=https://github.com/benringrose/SyncedNotes.git#main
ADD ${GIT_URL} /src
WORKDIR /src
RUN --mount=type=cache,id=syncednotes-nuget,target=/root/.nuget/packages \
    dotnet publish SyncedNotes/SyncedNotes.csproj -c Release -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
COPY --from=build /app .
# The database (notes, images, accounts and sign-in keys) lives in /data. Mount a volume there.
RUN mkdir /data && chown "$APP_UID" /data
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    DataDirectory=/data
VOLUME /data
EXPOSE 8080
# The image has no curl, so the health check makes the request with bash.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /healthz HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "SyncedNotes.dll"]
