/**
 * Runs learner SQL and checks it. Pure TypeScript with no browser APIs, so the same code runs in the
 * SQL runner's Web Worker (src/runner/sqlWorker.ts, on the preview origin) and in Vitest, where the
 * content tests check every published SQL challenge.
 *
 * The database is SQLite (sql.js: SQLite compiled to WebAssembly). Every run starts from nothing:
 *   1. a fresh in-memory database, with foreign keys switched ON (SQLite's default is off; SQL Server
 *      always enforces them, and the relational course relies on that);
 *   2. the challenge's hidden `.setup.sql` (tables and rows), which the learner can't edit;
 *   3. the learner's .sql files, one statement at a time, so an error can name its file and line;
 *   4. the challenge's tests, against the learner's results and the database as the learner left it.
 *
 * Dialect: SQLite, not SQL Server's T-SQL. Lessons stick to SQL that works the same in both, and
 * Builder lessons point out the differences (LIMIT vs TOP, AUTOINCREMENT vs IDENTITY, date functions).
 *
 * Runaway queries: SQLite in sql.js has no progress callback, so a query that never ends is stopped by
 * the host page, which kills the worker after a few seconds (public/sql-runner/host.js). Reading rows
 * stops at MAX_ROWS, so an endless recursive query that returns rows ends here on its own.
 */
import type { Database, SqlJsStatic, SqlValue } from 'sql.js';
import type { ConsoleLine, TestResult, TestSpec } from './types';

/** Rows kept per result set (enough for every lesson; protects the page from a million-row result). */
export const MAX_ROWS = 1000;
/** Rows kept per table in the "Tables" view. */
export const MAX_TABLE_ROWS = 50;

/** The hidden file in a challenge's starter files that builds the database. Dotfiles never show in the editor. */
export const SETUP_FILE = '.setup.sql';

export type SqlCell = number | string | null;

export interface SqlResultSet {
  /** Which file and line the statement that produced it starts on. */
  file: string;
  line: number;
  columns: string[];
  rows: SqlCell[][];
  /** True when there were more than MAX_ROWS rows and the rest were dropped. */
  truncated: boolean;
}

export interface SqlTable {
  name: string;
  columns: { name: string; type: string; primaryKey: boolean; notNull: boolean }[];
  rows: SqlCell[][];
  /** The real number of rows (rows holds at most MAX_TABLE_ROWS). */
  rowCount: number;
}

export interface SqlError {
  file: string;
  line: number;
  /** SQLite's own words, e.g. `no such column: nme`. */
  message: string;
  /** Plain-language version for beginners, or '' if there isn't one. */
  friendly: string;
}

export interface SqlRunRequest {
  /** The database to start from (the challenge's `.setup.sql`). */
  setup: string;
  /** The learner's files. Dotfiles (setup, app data) are ignored; .sql files run in key order. */
  files: Record<string, string>;
  tests?: TestSpec[];
}

export interface SqlRunResult {
  /** False when the setup itself failed: an authoring bug, not the learner's. */
  setupOk: boolean;
  /** The first error in the learner's code. Statements before it ran; statements after it didn't. */
  error: SqlError | null;
  resultSets: SqlResultSet[];
  /** Every table after the run, for the "Tables" view. */
  tables: SqlTable[];
  /** Ready for the Output panel: what each statement did, and the error in plain words. */
  console: ConsoleLine[];
  results: TestResult[];
  runMs: number;
}

/** Runs one request. Never throws for learner mistakes: they come back in `error` and `console`. */
export function runSql(SQL: SqlJsStatic, request: SqlRunRequest): SqlRunResult {
  const started = now();
  const db = new SQL.Database();
  const console: ConsoleLine[] = [];
  const resultSets: SqlResultSet[] = [];
  let error: SqlError | null = null;
  let setupOk = true;
  try {
    db.exec('PRAGMA foreign_keys = ON;');
    try {
      db.exec(request.setup);
    } catch (e) {
      setupOk = false;
      console.push({ level: 'error', text: `This challenge's starting database didn't build (our bug, not yours): ${messageOf(e)}` });
    }

    if (setupOk) {
      for (const [file, sql] of sqlFiles(request.files)) {
        error = runFile(db, file, sql, resultSets, console);
        if (error) break;
      }
      if (!error && resultSets.length === 0 && console.length === 0) {
        console.push({ level: 'info', text: 'Your code ran, but there was nothing to run. Write a statement, like SELECT * FROM a table.' });
      }
    }

    const tables = snapshotTables(db);
    const results = setupOk
      ? (request.tests ?? []).map((t) => evaluate(t, { db, error, resultSets, files: request.files }))
      : (request.tests ?? []).map((t) => ({ testId: t.id, passed: false, message: `${t.failMessage} (The starting database didn't build, so nothing could be checked.)` }));
    return { setupOk, error, resultSets, tables, console, results, runMs: Math.round(now() - started) };
  } finally {
    db.close();
  }
}

