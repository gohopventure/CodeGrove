# Codegrove (working name)

An interactive platform that teaches full-stack development to kids aged about 10–17, modeled on
Team Treehouse: tracks of short courses, lessons with in-browser code challenges and quizzes,
badges and certificates, and a parent dashboard. Two audiences share one codebase:
**Explorers** (10–13) and **Builders** (14–17).

This repo is the **Phase 1 MVP**: the HTML/CSS/JS path with a working editor, automatic checking,
progress tracking, COPPA-style parental consent and the parent dashboard. The design doc
(curriculum, architecture, schema, API, routes, roadmap) lives separately.

## What's in the box

```
codegrove/
├─ src/Codegrove.Api/            ASP.NET Core 10 Web API (also serves the built SPA in production)
│  ├─ Features/                  Controllers by feature: Auth, Me, Parent, Catalog, Learning, Certificates
│  ├─ Services/                  Domain rules: progress/XP/badges, consent, quiz grading, age policy,
│  │                             content filter, activity limits, email
│  ├─ Data/                      EF Core context, entities, migrations, seeders
│  ├─ Content/                   Course content as JSON (seeded on startup, upsert by slug)
│  └─ Infrastructure/            Auth setup, CSRF guard, rate limits, consent cleanup job
├─ tests/Codegrove.Api.Tests/    xUnit: rule tests + end-to-end API tests on in-memory SQLite (41 tests)
├─ web/                          React 19 + TypeScript + Vite + Tailwind 4 SPA
│  ├─ src/runner/                Code checking: declarative test engine, harness, loop protection (15 tests)
│  ├─ src/lesson/                Lesson player, Monaco editor wrapper, sandboxed live preview
│  ├─ src/pages/                 Learner, parent and public pages
│  └─ public/runner.html         The sandboxed preview page (deployed to its own origin)
├─ deploy/preview-host/          Static Web Apps config for the preview origin
├─ docker-compose.yml            Local SQL Server
└─ .github/workflows/ci.yml      Build, test, migration drift check
```

## Run it locally

Prerequisites: **.NET 10 SDK** (10.0.100 or later), Node 22, and Docker (for SQL Server) or nothing
extra (SQLite). Check with `dotnet --list-sdks`; get .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0.
.NET 8 and 9 reached end of support on November 10, 2026, so the project targets .NET 10 (LTS, supported to November 2028).

**1. API**

```bash
cd src/Codegrove.Api
dotnet run                                # http://localhost:5080, SQLite file codegrove-dev.db, seeds on start
```

Development uses SQLite by default so nothing else is needed. Delete `codegrove-dev.db` to start fresh.
To develop against SQL Server (what production runs on, and what the migrations target):

```bash
docker compose up -d                      # SQL Server on localhost:1433
dotnet run --launch-profile sqlserver     # applies migrations, then seeds
```

**2. Web app**

```bash
cd web
npm install
npm run dev                               # open http://localhost:5173
```

The app runs on `http://localhost:5173` and the preview runner on `http://127.0.0.1:5173/runner.html`.
Those are different origins on purpose, mirroring production.

**Demo accounts** (Development only; password `codegrove-demo` for all):

| Username | Role | Notes |
| --- | --- | --- |
| `parent@example.com` | Parent | Linked to both children below |
| `PixelFox42` | Learner, Explorer | Consent already granted |
| `ByteRunner` | Learner, Builder | 15 years old |
| `admin@example.com` | Admin | No admin UI yet (Phase 2) |

When a parent adds an under-13 child in Development, the consent email is written to the API log
and the confirm link is also shown on screen, so you can test without an inbox.

## Troubleshooting `dotnet run`

