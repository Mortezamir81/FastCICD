# Repository Guidelines

## Project Structure & Module Organization

`FastCICD.slnx` contains two .NET 9 projects: `FastCICD/` is the Spectre.Console deployment client; `CICD_API/` is the ASP.NET Core server. The client entry point and deployment workflow are in `FastCICD/Program.cs`, with configuration models in `FastCICD/Models/`, the HMAC request handler in `HmacDelegatingHandler.cs`, and response error handling in `HttpResponseExtensions.cs`. The server registers its pipeline in `CICD_API/Program.cs`; minimal API endpoint mapping and deployment operations are in `CICD_API/Endpoints/DeployEndpoints.cs`, request records are in `CICD_API/Models/DeployModels.cs`, and authentication is `CICD_API/Middlewares/SecurityAuthMiddleware.cs`. The only UI is the client console menu; there are no HTML, CSS, Razor, or page files.

This repository currently has no entities, ViewModels, separate response DTOs, repositories, database context, EF configurations, migrations, AutoMapper profiles, caching services, API versioning, or controller classes. Endpoint responses use `Results` directly, anonymous objects, collections, or plain error text.

## Contracts & Feature Placement

Put new server request contracts as records in `CICD_API/Models/DeployModels.cs` (or a nearby feature-specific model file); keep client configuration/transport models under `FastCICD/Models/`. Add new routes in `CICD_API/Endpoints/DeployEndpoints.cs` and map them from `MapDeployEndpoints`. Add client calls and console presentation in `FastCICD/Program.cs`; add reusable HTTP behavior in its dedicated helper/handler files. If response DTOs or mappings are introduced, place them beside the feature under `Models/` and add explicit mapping code there; no mapping framework is configured. Database changes have no current location because EF and migrations are absent. Protect every new API route through `SecurityAuthMiddleware`; project and Windows-service authorization is configuration-driven by `AllowedDirectories` and `AllowedServices`.

## Build, Run, and Test

Use `dotnet build FastCICD.slnx` for the complete solution, or build a changed project with `dotnet build FastCICD/FastCICD.csproj` / `dotnet build CICD_API/CICD_API.csproj`. Run the server with `dotnet run --project CICD_API/CICD_API.csproj --launch-profile http` (the configured URL is `http://localhost:5182`) and the client with `dotnet run --project FastCICD/FastCICD.csproj`. Copy each `appsettings.example.json` to a local ignored `appsettings.json` without committing sensitive values. No test project or test command is defined; do not run tests unless requested.

## Coding & Change Conventions

Nullable reference types and implicit usings are enabled. Existing code uses PascalCase types/methods, camelCase locals/parameters, file-scoped namespaces, records for request contracts, minimal APIs, constructor-injected configuration/loggers, structured `ILogger` messages, and `Results.Problem`/status results for failures. Keep code comments concise and English-only; for future UI files, add short English comments above major sections. Recent commits use `[BUGFIX]` (with one historical `[BUGIX]`) or short imperative subjects; preserve that established style unless the project adopts a clearer convention.
