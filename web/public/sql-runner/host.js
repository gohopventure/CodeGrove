// Codegrove SQL runner host: see index.html for what this page is and the messages it understands.
// A copy of csharp-runner/host.js with its own message names and limits; keep the two in step.
// Plain browser JavaScript on purpose (no build step), like runner.html.
(function () {
  'use strict';

  var RUN_TIMEOUT_MS = 5000; // one run (setup + learner SQL + tests); lessons take a few milliseconds
  var BOOT_TIMEOUT_MS = 60000; // downloading and starting SQLite (~0.7 MB, only slow the very first time)
  var RECYCLE_AFTER_RUNS = 500; // every run closes its database; this only guards against slow leaks

  var parentOrigin = null;
  var worker = null;
  var ready = false;
  var status = 'loading';
  var runsOnWorker = 0;
  var queue = [];
  var current = null; // the run the worker is busy with
  var timer = 0;

  function post(msg) {
    if (parentOrigin) window.parent.postMessage(msg, parentOrigin);
  }

  function setStatus(next, detail) {
    status = next;
    post({ type: 'cg:sql-status', status: next, detail: detail });
  }

  function startWorker() {
    ready = false;
    runsOnWorker = 0;
    setStatus('loading');
    worker = new Worker('worker.js'); // classic, so it can importScripts sql.js
    worker.onmessage = onWorkerMessage;
    worker.onerror = function (event) {
      event.preventDefault();
      failWorker('The SQL runner stopped unexpectedly. ' + (event.message || ''));
    };
    clearTimeout(timer);
    timer = setTimeout(function () {
      failWorker('Getting SQL ready took too long. Check your internet connection and try again.');
    }, BOOT_TIMEOUT_MS);
  }

  function stopWorker() {
    clearTimeout(timer);
    if (worker) worker.terminate();
    worker = null;
    ready = false;
  }

  // The worker couldn't start: tell the app, answer every waiting run, and try again on the next run.
  function failWorker(detail) {
    stopWorker();
    setStatus('failed', detail);
    var waiting = (current ? [current] : []).concat(queue);
    current = null;
    queue = [];
    waiting.forEach(function (run) {
      post({ type: 'cg:sql-result', id: run.id, failed: detail });
    });
  }

  function onWorkerMessage(event) {
    var msg = event.data || {};
    if (msg.type === 'ready') {
      clearTimeout(timer);
      ready = true;
      setStatus('ready');
      next();
    } else if (msg.type === 'failed') {
      failWorker(msg.detail || 'SQL could not start.');
    } else if (msg.type === 'result' && current && msg.id === current.id) {
      clearTimeout(timer);
      post(msg.result ? { type: 'cg:sql-result', id: current.id, result: msg.result } : { type: 'cg:sql-result', id: current.id, failed: msg.crashed });
      current = null;
      runsOnWorker++;
      if (runsOnWorker >= RECYCLE_AFTER_RUNS) {
        stopWorker();
        startWorker();
      } else {
        next();
      }
    }
  }

  function next() {
    if (!ready || current || queue.length === 0) return;
    current = queue.shift();
    worker.postMessage({ type: 'run', id: current.id, request: current.request });
    timer = setTimeout(function () {
      // Stuck (a query that never ends, like a recursive count with no stop): replace the worker.
      var stuck = current;
      current = null;
      stopWorker();
      post({ type: 'cg:sql-result', id: stuck.id, timedOut: true });
      startWorker();
    }, RUN_TIMEOUT_MS);
  }

  window.addEventListener('message', function (event) {
    if (event.source !== window.parent) return;
    var msg = event.data || {};
    // The first message from the app tells us where to send replies.
    if (!parentOrigin) parentOrigin = event.origin;
    if (event.origin !== parentOrigin) return;

    if (msg.type === 'cg:sql-hello') {
      post({ type: 'cg:sql-status', status: status });
    } else if (msg.type === 'cg:sql-run' && typeof msg.id === 'string' && msg.request && typeof msg.request === 'object') {
      if (!worker) startWorker(); // after a failure, try again
      queue.push({ id: msg.id, request: msg.request });
      next();
    } else if (msg.type === 'cg:sql-cancel' && typeof msg.id === 'string') {
      queue = queue.filter(function (run) { return run.id !== msg.id; });
    }
  });

  startWorker();
})();
