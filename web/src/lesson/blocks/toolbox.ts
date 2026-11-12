/*
 * Toolbox categories for blocks mode. Content picks categories by key (challenge.blocks.toolbox), so
 * each lesson shows only the blocks it needs. Keep the keys in sync with ContentSeeder.BlockCategories.
 *
 * Blocks that normally need the "gear" (mutator) to reshape, like if/else or a 3-part join, are offered
 * ready-made, because the mutator UI is hard for younger learners.
 */
const num = (n: number) => ({ shadow: { type: 'math_number', fields: { NUM: n } } });
const txt = (t: string) => ({ shadow: { type: 'text', fields: { TEXT: t } } });

const CATEGORIES = {
  variables: { kind: 'category', name: 'Variables', categorystyle: 'variable_category', custom: 'VARIABLE' },
  math: {
    kind: 'category', name: 'Math', categorystyle: 'math_category',
    contents: [
      { kind: 'block', type: 'math_number', fields: { NUM: 0 } },
      { kind: 'block', type: 'math_arithmetic', fields: { OP: 'ADD' }, inputs: { A: num(1), B: num(1) } },
      { kind: 'block', type: 'math_arithmetic', fields: { OP: 'MULTIPLY' }, inputs: { A: num(1), B: num(1) } },
    ],
  },
  text: {
    kind: 'category', name: 'Text', categorystyle: 'text_category',
    contents: [
      { kind: 'block', type: 'text' },
      { kind: 'block', type: 'text_join', extraState: { itemCount: 2 } },
      { kind: 'block', type: 'text_join', extraState: { itemCount: 3 } },
      { kind: 'block', type: 'text_join', extraState: { itemCount: 4 } },
    ],
  },
  logic: {
    kind: 'category', name: 'Logic', categorystyle: 'logic_category',
    contents: [
      { kind: 'block', type: 'controls_if' },
      { kind: 'block', type: 'controls_if', extraState: { hasElse: true } },
      { kind: 'block', type: 'controls_if', extraState: { elseIfCount: 1, hasElse: true } },
      { kind: 'block', type: 'logic_compare', fields: { OP: 'EQ' } },
      { kind: 'block', type: 'logic_boolean' },
      { kind: 'block', type: 'logic_operation' },
      { kind: 'block', type: 'logic_negate' },
    ],
  },
  loops: {
    kind: 'category', name: 'Loops', categorystyle: 'loop_category',
    contents: [
      // The lessons call the counter i. Without naming it, Blockly picks an unused name (j) when i exists.
      { kind: 'block', type: 'controls_for', fields: { VAR: { name: 'i' } }, inputs: { FROM: num(1), TO: num(10), BY: num(1) } },
      { kind: 'block', type: 'controls_repeat_ext', inputs: { TIMES: num(5) } },
      { kind: 'block', type: 'controls_whileUntil' },
    ],
  },
  functions: { kind: 'category', name: 'Functions', categorystyle: 'procedure_category', custom: 'PROCEDURE' },
  console: {
    kind: 'category', name: 'Console', colour: '#5b6b7c',
    contents: [{ kind: 'block', type: 'text_print', inputs: { TEXT: txt('Hello!') } }],
  },
} as const;

export type BlockCategory = keyof typeof CATEGORIES;
export const BLOCK_CATEGORIES = Object.keys(CATEGORIES) as BlockCategory[];

export function toolboxFor(keys: readonly string[]) {
  return {
    kind: 'categoryToolbox',
    contents: keys.filter((k): k is BlockCategory => k in CATEGORIES).map((k) => CATEGORIES[k]),
  };
}