/** The learner's .sql files, in the order the challenge lists them, without dotfiles. */
function sqlFiles(files: Record<string, string>): [string, string][] {
  return Object.entries(files).filter(([name]) => !name.startsWith('.') && name.toLowerCase().endsWith('.sql'));
}

/** Runs a file statement by statement. Returns the first error, or null. */
function runFile(db: Database, file: string, sql: string, resultSets: SqlResultSet[], console: ConsoleLine[]): SqlError | null {
  const iterator = db.iterateStatements(sql);
  let remaining = sql; // the SQL not handed out yet: where a statement that fails to PREPARE starts
  let running: number | null = null; // the line of the statement being run, while it runs
  try {
    for (const statement of iterator) {
      const text = statement.getSQL();
      const line = lineAt(sql, sql.length - remaining.length + leadingNoise(text));
      remaining = iterator.getRemainingSQL();
      running = line;
      try {
        const columns = statement.getColumnNames();
        const rows: SqlCell[][] = [];
        let truncated = false;
        while (statement.step()) {
          if (rows.length >= MAX_ROWS) {
            truncated = true;
            break;
          }
          rows.push(statement.get().map(cell));
        }
        if (columns.length > 0) {
          resultSets.push({ file, line, columns, rows, truncated });
        } else {
          const said = describeChange(text, db.getRowsModified());
          if (said) console.push({ level: 'info', text: said });
        }
      } finally {
        statement.free();
      }
      running = null; // only after it succeeded: the catch below needs it when a statement fails
    }
    return null;
  } catch (e) {
    // Failed while running (a constraint, a missing table found late): that statement's line. Failed to
    // prepare (a syntax error, an unknown column): it hasn't been handed out, so it starts at `remaining`.
    const message = messageOf(e);
    const failedAt = running ?? lineAt(sql, sql.length - remaining.length + leadingNoise(remaining));
    const err: SqlError = { file, line: nearLine(sql, failedAt, message), message, friendly: friendlyError(message) };
    console.push({ level: 'error', text: describeError(err) });
    return err;
  }
}

/**
 * The line to blame for an error, found from the statement's first line on. SQLite only tells us which
 * statement failed, so for a multi-line query we look for what the message names:
 * - `near "X": syntax error`: the line where X appears;
 * - `no such column: X` / `no such table: X`: the line where X appears as a word;
 * - `misuse of aggregate …: sum()`: the first SUM( on or after the WHERE.
 * Falls back to the statement's first line.
 */
function nearLine(sql: string, statementLine: number, message: string): number {
  const lines = sql.split('\n').map((l) => stripComments(l).toLowerCase());
  const find = (test: (line: string) => boolean, from = statementLine): number | null => {
    for (let i = from - 1; i < lines.length; i++) if (test(lines[i] ?? '')) return i + 1;
    return null;
  };
  const escape = (t: string) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

  const near = /^near "([^"]+)"/.exec(message)?.[1]?.toLowerCase();
  if (near) return find((l) => l.includes(near)) ?? statementLine;

  // A missing name, or an ambiguous one: the first place it's written without a table in front.
  const missing = /^(?:no such (?:column|table)|ambiguous column name): (\S+)/.exec(message)?.[1]?.toLowerCase();
  if (missing) {
    const word = new RegExp(`(^|[^\\w.])${escape(missing)}(?!\\w)`);
    return find((l) => word.test(l)) ?? statementLine;
  }

  const aggregate = /^misuse of aggregate(?: function)?:? ?(\w+)/.exec(message)?.[1]?.toLowerCase();
  if (aggregate) {
    const where = find((l) => /\bwhere\b/.test(l)) ?? statementLine;
    const call = new RegExp(`\\b${escape(aggregate)}\\s*\\(`);
    return find((l) => call.test(l), where) ?? where;
  }
  return statementLine;
}

