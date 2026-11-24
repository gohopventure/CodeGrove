import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CSharpRunResult, LiveServerReply } from './csharp';
import type { LiveFetchMessage } from './mockApi';

const runCSharp = vi.fn<(...args: unknown[]) => Promise<CSharpRunResult>>();
const sendToLiveServer = vi.fn<(session: string, request: unknown) => Promise<LiveServerReply>>();
vi.mock('./csharp', () => ({ runCSharp: (...a: unknown[]) => runCSharp(...a), sendToLiveServer: (s: string, r: unknown) => sendToLiveServer(s, r) }));

const { LiveApi, NOT_RUNNING, NOT_STARTED } = await import('./liveApi');

const started = (session: string | null): CSharpRunResult => ({
  compiled: session !== null, diagnostics: [], console: [], output: '', stopped: 'None', results: [], compileMs: 1, runMs: 1, liveSession: session,
});
const ok = (body: string): LiveServerReply => ({
  found: true, console: [],
  exchange: { method: 'GET', path: '/api/clubs', status: 200, reason: 'OK', contentType: 'application/json; charset=utf-8', body, headers: { 'Content-Type': 'application/json; charset=utf-8' } },
});
const get = (path: string): LiveFetchMessage => ({ type: 'cg:fetch', runId: 'r', id: 'x', method: 'GET', path, headers: {}, body: null });
const files = { 'api/Program.cs': 'app.Run();', 'web/App.jsx': '…' };

describe('LiveApi', () => {
  beforeEach(() => {
    runCSharp.mockReset();
    sendToLiveServer.mockReset();
  });

  it('starts the server from the picked files with keepServer, then forwards requests and logs them', async () => {
    runCSharp.mockResolvedValue(started('s1'));
    sendToLiveServer.mockResolvedValue(ok('[]'));
    const api = new LiveApi((f) => Object.fromEntries(Object.entries(f).filter(([k]) => k.startsWith('api/'))));

    await api.start(files);
    expect(runCSharp).toHaveBeenCalledWith({ 'api/Program.cs': 'app.Run();' }, [], undefined, undefined, { keepServer: true });

    const reply = await api.handle(get('/api/clubs'));
    expect(reply).toMatchObject({ status: 200, statusText: 'OK', body: '[]' });
    expect(sendToLiveServer).toHaveBeenCalledWith('s1', { method: 'GET', path: '/api/clubs', body: undefined, headers: {} });
    expect(api.getEntries()).toMatchObject([{ method: 'GET', path: '/api/clubs', status: 200, responseBody: '[]' }]);
  });

  it('waits for a start that is still compiling', async () => {
    let finish!: (r: CSharpRunResult) => void;
    runCSharp.mockReturnValue(new Promise((r) => (finish = r)));
    sendToLiveServer.mockResolvedValue(ok('[]'));
    const api = new LiveApi();
    void api.start(files);
    const pending = api.handle(get('/api/clubs'));
    finish(started('s1'));
    expect(await pending).toMatchObject({ status: 200 });
  });

  it('restarts a server the runner lost, keeping the log, and retries once', async () => {
    runCSharp.mockResolvedValueOnce(started('s1')).mockResolvedValueOnce(started('s2'));
    sendToLiveServer.mockResolvedValueOnce(ok('[1]')).mockResolvedValueOnce({ found: false, console: [] }).mockResolvedValueOnce(ok('[]'));
    const api = new LiveApi();
    await api.start(files);
    await api.handle(get('/api/a'));

    expect(await api.handle(get('/api/b'))).toMatchObject({ status: 200, body: '[]' });
    expect(runCSharp).toHaveBeenCalledTimes(2);
    expect(sendToLiveServer).toHaveBeenLastCalledWith('s2', expect.anything());
    expect(api.getEntries().map((e) => e.path)).toEqual(['/api/a', '/api/b']);
  });

  it("explains, without recompiling, when the server never started or isn't running yet", async () => {
    const fresh = new LiveApi();
    expect(await fresh.handle(get('/api/clubs'))).toEqual({ error: NOT_RUNNING });

    runCSharp.mockResolvedValue(started(null)); // a compile error
    const broken = new LiveApi();
    await broken.start(files);
    expect(await broken.handle(get('/api/clubs'))).toEqual({ error: NOT_STARTED });
    expect(await broken.handle(get('/api/clubs'))).toEqual({ error: NOT_STARTED });
    expect(runCSharp).toHaveBeenCalledTimes(1);
    expect(sendToLiveServer).not.toHaveBeenCalled();
    expect(broken.getEntries()[0]).toMatchObject({ status: null, error: NOT_STARTED });
  });

  it("doesn't restart after a time limit, and passes the reason on", async () => {
    runCSharp.mockResolvedValue(started('s1'));
    sendToLiveServer.mockResolvedValue({ found: false, timedOut: true, console: [{ level: 'error', text: 'took more than 15 seconds' }] });
    const api = new LiveApi();
    await api.start(files);
    expect(await api.handle(get('/api/forever'))).toEqual({ error: 'took more than 15 seconds' });
    expect(runCSharp).toHaveBeenCalledTimes(1);
  });

  it('a new start clears the log', async () => {
    runCSharp.mockResolvedValue(started('s1'));
    sendToLiveServer.mockResolvedValue(ok('[]'));
    const api = new LiveApi();
    await api.start(files);
    await api.handle(get('/api/clubs'));
    await api.start(files);
    expect(api.getEntries()).toEqual([]);
  });
});
