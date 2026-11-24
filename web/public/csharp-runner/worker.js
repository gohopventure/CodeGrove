// Codegrove C# runner worker: .NET on WebAssembly with Roslyn, compiling and running learner C#.
// Started by host.js. The .NET files in _framework/ come from `npm run build:csharp` (not committed).
//
// Learner code can only use the allow-listed reference assemblies (no System.Net.Http), and once .NET
// has started, this worker removes its own network and storage functions as a second lock. The worker
// lives on the preview origin, which holds nothing worth reaching anyway.

const NETWORK_AND_STORAGE = [
  'fetch', 'XMLHttpRequest', 'WebSocket', 'WebSocketStream', 'EventSource', 'WebTransport', 'importScripts',
  'Worker', 'SharedWorker', 'BroadcastChannel', 'indexedDB', 'caches',
];

function lockDown() {
  for (const name of NETWORK_AND_STORAGE) {
    // Shadow the global and remove it from the prototype chain, so neither self.fetch nor a saved prototype works.
    for (let target = self; target; target = Object.getPrototypeOf(target)) {
      try {
        if (Object.prototype.hasOwnProperty.call(target, name)) delete target[name];
      } catch {
        /* not configurable: shadowed below */
      }
    }
    try {
      Object.defineProperty(self, name, { value: undefined, writable: false, configurable: false });
    } catch {
      /* already locked */
    }
  }
}

let exports = null;

async function start() {
  let dotnet;
  try {
    ({ dotnet } = await import('./_framework/dotnet.js'));
  } catch {
    postMessage({
      type: 'failed',
      detail: "The C# runner isn't built yet. In the web folder, run: npm run build:csharp (needs the .NET 10 SDK), then reload.",
    });
    return;
  }
  try {
    const runtime = await dotnet.create();
    const config = runtime.getConfig();
    const assemblyExports = await runtime.getAssemblyExports(config.mainAssemblyName);
    exports = assemblyExports.RunnerExports;
    lockDown();
    // Compile one tiny program now, so the learner's first run doesn't wait for Roslyn to warm up.
    await exports.WarmUp();
    // Listen only now: the .NET runtime uses this worker's message events while it starts.
    self.addEventListener('message', onRun);
    postMessage({ type: 'ready' });
  } catch (error) {
    postMessage({ type: 'failed', detail: 'C# could not start: ' + (error && error.message ? error.message : String(error)) });
  }
}

async function onRun(event) {
  const msg = event.data || {};
  if (msg.type === 'serve' && exports) {
    onServe(msg);
    return;
  }
  if (msg.type !== 'run' || !exports) return;
  try {
    const json = await exports.Run(JSON.stringify(msg.request));
    postMessage({ type: 'result', id: msg.id, result: JSON.parse(json) });
  } catch (error) {
    // A bug in the runner itself, not the learner's code (their exceptions are caught in .NET).
    postMessage({
      type: 'result',
      id: msg.id,
      result: {
        compiled: false, diagnostics: [], output: '', stopped: 'Exception', results: [], compileMs: 0, runMs: 0,
        console: [{ level: 'error', text: "Codegrove's C# runner hit a problem (our bug, not yours): " + (error && error.message ? error.message : String(error)) }],
      },
    });
  }
}

// One request for the live server a KeepServer run left running (the capstone's preview calling its API).
async function onServe(msg) {
  try {
    const json = await exports.ServerRequest(JSON.stringify(msg.request));
    postMessage({ type: 'served', id: msg.id, response: JSON.parse(json) });
  } catch (error) {
    postMessage({
      type: 'served',
      id: msg.id,
      response: {
        found: true, exchange: null,
        console: [{ level: 'error', text: "Codegrove's C# runner hit a problem (our bug, not yours): " + (error && error.message ? error.message : String(error)) }],
      },
    });
  }
}

start();
