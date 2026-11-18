#!/usr/bin/env node
/**
 * Builds the in-browser C# runner (.NET on WebAssembly + Roslyn) and copies it to
 * public/csharp-runner/_framework, where public/csharp-runner/worker.js loads it.
 *
 *   npm run build:csharp
 *
 * Needs the .NET 10 SDK (the same one the API uses); no extra workloads. Takes about a minute. The output
 * (~18 MB, ~6 MB compressed) is not committed: run this once after cloning, and again after changing
 * anything in src/Codegrove.CSharpRunner or src/Codegrove.CSharpRunner.Wasm. CI runs it before `npm run build`.
 */
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const web = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const project = resolve(web, '../src/Codegrove.CSharpRunner.Wasm/Codegrove.CSharpRunner.Wasm.csproj');
const target = join(web, 'public/csharp-runner/_framework');
const out = mkdtempSync(join(tmpdir(), 'cg-csharp-'));

try {
  console.log('Publishing the C# runner (dotnet publish, Release)…');
  const result = spawnSync('dotnet', ['publish', project, '-c', 'Release', '-o', out, '--nologo', '-v', 'q'], { stdio: 'inherit' });
  if (result.error) throw new Error(`Couldn't run dotnet (${result.error.message}). Is the .NET 10 SDK installed?`);
  if (result.status !== 0) throw new Error(`dotnet publish failed (exit code ${result.status}).`);

  const framework = join(out, 'wwwroot/_framework');
  if (!existsSync(join(framework, 'dotnet.js'))) throw new Error(`dotnet publish didn't produce ${framework}/dotnet.js.`);
  rmSync(target, { recursive: true, force: true });
  cpSync(framework, target, { recursive: true });
  console.log(`C# runner copied to ${target}`);
} catch (error) {
  console.error(String(error instanceof Error ? error.message : error));
  process.exitCode = 1;
} finally {
  rmSync(out, { recursive: true, force: true });
}
