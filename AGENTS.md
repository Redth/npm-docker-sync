# Repository Guidelines

## Project Structure & Module Organization
The solution targets .NET 8.0 and runs from `Program.cs`, which wires up the background services. Reusable logic lives in `Services/` (e.g., `DockerMonitorService`, `NginxProxyManagerClient`). Runtime configuration defaults reside in `appsettings.json`, while container assets sit in `Dockerfile` and `docker-compose.test.yml`. Build artefacts land in `bin/` and `obj/`; keep them ignored in commits.

## Build, Test, and Development Commands
- `dotnet restore` — install NuGet dependencies defined in `NpmDockerSync.csproj`.
- `dotnet build` — compile the console host and fail fast on warnings-as-errors set in the SDK.
- `dotnet run` — run the sync service locally; set `DOCKER_HOST`/`NPM_URL` via env vars.
- `docker build -t npm-docker-sync .` — produce the shipping container image.
Run commands from the repository root to respect solution-relative paths.

## Coding Style & Naming Conventions
Use the standard C# conventions: four-space indentation, PascalCase for types, camelCase for locals, and `_camelCase` for private readonly fields. Nullable reference types are enabled; resolve warnings instead of suppressing them. Before committing, run `dotnet format` (install the tool if missing) to enforce whitespace, ordering, and using directives.

## Testing Guidelines
A dedicated test project is not yet checked in. New features should include an xUnit project under `tests/NpmDockerSync.Tests/` with filenames matching the target type (e.g., `DockerMonitorServiceTests.cs`). Name test methods with the behavior under test (`MethodName_State_ExpectedResult`). Execute `dotnet test` locally and ensure high-signal integration scenarios cover Docker label parsing and NPM API calls via mocks.

## Commit & Pull Request Guidelines
Write imperative commit subjects (`Add mirror sync retries`) and keep them under 72 characters; expand on context in the body if needed. Each PR should describe the change, list validation steps (`dotnet build`, `dotnet test`), and call out configuration or label impacts. Link related issues or discussions, attach screenshots/log snippets when altering observer-facing behavior, and update README/AGENTS docs when configuration contracts move.

## Configuration Tips
Surface new environment variables in `README.md` and document defaults in `appsettings.json`. Avoid hard-coding sensitive values; rely on `.env.example` to demonstrate expected keys without secrets.