/** The text of the error for the Output panel: `Error in query.sql, line 3: <plain words> (<SQLite's words>)`. */
export function describeError(err: SqlError): string {
  const where = `Error in ${err.file}, line ${err.line}`;
  return err.friendly ? `${where}: ${err.friendly} (${err.message})` : `${where}: ${err.message}`;
}

/** "INSERT added 2 rows." and friends: feedback for statements that don't return rows. */
function describeChange(sql: string, changed: number): string {
  const words = stripComments(sql).trim().split(/\s+/).map((w) => w.toUpperCase());
  const rows = (n: number) => `${n} ${n === 1 ? 'row' : 'rows'}`;
  switch (words[0]) {
    case 'INSERT':
    case 'REPLACE':
      return `INSERT added ${rows(changed)}.`;
    case 'UPDATE':
      return `UPDATE changed ${rows(changed)}.`;
    case 'DELETE':
      return `DELETE removed ${rows(changed)}.`;
    case 'CREATE': {
      const what = words[1] === 'UNIQUE' ? 'INDEX' : words[1];
      const name = nameAfter(sql, what);
      return what === 'TABLE' ? `CREATE TABLE made the table ${name}.` : `CREATE ${what} made ${name}.`;
    }
    case 'DROP':
      return `DROP ${words[1]} removed ${nameAfter(sql, words[1])}.`;
    case 'ALTER':
      return `ALTER TABLE changed ${nameAfter(sql, 'TABLE')}.`;
    case 'BEGIN':
      return 'BEGIN started a transaction.';
    case 'COMMIT':
    case 'END':
      return 'COMMIT saved the transaction.';
    case 'ROLLBACK':
      return 'ROLLBACK undid the transaction.';
    default:
      return '';
  }
}

