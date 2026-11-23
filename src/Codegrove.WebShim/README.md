# Codegrove.WebShim: the practice web server

ASP.NET Core can't run on .NET for WebAssembly, so Web API lessons (`csharp-web-api`) host learner servers here instead. Learner code compiles against this assembly (embedded by `Codegrove.CSharpRunner`), using the real ASP.NET Core namespaces and type names, so it pastes into a `dotnet new web` project unchanged.

When a program mentions `WebApplication`, the runner adds the Web SDK's global usings (`Microsoft.AspNetCore.Builder`, `.Http`, `.Routing`, `Microsoft.Extensions.DependencyInjection`). `Microsoft.AspNetCore.Http.HttpResults` and `Microsoft.AspNetCore.Mvc` still need a `using`, as in a real project.

## What it covers

- `WebApplication.CreateBuilder`, `builder.Services`, `builder.Build()`, `app.Run()` / `RunAsync()`
- `MapGet/MapPost/MapPut/MapPatch/MapDelete/MapMethods`, `MapGroup`, `WithName` (and no-op `WithTags`/`WithSummary`)
- Route templates: literals, `{id}`, `{id?}`, `{*rest}`, constraints `int long bool guid decimal double float alpha min max range minlength maxlength length`
- Binding: route, query string, JSON body, services, `HttpContext/HttpRequest/HttpResponse`, `CancellationToken`, `[FromRoute] [FromQuery] [FromHeader] [FromBody] [FromServices]`; optional vs required follows nullability like `RequestDelegateFactory` (nullable off ⇒ reference types are optional)
- Results: `string` → text/plain, object → camelCase JSON, `IResult` (`Results.*`, `TypedResults.*`, `Results<T1..T4>`), problem details with ASP.NET Core's default `type`/`title`
- Status codes for routing: 404 no match, 405 + `Allow` for a wrong method, 500 for an ambiguous match or a crash, 400/415 for binding failures
- Endpoint-building rules, as ASP.NET Core 10 does them (confirmed by the parity tests): only one parameter can come from the body (a second one is "UNKNOWN": `Failure to infer one or more parameters…`), and a GET/DELETE/HEAD/OPTIONS endpoint can't infer a body (`Body was inferred but the method does not allow inferred body parameters…`). ASP.NET Core builds all endpoints together on the first request, so **one broken endpoint makes every request answer 500** with the first broken endpoint's error, even requests to other paths. The usual cause is a service that was never registered; the runner's Output line names the endpoint and says to register it (lesson 4 Explorer relies on it).
- Missing bodies: an optional body parameter (nullable, or any reference type while nullable is off) binds `null`; a required one answers 400 `Implicit body inferred for parameter "pet" but no body was provided…`. A `string` handler that returns `null` is a 500 (`ArgumentNullException`), as in ASP.NET Core; other types answer 200 `null`.
- DI: singleton/scoped/transient, a scope per request, constructor injection, validation at `Build()` with Microsoft's messages (unresolvable services, scoped inside singleton)

## Deliberate differences

- `app.Run()` doesn't open a port. It hands the app to the runner and ends the program (code after it doesn't run, as with a real blocking `Run`). Requests come from the Try it panel and `httpRequest` checks.
- No `Microsoft.Hosting.Lifetime` log lines; the runner prints its own "Your server is running…" line instead.
- A 400 from binding has the exception message as its plain-text body; a 500 has `ExceptionType: message`. Real Development servers send the developer exception page (also plain text for non-browser clients, but with a stack trace).
- `HttpRequest.Path` is a `string` (not `PathString`); `Query`/`Headers` are read-only dictionaries of single strings (not `StringValues`). Repeated query keys keep the first value.
- Route precedence is simplified (literal, then constrained, then plain parameter, segment by segment). No complex segments like `{a}-{b}`.
- DI: the last registration for a type wins; `IEnumerable<T>` of all registrations, keyed services and open generics aren't supported.
- Not there: middleware (`Use…`), authentication/authorization, CORS, OpenAPI, configuration, logging, `app.Environment`, `[AsParameters]`, controllers (planned for lesson 5), validation attributes and `AddValidation()` (planned for lesson 3).

## Changing it

Only add what a lesson uses, and match real ASP.NET Core's names, status codes and messages. `tests/Codegrove.CSharpRunner.Tests/WebApiTests.cs` pins the behavior; **`tests/Codegrove.WebApi.ParityTests`** runs the same programs on real ASP.NET Core (Kestrel) and fails if the answers differ, and runs every published lesson solution's `httpRequest` checks there too. When you add a behavior, add a probe for it in `ShimParityTests.cs`. After any change here, rebuild the browser runner: `npm run build:csharp`.
