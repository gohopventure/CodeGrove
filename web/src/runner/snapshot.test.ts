import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { buildRunDocument } from './compose';
import { installSnapshot, type SnapshotReply } from './snapshot';

/**
 * jsdom can't draw, so Image and the canvas are stubbed: the stub Image "loads" whatever SVG it's given, and the
 * canvas turns that into a fake PNG data URL that records the drawing size. What's checked is everything around the
 * drawing: which messages are answered, what goes into the SVG, the size, and the fallback for big pictures.
 * The real drawing is checked in Chrome (see the capstone plan's verification notes).
 */
describe('portfolio pictures (installSnapshot)', () => {
  let svgs: string[];
  let replies: SnapshotReply[];
  let pngLength: (width: number) => number;
  let fills: string[];

  beforeEach(() => {
    svgs = [];
    replies = [];
    pngLength = () => 1000;
    fills = [];
    document.documentElement.innerHTML =
      '<head><title>Junior League</title><style>h1 { color: teal; }</style><script>window.secret = 1</script></head>' +
      '<body><h1>Junior League</h1><input id="name" value="old"><input id="pw" type="password">' +
      '<input id="ok" type="checkbox"><textarea id="note"></textarea>' +
      '<select id="team"><option>Owls</option><option>Frogs</option></select></body>';
    (document.getElementById('name') as HTMLInputElement).value = 'Pip & co';
    (document.getElementById('pw') as HTMLInputElement).value = 'hunter2';
    (document.getElementById('ok') as HTMLInputElement).checked = true;
    (document.getElementById('note') as HTMLTextAreaElement).value = 'a <note>';
    (document.getElementById('team') as HTMLSelectElement).selectedIndex = 1;
    Object.defineProperty(document.documentElement, 'clientWidth', { value: 600, configurable: true });

    class StubImage {
      onload: (() => void) | null = null;
      onerror: (() => void) | null = null;
      set src(url: string) {
        svgs.push(decodeURIComponent(url.slice('data:image/svg+xml;charset=utf-8,'.length)));
        setTimeout(() => this.onload?.(), 0);
      }
    }
    vi.stubGlobal('Image', StubImage);
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(
      () => ({ fillRect() {}, scale() {}, drawImage() {}, set fillStyle(c: string) { fills.push(c); } }) as unknown as CanvasRenderingContext2D,
    );
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(function (this: HTMLCanvasElement) {
      return `data:image/png;base64,${this.width}x${this.height}:`.padEnd(pngLength(this.width), 'A');
    });
    vi.spyOn(window.parent, 'postMessage').mockImplementation((msg: unknown) => {
      if ((msg as SnapshotReply).type === 'cg:snapshot-result') replies.push(msg as SnapshotReply);
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  const ask = (runId: string, source: MessageEventSource | null = window.parent) =>
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'cg:snapshot', runId, id: 'q1' }, source }));
  const settle = () => vi.waitFor(() => expect(replies.length).toBeGreaterThan(0), { timeout: 2000 });

  it('draws the page with what was typed, without scripts or passwords, at twice the width up to 1200', async () => {
    installSnapshot(window, 'run-0');
    ask('run-0');
    await settle();

    expect(replies).toEqual([expect.objectContaining({ runId: 'run-0', id: 'q1', dataUrl: expect.stringMatching(/^data:image\/png;base64,1200x750:/) })]);
    const svg = svgs[0]!;
    expect(svg).toMatch(/^<svg xmlns="http:\/\/www.w3.org\/2000\/svg" width="600" height="375"><foreignObject/);
    expect(svg).toContain('<h1>Junior League</h1>');
    expect(svg).toContain('h1 { color: teal; }'); // the page's CSS comes along
    expect(svg).toContain('value="Pip &amp; co"');
    expect(svg).toContain('checked=""');
    expect(svg).toContain('a &lt;note&gt;');
    expect(svg).toMatch(/<option selected="">Frogs<\/option>/);
    expect(svg).not.toContain('hunter2');
    expect(svg).not.toContain('<script');
    expect(svg).not.toContain('window.secret');
    // The live page itself is untouched.
    expect(document.querySelector('script')).not.toBeNull();
    expect(document.getElementById('name')!.getAttribute('value')).toBe('old');
  });

  it("fills the picture with the page's background color, like a real window", async () => {
    installSnapshot(window, 'run-color');
    ask('run-color');
    await settle();
    expect(fills).toEqual(['#ffffff']);

    replies = [];
    document.body.style.backgroundColor = 'rgb(240, 253, 244)';
    ask('run-color');
    await settle();
    expect(fills[1]).toBe('rgb(240, 253, 244)');
  });

  it('only answers its own run, and only its parent', async () => {
    installSnapshot(window, 'run-2');
    ask('another-run');
    ask('run-2', null);
    await new Promise((r) => setTimeout(r, 400));
    expect(replies).toEqual([]);
  });

  it('tries again at 1× when the sharp picture is too big for the API, and gives up if that is too', async () => {
    pngLength = (width) => (width >= 1200 ? 2_000_000 : 900_000);
    installSnapshot(window, 'run-3');
    ask('run-3');
    await settle();
    expect(replies[0]!.dataUrl).toMatch(/^data:image\/png;base64,600x375:/);

    replies = [];
    pngLength = () => 2_000_000;
    ask('run-3');
    await settle();
    expect(replies[0]).toEqual(expect.objectContaining({ dataUrl: undefined, error: 'too big' }));
  });

  it('says why when the browser refuses to export the drawing', async () => {
    vi.spyOn(HTMLCanvasElement.prototype, 'toDataURL').mockImplementation(() => {
      throw new DOMException('Tainted canvases may not be exported.', 'SecurityError');
    });
    installSnapshot(window, 'run-4');
    ask('run-4');
    await settle();
    expect(replies[0]).toEqual(expect.objectContaining({ dataUrl: undefined, error: 'SecurityError' }));
  });

  it('is part of every preview document, bound to its run', () => {
    const html = buildRunDocument('<p>hi</p>', 'run-</script>', []);
    expect(html).toContain('cg:snapshot-result');
    expect(html).toContain('(window, "run-\\u003c/script>")');
  });
});
