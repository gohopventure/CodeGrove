/*
 * Turns a Blockly workspace into the JavaScript a learner would write by hand.
 *
 * Blockly's stock JavaScript generator produces correct but unfamiliar code (`var`, `window.alert`,
 * `[a, b].join('')`, `x = (typeof x === 'number' ? x : 0) + 1`). Explorer lessons use blocks as a
 * bridge to text, so the generated code is tuned to look like the code in the lessons:
 *
 *   let declarations · console.log for "print" · `a + b` for joining text · `x = x + 1` for "change by"
 *   · double-quoted strings
 *
 * This module has no React or DOM dependencies, so the content checks (Vitest, jsdom) use the same
 * generator as the app.
 */
import * as Blockly from 'blockly/core';
import 'blockly/blocks';
import { javascriptGenerator, Order } from 'blockly/javascript';
import * as En from 'blockly/msg/en';

Blockly.setLocale(En as unknown as { [key: string]: string });
Blockly.Msg['TEXT_PRINT_TITLE'] = 'print to Console %1';
Blockly.Msg['TEXT_PRINT_TOOLTIP'] = 'Show a message in the Console under the preview.';
// Rendered function blocks get a default "Describe this function..." comment, which would also land in the
// generated code. Leave it out; learners can still add their own comments.
Blockly.Msg['PROCEDURES_DEFRETURN_COMMENT'] = '';
Blockly.Msg['PROCEDURES_DEFNORETURN_COMMENT'] = '';

/** First line of every generated script.js, so learners know where the code came from. */
export const BLOCKS_HEADER = '// This JavaScript was made from your blocks. Switch to Text to edit it yourself.\n';

type Gen = typeof javascriptGenerator;
const gen = javascriptGenerator as Gen & { definitions_: Record<string, string> };

gen.quote_ = (text: string) => JSON.stringify(text);

// let instead of var, and no top-level declarations for function parameters.
const baseInit = gen.init.bind(gen);
gen.init = (workspace: Blockly.Workspace) => {
  baseInit(workspace);
  const [noReturn, withReturn] = Blockly.Procedures.allProcedures(workspace);
  // Compare generated names on both sides: Blockly renames variables that clash with JS globals (name → name2).
  const params = new Set([...noReturn, ...withReturn].flatMap(([, args]) => args).map((arg) => gen.getVariableName(arg)));
  const names = Blockly.Variables.allUsedVarModels(workspace)
    .map((v) => gen.getVariableName(v.getId()))
    .filter((name) => !params.has(name));
  if (names.length) gen.definitions_['variables'] = `let ${names.join(', ')};`;
  else delete gen.definitions_['variables'];
};

gen.forBlock['text_print'] = (block, g) => `console.log(${g.valueToCode(block, 'TEXT', Order.NONE) || '""'});\n`;

gen.forBlock['math_change'] = (block, g) => {
  const name = g.getVariableName(block.getFieldValue('VAR'));
  const delta = g.valueToCode(block, 'DELTA', Order.ADDITION) || '0';
  return /^-\d/.test(delta) ? `${name} = ${name} - ${delta.slice(1)};\n` : `${name} = ${name} + ${delta};\n`;
};

// "join" → a + b + c, the way people write it. Only when the first two items are both number blocks is
// the first wrapped in String(), so they're joined, not added (3 + 4 + " cats" would be "7 cats"). Variables
// are left alone, as a person would write them.
gen.forBlock['text_join'] = (block, g) => {
  const count = (block as Blockly.Block & { itemCount_: number }).itemCount_;
  if (count === 0) return ['""', Order.ATOMIC];
  const typeOf = (i: number) => block.getInputTargetBlock('ADD' + i)?.type ?? '';
  const isText = (i: number) => typeOf(i) === 'text';
  const isNumber = (i: number) => typeOf(i).startsWith('math_');
  const parts = Array.from({ length: count }, (_, i) => g.valueToCode(block, 'ADD' + i, Order.ADDITION) || '""');
  if (count === 1) return isText(0) ? [parts[0]!, Order.ATOMIC] : [`String(${g.valueToCode(block, 'ADD0', Order.NONE) || '""'})`, Order.FUNCTION_CALL];
  if (isNumber(0) && isNumber(1)) parts[0] = `String(${g.valueToCode(block, 'ADD0', Order.NONE) || '""'})`;
  return [parts.join(' + '), Order.ADDITION];
};

/** The script.js text for a workspace. */
export function workspaceToScript(workspace: Blockly.Workspace): string {
  const code = gen.workspaceToCode(workspace).replace(/\n{3,}/g, '\n\n').trim();
  return BLOCKS_HEADER + (code ? `\n${code}\n` : '');
}

/** Generates script.js from a saved workspace state without rendering anything (used by tests). */
export function stateToScript(state: object | null | undefined): string {
  const workspace = new Blockly.Workspace();
  try {
    if (state) Blockly.serialization.workspaces.load(state, workspace);
    return workspaceToScript(workspace);
  } finally {
    workspace.dispose();
  }
}

export { Blockly };
