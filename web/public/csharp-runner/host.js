// Codegrove C# runner host: see index.html for what this page is and the messages it understands.
// Plain browser JavaScript on purpose (no build step), like runner.html.
(function () {
  'use strict';

  var RUN_TIMEOUT_MS = 15000; // one run, from when the worker gets it (compiling + running + tests)
  var BOOT_TIMEOUT_MS = 180000; // downloading and starting .NET (only slow the very first time)
  var RECYCLE_AFTER_RUNS = 150; // .NET in the browser keeps every assembly it loads; a fresh worker frees them

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
    post({ type: 'cg:cs-status', status: next, detail: detail });
  }

  function startWorker() {
    ready = false;
    runsOnWorker = 0;
    setStatus('loading');
    worker = new Worker('worker.js', { type: 'module' });
    worker.onmessage = onWorkerMessage;
    worker.onerror = function (event) {
      event.preventDefault();
      failWorker('The C# runner stopped unexpectedly. ' + (event.message || ''));
    };
    clearTimeout(timer);
    timer = setTimeout(function () {
      failWorker('Getting C# ready took too long. Check your internet connection and try again.');
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
    waiting.forEach(function (job) {
      post({ type: job.kind === 'serve' ? 'cg:cs-served' : 'cg:cs-result', id: job.id, failed: detail });
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
      failWorker(msg.detail || 'C# could not start.');
    } else if (msg.type === 'served' && current && current.kind === 'serve' && msg.id === current.id) {
      // A live-server request: doesn't count towards recycling (recycling would stop the server).
      clearTimeout(timer);
      post({ type: 'cg:cs-served', id: current.id, response: msg.response });
      current = null;
      next();
    } else if (msg.type === 'result' && current && msg.id === current.id) {
      clearTimeout(timer);
      post({ type: 'cg:cs-result', id: current.id, result: msg.result });
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
    worker.postMessage({ type: current.kind, id: current.id, request: current.request });
    timer = setTimeout(function () {
      // Stuck (a loop the guard can't see, or a huge computation): replace the worker. That also ends
      // any live server, so the app's next request for it is answered "not running" and it starts a new one.
      var stuck = current;
      current = null;
      stopWorker();
      post({ type: stuck.kind === 'serve' ? 'cg:cs-served' : 'cg:cs-result', id: stuck.id, timedOut: true });
      startWorker();
    }, RUN_TIMEOUT_MS);
  }

  window.addEventListener('message', function (event) {
    if (event.source !== window.parent) return;
    var msg = event.data || {};
    // The first message from the app tells us where to send replies.
    if (!parentOrigin) parentOrigin = event.origin;
    if (event.origin !== parentOrigin) return;

    if (msg.type === 'cg:cs-hello') {
      post({ type: 'cg:cs-status', status: status });
    } else if (msg.type === 'cg:cs-run' && typeof msg.id === 'string' && msg.request && typeof msg.request === 'object') {
      if (!worker) startWorker(); // after a failure, try again
      queue.push({ kind: 'run', id: msg.id, request: msg.request });
      next();
    } else if (msg.type === 'cg:cs-serve' && typeof msg.id === 'string' && msg.request && typeof msg.request === 'object') {
      if (!worker) startWorker();
      queue.push({ kind: 'serve', id: msg.id, request: msg.request });
      next();
    } else if (msg.type === 'cg:cs-cancel' && typeof msg.id === 'string') {
      queue = queue.filter(function (run) { return run.id !== msg.id; });
    }
  });

  startWorker();
})();
