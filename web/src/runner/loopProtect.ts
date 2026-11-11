import { parse } from 'acorn';

interface Edit {
  pos: number;
  text: string;
}

interface AnyNode {
  type: string;
  start: number;
  end: number;
  body?: AnyNode | AnyNode[];
  [key: string]: unknown;
}

const LOOPS = new Set(['ForStatement', 'ForInStatement', 'ForOfStatement', 'WhileStatement', 'DoWhileStatement']);

/**
 * Adds a time guard to every loop so an accidental infinite loop throws a friendly error instead of
 * freezing the learner's tab. Each loop is wrapped in its own block with a start time:
 *
 *   while (x) { ... }   →   {let __cg0=Date.now(); while (x) {if(Date.now()-__cg0>1500)throw ...; ... }}
 *
 * Wrapping in a block is valid anywhere a statement is (including `if (a) while (b) ...`); labeled
 * loops are wrapped outside their label so `continue label` still works.
 * If the code doesn't parse, it's returned unchanged and the browser reports the syntax error.
 */
export function protectLoops(code: string, maxMs = 1500): string {
  let ast: AnyNode;
  try {
    ast = parse(code, { ecmaVersion: 'latest', sourceType: 'script', allowReturnOutsideFunction: true }) as unknown as AnyNode;
  } catch {
    return code;
  }

  const edits: Edit[] = [];
  let counter = 0;
  const message = JSON.stringify(
    'Your loop has been running for a long time, so we stopped it. Check that its condition eventually becomes false.',
  );

  const visit = (node: AnyNode, parent: AnyNode | null) => {
    if (LOOPS.has(node.type) && node.body && !Array.isArray(node.body)) {
      const id = `__cg${counter++}`;
      const outer = parent?.type === 'LabeledStatement' ? parent : node;
      const guard = `if(Date.now()-${id}>${maxMs}){throw new RangeError(${message});}`;
      const body = node.body;

      edits.push({ pos: outer.start, text: `{let ${id}=Date.now();` });
      if (body.type === 'BlockStatement') {
        edits.push({ pos: body.start + 1, text: guard });
      } else {
        edits.push({ pos: body.start, text: `{${guard}` });
        edits.push({ pos: body.end, text: '}' });
      }
      edits.push({ pos: outer.end, text: '}' });
    }

    for (const key of Object.keys(node)) {
      const value = node[key];
      if (Array.isArray(value)) {
        for (const child of value) if (isNode(child)) visit(child, node);
      } else if (isNode(value)) {
        visit(value, node);
      }
    }
  };

  visit(ast, null);

  // Apply from the end so earlier positions stay valid. Edits at the same position are all "}" or
  // an opening that sorts after closings (stable sort keeps push order), so order is safe.
  edits.sort((a, b) => b.pos - a.pos);
  let out = code;
  for (const e of edits) out = out.slice(0, e.pos) + e.text + out.slice(e.pos);
  return out;
}

function isNode(v: unknown): v is AnyNode {
  return typeof v === 'object' && v !== null && typeof (v as AnyNode).type === 'string' && typeof (v as AnyNode).start === 'number';
}
