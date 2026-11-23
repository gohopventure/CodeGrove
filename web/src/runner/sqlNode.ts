/**
 * sql.js for Vitest (test-only). Reads the WebAssembly file from node_modules directly, so it works
 * under the jsdom test environment too, where sql.js would otherwise try to fetch() it.
 */
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import initSqlJs, { type SqlJsStatic } from 'sql.js';

let loading: Promise<SqlJsStatic> | null = null;

export function loadSqlJs(): Promise<SqlJsStatic> {
  if (!loading) {
    const require = createRequire(import.meta.url);
    const wasm = readFileSync(require.resolve('sql.js/dist/sql-wasm.wasm'));
    const wasmBinary = wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength) as ArrayBuffer;
    // @types/sql.js marks newer options as required; sql.js itself treats them all as optional.
    loading = initSqlJs({ wasmBinary } as Parameters<typeof initSqlJs>[0]);
  }
  return loading;
}