| Symptom | Cause | Fix |
| --- | --- | --- |
| `A compatible .NET SDK was not found` / `Requested SDK version: 10.0.100` | No .NET 10 SDK installed (`global.json` asks for it) | Install the .NET 10 SDK, then open a new terminal |
| `Couldn't find a project to run` | Ran from the repo root, which has two projects | `cd src/Codegrove.Api` first, or `dotnet run --project src/Codegrove.Api` |
| `A network-related or instance-specific error ... SQL Server` | Started with the `sqlserver` profile but the container isn't running | `docker compose up -d`, or plain `dotnet run` (SQLite) |
| `npm run dev`: `does not provide an export named 'styleText'` or EBADENGINE warnings | Node older than 22.12 | `nvm install 22 && nvm use 22` (the repo's `web/.nvmrc` pins 22), then `rm -rf node_modules && npm install` |
| `Address already in use` on 5080 | Another copy is still running | Stop it, or set `ASPNETCORE_URLS=http://localhost:5081` (and update `web/vite.config.ts`) |

## Tests

```bash
dotnet test                 # 41 API tests: rules + full HTTP flows (consent, lesson, XP once, hints, limits)
cd web && npm test          # 15 runner tests: test engine, loop protection, document composition
```

## How code checking works

1. The lesson player sends the learner's files to a fresh `<iframe sandbox="allow-scripts">` that
   loads `runner.html` from the preview origin. No `allow-same-origin`, so the frame has an opaque
   origin and can't read the app's cookies, storage or DOM.
2. The app composes one document: linked CSS/JS files inlined, every loop wrapped in a time guard
   (`src/runner/loopProtect.ts`), and a test harness injected first in `<head>` with a CSP that
   blocks all network access.
3. The runner replaces itself with that document. The harness captures console output, waits for
   `load`, runs the challenge's **declarative tests** (`elementExists`, `elementText`,
   `cssProperty`, `consoleIncludes`, …) and posts results back. Authors write tests as JSON, not code.
4. The app posts code + results to the API, which decides "passed" itself (every test of the
   challenge must be present and passing), awards XP once, and checks badges and certificates.

## Authoring content

Until the Phase 2 authoring UI exists, content is JSON in `src/Codegrove.Api/Content/`:

- `catalog.json`: tracks (ordered course slugs, prerequisites) and badges
- `courses/*.json`: a course with its lessons; each lesson has learn markdown, a worked example,
  a challenge (starter files, solution, tests, three hint levels) and a quiz

The seeder upserts by slug on every start and validates published lessons (tests present, hints at
levels 1–3, exactly one correct option per question, captions for every video). Learner progress is
keyed by lesson id, so editing content never loses progress. `html-basics.json` has the fully built
first lesson in both an Explorer and a Builder version; the other lessons and courses are outlines.

## Deploying to Azure

| Piece | Resource | Notes |
| --- | --- | --- |
| API + SPA | App Service (Linux, .NET 10) | Copy `web/dist` (minus `runner.html`) into `wwwroot` before `dotnet publish` |
| Preview origin | Static Web Apps on a **separate domain** | Deploy only `runner.html` + `deploy/preview-host/staticwebapp.config.json` |
| Database | Azure SQL (serverless) | Run migrations from CI with `dotnet ef migrations bundle`; keep `MigrateOnStartup=false` |
| Secrets | Key Vault + managed identity | `ConnectionStrings:Default`, `Email:Acs:ConnectionString` |
| Email | Azure Communication Services | Without a connection string, emails go to the log |
| Telemetry | Application Insights | Only enabled when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set |

Set `App:BaseUrl`, `App:PreviewOrigin` and `App:MediaOrigin` in App Service configuration, and
`VITE_PREVIEW_ORIGIN` at web build time.

## Tradeoffs and assumptions made in this build

- **Client-side checking is trusted.** A determined teen could fake a pass from DevTools. With no
  leaderboards and no prizes, that's accepted for the MVP; Phase 2 re-checks HTML tests on the server.
- **Cookie auth, not JWTs.** Same-origin SPA, HttpOnly `SameSite=Strict` cookie, plus a
  JSON-content-type requirement on every state-changing request as a CSRF guard.
- **Email-plus consent.** Valid only while children's data stays internal. The AI tutor (Phase 4)
  needs a stronger method and the separate `AllowThirdPartyDisclosure` consent flag; the API already
  refuses to enable it.
- **No EF retry-on-failure.** Progress writes use explicit transactions, which conflict with EF's
  retrying strategy. Concurrent duplicate requests are handled by a single conflict retry instead.
  Revisit with `CreateExecutionStrategy()` if Azure SQL transient errors show up in telemetry.
- **Lesson-completion counts use the UTC date**; time spent uses the learner's local date.
- **Content filter word list is deliberately short.** Swap in a maintained list or a moderation
  service behind `IContentFilter` before launch.
- **Monaco on tablets is weak.** It's isolated in `CodeEditor.tsx` so CodeMirror 6 can replace it.
- **In-process background job.** `ConsentCleanupJob` runs in the web app; move it to an Azure
  Function when you scale out to more than one instance.
- **Not legal advice.** Have a privacy attorney or an FTC-approved COPPA Safe Harbor program review
  the consent flow, notices and retention policy before launch.
