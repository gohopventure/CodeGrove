# Codegrove.WebApi.ParityTests

Keeps Codegrove's practice web server (`src/Codegrove.WebShim`) honest. Learner programs are compiled with Roslyn against the **real** ASP.NET Core shared framework (with the Web SDK's implicit usings, nullable off, as the runner compiles them), started on Kestrel on a free `127.0.0.1` port in Development, and sent real HTTP requests.

- **`LessonParityTests`**: every published lesson solution with `httpRequest` checks must pass them on real ASP.NET Core too, each check on a freshly started server (as the runner does). Uses the runner's own `WebChecks.RequestFrom` / `Mismatch`.
- **`ShimParityTests`**: the same program and requests go through the runner (practice server) and real ASP.NET Core, and the answers must agree (`Parity.Diff`): status, media type, JSON (where the practice server has a value; real ASP.NET Core may add fields like `traceId`), the first line of plain-text error bodies (the real one is the developer exception page, with a stack trace after it), other bodies exactly, and `Location`/`Allow`. Plus DI validation errors at `Build()`.

How it finds the server: the `HostBuilt` event on the `Microsoft.Extensions.Hosting` DiagnosticListener (what `WebApplicationFactory` relies on), then waits for `ApplicationStarted`, and stops the host after each use. Tests don't run in parallel (`DisableTestParallelization`), because the URL and environment are set through environment variables.

The runner references Codegrove.WebShim, whose types have ASP.NET Core's names, so this project sets `DisableTransitiveProjectReferences` and loads the shim by name at run time from `bin/…/shim/` (`ShimLoader`).

No CI change was needed: `dotnet test` on the solution runs it. About 10 seconds.
