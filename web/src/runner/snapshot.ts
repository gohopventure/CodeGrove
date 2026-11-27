/**
 * Portfolio pictures: a PNG of the learner's page, taken inside the sandboxed preview.
 *
 * The preview has an opaque origin, so the app can't read its pixels. Instead the page draws itself: the harness
 * copies the live DOM (with the values typed into inputs), turns it into an SVG <foreignObject> image, paints that
 * onto a canvas and sends the PNG to the app. Nothing leaves the frame except that one message to its parent, and
 * the frame's CSP already allows data: images, so no policy changes.
 *
 * TRADEOFF: this is a re-drawing of the page, not a real screenshot. It matches what's on screen for the HTML and
 * CSS the capstone uses; things a foreignObject image can't load (pictures from https: URLs, web fonts) come out
 * blank or in the default font. A pixel-exact screenshot would need a headless browser on the server.
 *
 * `installSnapshot` is serialized into the page with toString() (like the test harness in testEngine.ts), so it
 * must not use anything from outside its own body.
 */

/** App → preview. */
export interface SnapshotRequest {
  type: 'cg:snapshot';
  runId: string;
  id: string;
}

/** Preview → app: a PNG data URL, or why there isn't one. */
export interface SnapshotReply {
  type: 'cg:snapshot-result';
  runId: string;
  id: string;
  dataUrl?: string;
  error?: string;
}

/**
 * Pictures are 16:10 (the portfolio card's shape), cut from the top of the page, at most 1200 pixels wide (twice
 * the preview's width on small screens, for sharp text), and their data URL stays under 1.3 MB so the PNG fits
 * the API's 1 MB limit. The numbers are written inline below because the function is serialized on its own.
 */
export function installSnapshot(win: Window, runId: string): void {
  const doc = win.document;

  // Wait for the page's requests to answer (the practice/live API counter from mockApi.ts), then a beat for React.
  const whenIdle = (then: () => void) => {
    const started = Date.now();
    const poll = () => {
      const pending = (win as Window & { __cgMockPending?: number }).__cgMockPending ?? 0;
      if (pending > 0 && Date.now() - started < 2000) setTimeout(poll, 20);
      else setTimeout(then, 150);
    };
    poll();
  };

  const svgOfPage = (width: number, height: number) => {
    const root = doc.documentElement;
    const copy = root.cloneNode(true) as HTMLElement;

    // cloneNode copies attributes, not what was typed: carry the live values over.
    const live = root.querySelectorAll('input, textarea, select');
    const copies = copy.querySelectorAll('input, textarea, select');
    live.forEach((element, i) => {
      const target = copies[i];
      if (!target) return;
      if (element.tagName === 'INPUT') {
        const input = element as HTMLInputElement;
        if (input.type === 'checkbox' || input.type === 'radio') {
          if (input.checked) target.setAttribute('checked', '');
          else target.removeAttribute('checked');
        } else if (input.type !== 'password' && input.type !== 'file') {
          target.setAttribute('value', input.value);
        }
      } else if (element.tagName === 'TEXTAREA') {
        target.textContent = (element as HTMLTextAreaElement).value;
      } else if (element.tagName === 'SELECT') {
        const select = element as HTMLSelectElement;
        target.querySelectorAll('option').forEach((option, j) => {
          if (select.options[j]?.selected) option.setAttribute('selected', '');
          else option.removeAttribute('selected');
        });
      }
    });

    // A canvas copies empty: swap in a picture of what it shows.
    const canvases = root.querySelectorAll('canvas');
    copy.querySelectorAll('canvas').forEach((target, i) => {
      try {
        const img = doc.createElement('img');
        img.setAttribute('src', (canvases[i] as HTMLCanvasElement).toDataURL());
        img.setAttribute('width', String((canvases[i] as HTMLCanvasElement).width));
        target.replaceWith(img);
      } catch {
        // leave it blank
      }
    });

    // Scripts never run in an image anyway; the rest could only fail to load.
    copy.querySelectorAll('script, noscript, iframe, object, embed, link, meta').forEach((n) => n.remove());

    // The page as it scrolls from the top, at the preview's width.
    const page = new XMLSerializer().serializeToString(copy);
    return (
      `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">` +
      `<foreignObject x="0" y="0" width="${width}" height="${height}">${page}</foreignObject></svg>`
    );
  };

  // In a real page the html's (or else the body's) background fills the whole window. An image of the page only
  // paints the body's box, so the canvas is filled with that color first. White when neither has one.
  const pageColor = () => {
    const color = (element: Element | null) => {
      const c = element ? win.getComputedStyle(element).backgroundColor : '';
      return c && c !== 'transparent' && c !== 'rgba(0, 0, 0, 0)' ? c : null;
    };
    return color(doc.documentElement) ?? color(doc.body) ?? '#ffffff';
  };

  const capture = (scale: number, done: (dataUrl?: string, error?: string) => void) => {
    const width = Math.max(320, Math.min(doc.documentElement.clientWidth || 800, 1600));
    const height = Math.round(width * 0.625);
    const image = new Image();
    image.onload = () => {
      try {
        const canvas = doc.createElement('canvas');
        canvas.width = Math.round(width * scale);
        canvas.height = Math.round(height * scale);
        const g = canvas.getContext('2d');
        if (!g) return done(undefined, 'no canvas');
        g.fillStyle = pageColor();
        g.fillRect(0, 0, canvas.width, canvas.height);
        g.scale(scale, scale);
        g.drawImage(image, 0, 0, width, height);
        done(canvas.toDataURL('image/png'));
      } catch (e) {
        // A browser that treats foreignObject pictures as unsafe refuses toDataURL (a "tainted" canvas).
        done(undefined, typeof (e as { name?: unknown })?.name === 'string' ? (e as { name: string }).name : 'failed');
      }
    };
    image.onerror = () => done(undefined, 'the page could not be drawn');
    image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgOfPage(width, height));
  };

  win.addEventListener('message', (event: MessageEvent) => {
    const msg = event.data as { type?: string; runId?: string; id?: string } | null;
    if (event.source !== win.parent || msg?.type !== 'cg:snapshot' || msg.runId !== runId || typeof msg.id !== 'string') return;
    const id = msg.id;
    const reply = (dataUrl?: string, error?: string) =>
      win.parent.postMessage({ type: 'cg:snapshot-result', runId, id, dataUrl, error }, '*');

    whenIdle(() => {
      const width = Math.max(320, Math.min(doc.documentElement.clientWidth || 800, 1600));
      const sharp = Math.max(1, Math.min(2, 1200 / width));
      // A busy page at 2× can come out too big for the API: try again at 1×.
      capture(sharp, (dataUrl, error) => {
        if (!dataUrl || dataUrl.length <= 1_300_000 || sharp === 1) return reply(dataUrl, error);
        capture(1, (small, smallError) => reply(small && small.length <= 1_300_000 ? small : undefined, smallError ?? (small ? 'too big' : undefined)));
      });
    });
  });
}
