/**
 * "Download project" for FullStack (capstone) apps: the learner's api/ and web/ files, unchanged, inside a
 * real ASP.NET Core project and a real Vite + React project, zipped, so the same app runs with
 * `dotnet run` and `npm run dev` on their own computer.
 *
 * The scaffolding matches what the files ran against in Codegrove:
 * - The API: the Web SDK (its implicit usings are the ones the C# runner adds), nullable off like the runner,
 *   and EF Core's in-memory provider at the version the runner embeds. It listens on http://localhost:5080.
 * - The page: Vite with the React plugin, and a dev-server proxy that sends /api to the API, so
 *   fetch('/api/...') works unchanged.
 * exportProject.test.ts keeps the versions in step with web/package.json and the runner's .csproj, and in CI
 * builds the export of every published FullStack solution.
 */
import type { Files } from './fullStack';

/** Versions of what the exported project depends on (kept in step by exportProject.test.ts). */
export const EXPORT_VERSIONS = {
  dotnet: 'net10.0',
  efCoreInMemory: '10.0.15',
  react: '^19.3.1',
  vite: '^8.3.7',
  pluginReact: '^6.3.0',
} as const;

export const API_PORT = 5080;

/** A folder and C# namespace friendly name from a lesson title or slug ("The standings page" → "the-standings-page"). */
export function projectSlug(name: string): string {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'my-app';
}

/** Every file of the exported project, by path inside the zip. */
export function projectFiles(files: Files, name: string): Files {
  const slug = projectSlug(name);
  const out: Files = {};
  const root = `${slug}/`;

  for (const [path, code] of Object.entries(files)) {
    if (!path.startsWith('api/') && !path.startsWith('web/')) continue;
    const file = path.split('/').pop() ?? '';
    if (file.startsWith('.')) continue; // hidden helper files stay in Codegrove
    out[root + path] = code;
  }

  out[`${root}api/App.Api.csproj`] = `<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>${EXPORT_VERSIONS.dotnet}</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Off, as in Codegrove, so your code builds the same way. -->
    <Nullable>disable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="${EXPORT_VERSIONS.efCoreInMemory}" />
  </ItemGroup>

</Project>
`;
  out[`${root}api/Properties/launchSettings.json`] = JSON.stringify({
    profiles: {
      http: {
        commandName: 'Project',
        launchBrowser: false,
        applicationUrl: `http://localhost:${API_PORT}`,
        environmentVariables: { ASPNETCORE_ENVIRONMENT: 'Development' },
      },
    },
  }, null, 2) + '\n';

  out[`${root}web/package.json`] = JSON.stringify({
    name: slug,
    private: true,
    version: '1.0.0',
    type: 'module',
    scripts: { dev: 'vite', build: 'vite build', preview: 'vite preview' },
    dependencies: { react: EXPORT_VERSIONS.react, 'react-dom': EXPORT_VERSIONS.react },
    devDependencies: { vite: EXPORT_VERSIONS.vite, '@vitejs/plugin-react': EXPORT_VERSIONS.pluginReact },
  }, null, 2) + '\n';
  out[`${root}web/vite.config.js`] = `import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// fetch("/api/...") goes to your API, as it did in Codegrove.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { "/api": "http://localhost:${API_PORT}" },
  },
});
`;
  out[`${root}.gitignore`] = 'api/bin/\napi/obj/\nweb/node_modules/\nweb/dist/\n';
  out[`${root}README.md`] = readme(name);
  return out;
}

function readme(name: string): string {
  return `# ${name}

You built this app in Codegrove: an ASP.NET Core API in \`api/\` and a React page in \`web/\`.
Here's how to run it on your own computer.

## What you need

- The **.NET 10 SDK**: https://dotnet.microsoft.com/download
- **Node.js 22** or newer: https://nodejs.org

Ask an adult before you install anything.

## Run it

Open two terminal windows.

In the first one, start the API:

\`\`\`
cd api
dotnet run
\`\`\`

It's ready when it says \`Now listening on: http://localhost:${API_PORT}\`.

In the second one, start the page:

\`\`\`
cd web
npm install
npm run dev
\`\`\`

Then open http://localhost:5173 in your browser.

## Good to know

- The data lives in memory, so it starts over every time the API restarts, just like in Codegrove.
- The page's \`fetch("/api/...")\` calls reach the API through the proxy in \`web/vite.config.js\`.
- Want the data to stay? Swap the in-memory database for SQLite: add the
  \`Microsoft.EntityFrameworkCore.Sqlite\` package, change \`UseInMemoryDatabase("...")\` to
  \`UseSqlite("Data Source=app.db")\`, and make sure \`Database.EnsureCreated()\` runs at startup.
`;
}

/** The project as a .zip (stored, not compressed: the files are small, and this needs no library). */
export function projectZip(files: Files, name: string): Blob {
  return new Blob([zipStore(projectFiles(files, name))], { type: 'application/zip' });
}

/** Starts a download of the project in the browser. */
export function downloadProject(files: Files, name: string): void {
  const url = URL.createObjectURL(projectZip(files, name));
  const a = document.createElement('a');
  a.href = url;
  a.download = `${projectSlug(name)}.zip`;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

// ---- A minimal ZIP writer (method 0, "stored"; UTF-8 names) ----

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

export function crc32(bytes: Uint8Array): number {
  let c = 0xffffffff;
  for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/** Zips text files without compression. */
export function zipStore(files: Files): Uint8Array<ArrayBuffer> {
  const encoder = new TextEncoder();
  const entries = Object.entries(files).map(([path, text]) => {
    const name = encoder.encode(path);
    const data = encoder.encode(text);
    return { name, data, crc: crc32(data) };
  });

  const chunks: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  // A fixed time (2026-01-01 00:00), so the same files always make the same zip.
  const dosTime = 0;
  const dosDate = ((2026 - 1980) << 9) | (1 << 5) | 1;
  const UTF8 = 0x0800;

  for (const e of entries) {
    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, UTF8, true);
    local.setUint16(8, 0, true);
    local.setUint16(10, dosTime, true);
    local.setUint16(12, dosDate, true);
    local.setUint32(14, e.crc, true);
    local.setUint32(18, e.data.length, true);
    local.setUint32(22, e.data.length, true);
    local.setUint16(26, e.name.length, true);
    local.setUint16(28, 0, true);
    chunks.push(new Uint8Array(local.buffer), e.name, e.data);

    const head = new DataView(new ArrayBuffer(46));
    head.setUint32(0, 0x02014b50, true);
    head.setUint16(4, 20, true);
    head.setUint16(6, 20, true);
    head.setUint16(8, UTF8, true);
    head.setUint16(10, 0, true);
    head.setUint16(12, dosTime, true);
    head.setUint16(14, dosDate, true);
    head.setUint32(16, e.crc, true);
    head.setUint32(20, e.data.length, true);
    head.setUint32(24, e.data.length, true);
    head.setUint16(28, e.name.length, true);
    head.setUint32(42, offset, true);
    central.push(new Uint8Array(head.buffer), e.name);

    offset += 30 + e.name.length + e.data.length;
  }

  const centralSize = central.reduce((n, c) => n + c.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, entries.length, true);
  end.setUint16(10, entries.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);

  const all = [...chunks, ...central, new Uint8Array(end.buffer)];
  const out = new Uint8Array(all.reduce((n, c) => n + c.length, 0));
  let at = 0;
  for (const c of all) {
    out.set(c, at);
    at += c.length;
  }
  return out;
}