function nameAfter(sql: string, keyword: string | undefined): string {
  if (!keyword) return 'it';
  const m = new RegExp(`\\b${keyword}\\s+(?:IF\\s+(?:NOT\\s+)?EXISTS\\s+)?([\\w"\`\\[\\]]+)`, 'i').exec(stripComments(sql));
  return m?.[1] ? m[1].replace(/["`[\]]/g, '') : 'it';
}

// ---------------------------------------------------------------------------------------------------
// Plain-language errors. SQLite's messages are short; these say what to look at.
// ---------------------------------------------------------------------------------------------------

const FRIENDLY: [RegExp, (m: RegExpExecArray) => string][] = [
  [/^near "([^"]+)": syntax error/, (m) => `SQL got confused near "${m[1]}". Check the words just before it: a missing comma between columns, a misspelled keyword, or a missing quote.`],
  [/^incomplete input/, () => 'The statement stops too early. Is something missing at the end, like a value after WHERE, a closing bracket ) or a closing quote \'?'],
  [/^unrecognized token: "?'/, () => "There's a quote ' that never closes. Text values need a quote at both ends, like 'Juni'."],
  [/^unrecognized token: "([^"]+)"/, (m) => `SQL doesn't know what "${m[1]}" is. Text values go in single quotes, like 'Juni'.`],
  [/^no such table: (\S+)/, (m) => `There's no table called ${m[1]}. Look at the Tables tab for the exact table names.`],
  [/^no such column: (\S+)/, (m) => `There's no column called ${m[1]}. Check the spelling against the Tables tab. If you meant a piece of text, put it in single quotes, like '${(m[1] ?? '').replace(/^.*\./, '')}'.`],
  [/^ambiguous column name: (\S+)/, (m) => `More than one table has a column called ${m[1]}, so SQL doesn't know which one you mean. Write the table's name (or its alias), a dot, then ${m[1]}.`],
  [/^no such function: (\S+)/, (m) => `SQL doesn't have a function called ${m[1]}. Check the spelling.`],
  [/^wrong number of arguments to function (\S+?)\(\)/, (m) => `${m[1]}() got the wrong number of values inside its brackets.`],
  [/^table (\S+) has (\d+) columns but (\d+) values were supplied/, (m) => `The table ${m[1]} has ${m[2]} columns, but you gave ${m[3]} values. Give one value per column, or list the columns you're filling in.`],
  [/^(\d+) values for (\d+) columns/, (m) => `You listed ${m[2]} columns but gave ${m[1]} values. They need to match, one value for each column.`],
  [/^all VALUES must have the same number of terms/, () => 'Every row in VALUES needs the same number of values.'],
  [/^NOT NULL constraint failed: (\S+)/, (m) => `${m[1]} can't be empty (NULL): this column must always have a value.`],
  [/^UNIQUE constraint failed: (\S+)/, (m) => `There's already a row with that ${m[1]}. This column can't have the same value twice.`],
  [/^CHECK constraint failed: (.+)/, (m) => `A value broke one of the table's rules (CHECK ${m[1]}).`],
  [/^FOREIGN KEY constraint failed/, () => "That would leave a row pointing at something that doesn't exist. A foreign key must match a row in the other table, and you can't delete a row that other rows still point at."],
  [/^datatype mismatch/, () => "A value is the wrong type for its column, like text in a whole-number id column."],
  [/^table (\S+) already exists/, (m) => `There's already a table called ${m[1]}. Choose another name, or use CREATE TABLE IF NOT EXISTS.`],
  [/^duplicate column name: (\S+)/, (m) => `The column ${m[1]} is in the list twice.`],
  // SQLite says both "misuse of aggregate function sum()" and "misuse of aggregate: count()".
  [/^misuse of aggregate(?: function)?:? ?(\w+)?/, (m) => `${m[1] ? m[1].toUpperCase() + '()' : 'A function like COUNT() or SUM()'} works on groups of rows, so it can't go in WHERE, which checks one row at a time. To filter groups, use HAVING after GROUP BY.`],
  [/^a GROUP BY clause is required before HAVING/, () => 'HAVING filters groups, so it needs a GROUP BY before it.'],
  [/^aggregate functions are not allowed in the GROUP BY clause/, () => "GROUP BY needs column names, not COUNT() or SUM()."],
  [/^1st ORDER BY term out of range/, () => 'ORDER BY 1 means "the first column", but there are fewer columns than that.'],
  [/^SELECTs to the left and right of (UNION|INTERSECT|EXCEPT)/, (m) => `Both SELECTs in a ${m[1]} need the same number of columns.`],
  [/^cannot start a transaction within a transaction/, () => 'A transaction is already open. Finish it with COMMIT or ROLLBACK before starting another.'],
  [/^cannot (commit|rollback) - no transaction is active/, (m) => `There's no transaction to ${m[1]}. Start one with BEGIN first.`],
];

export function friendlyError(message: string): string {
  for (const [pattern, say] of FRIENDLY) {
    const m = pattern.exec(message);
    if (m) return say(m);
  }
  return '';
}

// ---------------------------------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------------------------------

/** Test kinds this engine understands. ContentSeeder.SqlTestKinds must list the same ones. */
export const SQL_TEST_KINDS = ['noErrors', 'sourceMatches', 'sqlResultEquals', 'sqlRowCount', 'sqlCheckQuery', 'sqlError'] as const;

interface Run {
  db: Database;
  error: SqlError | null;
  resultSets: SqlResultSet[];
  files: Record<string, string>;
}

function evaluate(test: TestSpec, run: Run): TestResult {
  const spec = test.spec;
  const pass = (): TestResult => ({ testId: test.id, passed: true, message: null });
  const fail = (why?: string): TestResult => ({ testId: test.id, passed: false, message: why ? `${test.failMessage} (${why})` : test.failMessage });
  try {
    switch (test.kind as string) {
      case 'noErrors':
        return run.error ? fail(`${run.error.file}, line ${run.error.line}: ${run.error.friendly || run.error.message}`) : pass();

      case 'sqlError': {
        if (!run.error) return fail('Your code ran without an error.');
        const contains = spec.contains as string | undefined;
        return !contains || run.error.message.toLowerCase().includes(contains.toLowerCase()) ? pass() : fail(`The error was: ${run.error.message}`);
      }

      case 'sourceMatches': {
        const file = spec.file as string | undefined;
        const source = Object.entries(run.files)
          .filter(([name]) => !name.startsWith('.') && (!file || name === file))
          .map(([, text]) => stripComments(text))
          .join('\n');
        const flags = spec.ignoreCase === false ? 'g' : 'gi'; // SQL keywords don't care about case, so neither do source checks
        const count = (source.match(new RegExp(spec.pattern as string, flags)) ?? []).length;
        const max = spec.max as number | undefined;
        const min = (spec.min as number | undefined) ?? (max === 0 ? 0 : 1);
        return count >= min && (max === undefined || count <= max) ? pass() : fail();
      }

      case 'sqlResultEquals':
      case 'sqlRowCount': {
        const set = pickResult(run, spec.result);
        if (!set) {
          if (run.error) return fail('Your code stopped at an error before it got there.');
          const n = run.resultSets.length;
          if (n === 0) return fail("Your code didn't return any rows. A SELECT returns rows.");
          return fail(`Your code gave ${n === 1 ? 'only 1 result' : `${n} results`}, and this check looks at result ${resultNumber(spec.result, n)}. Write one query for each step.`);
        }
        if (test.kind === 'sqlRowCount') {
          const n = set.rows.length;
          const ok = (spec.equals === undefined || n === spec.equals)
            && (spec.min === undefined || n >= (spec.min as number))
            && (spec.max === undefined || n <= (spec.max as number));
          return ok ? pass() : fail(`Your result has ${rowsWord(n)}.`);
        }
        const why = compareResult(set, spec);
        return why === null ? pass() : fail(why);
      }

      case 'sqlCheckQuery': {
        let rows: SqlCell[][];
        let columns: string[];
        try {
          const out = run.db.exec(spec.query as string);
          const last = out[out.length - 1];
          rows = last ? last.values.map((r) => r.map(cell)) : [];
          columns = last ? last.columns : [];
        } catch (e) {
          // The learner didn't write this query: name what's missing, without beginner tips about quotes.
          const msg = messageOf(e);
          const missing = /^no such (column|table): (\S+)/.exec(msg);
          if (missing) return fail(`While checking: there's no ${missing[1]} called ${missing[2]!.replace(/^.*\./, '')} yet.`);
          return fail(`While checking: ${friendlyError(msg) || msg}`);
        }
        if (Array.isArray(spec.equals)) {
          // The learner didn't write this query, so talk about what's in the table, not "your query".
          // `missing` lets a check say what's absent in its own words ("There's no plants table yet.").
          if (rows.length === 0 && spec.equals.length > 0) return fail((spec.missing as string | undefined) ?? "The check didn't find it in the table.");
          const why = compareResult({ file: '', line: 0, columns, rows, truncated: false }, { rows: spec.equals, ordered: spec.ordered, caseSensitive: spec.caseSensitive });
          if (why === null) return pass();
          const counts = /^Your query returned (\d+ rows?); it should return (\d+)\.$/.exec(why);
          return fail(counts ? `The check found ${counts[1]} in the table; it should find ${counts[2]}.` : why);
        }
        const actual = rows[0]?.[0] ?? null;
        if (sameCell(actual, spec.equals as SqlCell, spec.caseSensitive !== false)) return pass();
        if (actual === null && spec.missing) return fail(spec.missing as string);
        // `report` words the value for this check: "{value} of the 2 bad rows got in." (default "It was {value}.")
        const report = (spec.report as string | undefined) ?? 'It was {value}.';
        return fail(report.replace('{value}', typeof actual === 'string' ? `'${actual}'` : show(actual)));
      }

      default:
        return fail(`Codegrove doesn't know the check "${test.kind}" (our bug, not yours).`);
    }
  } catch (e) {
    return fail(`The check itself broke (our bug, not yours): ${messageOf(e)}`);
  }
}

/** Which result a check looks at, counted from 1 for messages. */
function resultNumber(which: unknown, count: number): number {
  return typeof which === 'number' ? (which < 0 ? count + which + 1 : which + 1) : count;
}

/** `result` in a spec: a number (0 = first result set, -1 = last) or omitted (the last one). */
function pickResult(run: Run, which: unknown): SqlResultSet | undefined {
  const sets = run.resultSets;
  const index = typeof which === 'number' ? (which < 0 ? sets.length + which : which) : sets.length - 1;
  return sets[index];
}

/**
 * Compares a result set with `{ columns?, rows, ordered?, sortedBy?, caseSensitive? }`. Returns null when it
 * matches, or a short sentence a learner can act on. Column names are compared without caring about
 * case; rows in any order unless `ordered` (for ORDER BY lessons).
 */
function compareResult(set: SqlResultSet, spec: Record<string, unknown>): string | null {
  const expected = (spec.rows as SqlCell[][]).map((r) => r.map(toCell));
  const caseSensitive = spec.caseSensitive !== false;
  const columns = spec.columns as string[] | undefined;
  const yourColumns = `Your columns were: ${set.columns.join(', ') || 'none'}.`;
  if (columns) {
    if (set.columns.length !== columns.length) {
      return `It should have ${columns.length} ${columns.length === 1 ? 'column' : 'columns'} (${columns.join(', ')}). ${yourColumns}`;
    }
    const wrong = columns.findIndex((c, i) => c.toLowerCase() !== set.columns[i]?.toLowerCase());
    if (wrong >= 0) return `Column ${wrong + 1} should be called ${columns[wrong]}. ${yourColumns}`;
  } else if (expected[0] && set.columns.length !== expected[0].length) {
    const n = expected[0].length;
    return `Each row should have ${n} ${n === 1 ? 'value' : 'values'}. ${yourColumns}`;
  }
  if (set.rows.length !== expected.length) {
    return `Your query returned ${rowsWord(set.rows.length)}; it should return ${expected.length}.`;
  }
  const same = (a: SqlCell[], b: SqlCell[]) => a.length === b.length && a.every((v, i) => sameCell(v, b[i] ?? null, caseSensitive));
  if (spec.ordered) {
    const bad = expected.findIndex((row, i) => !same(set.rows[i] ?? [], row));
    if (bad < 0) return null;
    if (sameMultiset(set.rows, expected, same)) return 'The right rows are there, but in a different order.';
    return `Row ${bad + 1} should be ${showRow(expected[bad] ?? [])}, but it was ${showRow(set.rows[bad] ?? [])}.`;
  }
  if (sameMultiset(set.rows, expected, same)) return sortProblem(set, spec.sortedBy);
  if (expected.length === 1 && set.rows.length === 1) return `It should be ${showRow(expected[0]!)}, but it was ${showRow(set.rows[0]!)}.`;
  const extra = set.rows.find((row) => !expected.some((e) => same(row, e)));
  return extra ? `This row shouldn't be there: ${showRow(extra)}.` : 'Some of the rows have the wrong values.';
}

/**
 * `sortedBy: ["age desc", "name"]` in a sqlResultEquals spec: the rows must be sorted by these result
 * columns, but rows that tie on all of them may come in any order. That's what ORDER BY promises, so a
 * learner who sorts by age alone isn't failed for the order SQLite happened to give two same-age rows.
 * (`ordered: true` demands one exact order instead.) Compares like SQLite's default: NULL first, then
 * numbers, then text by character code.
 */
function sortProblem(set: SqlResultSet, sortedBy: unknown): string | null {
  if (!Array.isArray(sortedBy) || sortedBy.length === 0) return null;
  const keys = (sortedBy as string[]).map((entry) => {
    const [name = '', dir = ''] = entry.trim().split(/\s+/);
    return { name, desc: dir.toLowerCase() === 'desc', index: set.columns.findIndex((c) => c.toLowerCase() === name.toLowerCase()) };
  });
  const missing = keys.find((k) => k.index < 0);
  if (missing) return `The result needs a column called ${missing.name} to check how it's sorted.`;
  for (let i = 1; i < set.rows.length; i++) {
    for (const key of keys) {
      const c = compareCells(set.rows[i - 1]![key.index] ?? null, set.rows[i]![key.index] ?? null) * (key.desc ? -1 : 1);
      if (c < 0) break; // in order on this column: later columns don't matter for this pair
      if (c > 0) {
        const how = keys.map((k) => `${k.name} ${direction(set, k.index, k.desc)}`).join(', then ');
        return `The right rows are there, but they aren't sorted by ${how}. Rows ${i} and ${i + 1} are the wrong way round.`;
      }
    }
  }
  return null;
}

/** "(A to Z)" for a text column, "(smallest first)" for numbers: what a learner would call it. */
function direction(set: SqlResultSet, index: number, desc: boolean): string {
  const text = set.rows.some((r) => typeof r[index] === 'string');
  if (text) return desc ? '(Z to A)' : '(A to Z)';
  return desc ? '(biggest first)' : '(smallest first)';
}

function compareCells(a: SqlCell, b: SqlCell): number {
  const rank = (v: SqlCell) => (v === null ? 0 : typeof v === 'number' ? 1 : 2);
  if (rank(a) !== rank(b)) return rank(a) - rank(b);
  if (a === null || b === null) return 0;
  return a < b ? -1 : a > b ? 1 : 0;
}

function sameMultiset(actual: SqlCell[][], expected: SqlCell[][], same: (a: SqlCell[], b: SqlCell[]) => boolean): boolean {
  const left = [...expected];
  for (const row of actual) {
    const i = left.findIndex((e) => same(row, e));
    if (i < 0) return false;
    left.splice(i, 1);
  }
  return left.length === 0;
}

function sameCell(a: SqlCell, b: SqlCell, caseSensitive: boolean): boolean {
  b = toCell(b);
  if (a === null || b === null) return a === b;
  if (typeof a === 'number' && typeof b === 'number') return Math.abs(a - b) < 1e-9;
  if (typeof a === 'string' && typeof b === 'string') return caseSensitive ? a === b : a.toLowerCase() === b.toLowerCase();
  return false;
}

/** JSON test data may use true/false; SQLite stores those as 1/0. */
function toCell(v: unknown): SqlCell {
  if (typeof v === 'boolean') return v ? 1 : 0;
  if (v === undefined) return null;
  return v as SqlCell;
}

function show(v: SqlCell): string {
  return v === null ? 'NULL' : typeof v === 'string' ? `'${v}'` : String(v);
}
function showRow(row: SqlCell[]): string {
  return row.map(show).join(' | ');
}
function rowsWord(n: number): string {
  return `${n} ${n === 1 ? 'row' : 'rows'}`;
}

// ---------------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------------

/** Every table and its rows, for the Tables view. */
function snapshotTables(db: Database): SqlTable[] {
  const names = db.exec("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY rowid")[0]?.values ?? [];
  return names.map(([name]) => {
    const table = String(name);
    const quoted = `"${table.replace(/"/g, '""')}"`;
    const info = db.exec(`PRAGMA table_info(${quoted})`)[0]?.values ?? [];
    const data = db.exec(`SELECT * FROM ${quoted} LIMIT ${MAX_TABLE_ROWS}`)[0]?.values ?? [];
    const count = Number(db.exec(`SELECT COUNT(*) FROM ${quoted}`)[0]?.values[0]?.[0] ?? 0);
    return {
      name: table,
      // table_info rows: cid, name, type, notnull, dflt_value, pk
      columns: info.map((c) => ({ name: String(c[1]), type: String(c[2] ?? ''), notNull: c[3] === 1, primaryKey: Number(c[5]) > 0 })),
      rows: data.map((r) => r.map(cell)),
      rowCount: count,
    };
  });
}

function cell(v: SqlValue): SqlCell {
  if (v instanceof Uint8Array) return `[${v.length} bytes]`;
  return v as SqlCell;
}

/** Removes -- and /* *\/ comments but leaves text in quotes alone. */
export function stripComments(sql: string): string {
  return sql.replace(/('(?:[^']|'')*'|"(?:[^"]|"")*")|--[^\n]*|\/\*[\s\S]*?(?:\*\/|$)/g, (m, quoted: string | undefined) =>
    quoted ?? m.replace(/[^\n]/g, ' '));
}

/** How many characters of whitespace and comments a statement's text starts with. */
function leadingNoise(text: string): number {
  const m = /^(?:\s+|--[^\n]*|\/\*[\s\S]*?\*\/)*/.exec(text);
  return m ? m[0].length : 0;
}

function lineAt(text: string, offset: number): number {
  let line = 1;
  for (let i = 0; i < offset && i < text.length; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

function messageOf(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

function now(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now();
}
