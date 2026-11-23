import { beforeAll, describe, expect, it } from 'vitest';
import type { SqlJsStatic } from 'sql.js';
import { friendlyError, runSql, stripComments, MAX_ROWS } from './sqlEngine';
import { loadSqlJs } from './sqlNode';
import type { TestSpec } from './types';

const SETUP = `
CREATE TABLE pets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, kind TEXT, age INTEGER);
INSERT INTO pets VALUES (1, 'Juni', 'fox', 3), (2, 'Pip', 'owl', 1), (3, 'Moss', 'frog', 2);
CREATE TABLE toys (id INTEGER PRIMARY KEY, pet_id INTEGER REFERENCES pets(id), toy TEXT);
INSERT INTO toys VALUES (1, 1, 'ball');
`;

let SQL: SqlJsStatic;
beforeAll(async () => {
  SQL = await loadSqlJs();
}, 60_000);

const test = (id: number, kind: string, spec: Record<string, unknown>): TestSpec => ({ id, label: kind, kind: kind as TestSpec['kind'], spec, failMessage: 'Nope.' });
const run = (query: string, tests: TestSpec[] = [], files?: Record<string, string>) =>
  runSql(SQL, { setup: SETUP, files: files ?? { 'query.sql': query }, tests });

describe('running SQL', () => {
  it('returns result sets with columns, rows and the line they start on', () => {
    const r = run('-- all the pets\n\nSELECT name, age FROM pets ORDER BY age;\nSELECT COUNT(*) AS n FROM pets;');
    expect(r.error).toBeNull();
    expect(r.resultSets).toHaveLength(2);
    expect(r.resultSets[0]).toMatchObject({ file: 'query.sql', line: 3, columns: ['name', 'age'], rows: [['Pip', 1], ['Moss', 2], ['Juni', 3]] });
    expect(r.resultSets[1]).toMatchObject({ line: 4, rows: [[3]] });
  });

  it('says what statements without rows did', () => {
    const r = run("INSERT INTO pets (name) VALUES ('Fern'), ('Bo');\nUPDATE pets SET age = 4 WHERE name = 'Juni';\nDELETE FROM toys;\nCREATE TABLE snacks (id INTEGER);");
    expect(r.console.map((l) => l.text)).toEqual([
      'INSERT added 2 rows.', 'UPDATE changed 1 row.', 'DELETE removed 1 row.', 'CREATE TABLE made the table snacks.',
    ]);
    expect(r.tables.map((t) => t.name)).toEqual(['pets', 'toys', 'snacks']);
    expect(r.tables[0]!.rowCount).toBe(5);
    expect(r.tables[0]!.columns[0]).toEqual({ name: 'id', type: 'INTEGER', primaryKey: true, notNull: false });
  });

  it('reports a syntax error on the line where it happens, in plain words', () => {
    const r = run('SELECT name\nFROM pets\nWHER age > 1;');
    expect(r.error).toMatchObject({ line: 3, message: 'near "age": syntax error' });
    expect(r.console[0]!.level).toBe('error');
    expect(r.console[0]!.text).toMatch(/^Error in query.sql, line 3: SQL got confused near "age"/);
  });

  it('stops at the first error but keeps what ran before it', () => {
    const r = run("SELECT * FROM pets;\n\nSELECT nme FROM pets;\nSELECT 1;");
    expect(r.resultSets).toHaveLength(1);
    expect(r.error).toMatchObject({ line: 3, message: 'no such column: nme' });
    expect(r.error?.friendly).toContain("no column called nme");
  });

  it('gives a runtime error the line of its statement', () => {
    const r = run("SELECT 1;\nINSERT INTO pets (id, name)\n  VALUES (1, 'Again');");
    expect(r.error).toMatchObject({ line: 2, message: 'UNIQUE constraint failed: pets.id' });
  });

  it('enforces foreign keys, like SQL Server', () => {
    const r = run('INSERT INTO toys (pet_id, toy) VALUES (99, \'kite\');');
    expect(r.error?.message).toBe('FOREIGN KEY constraint failed');
  });

  it('starts every run from the setup again', () => {
    run('DELETE FROM toys; DROP TABLE toys;');
    expect(run('SELECT COUNT(*) FROM toys;').resultSets[0]!.rows).toEqual([[1]]);
  });

  it('runs .sql files in order and skips dotfiles', () => {
    const r = run('', [], { '.setup.sql': 'DROP TABLE pets;', 'a.sql': "INSERT INTO pets (name) VALUES ('Fern');", 'b.sql': 'SELECT COUNT(*) FROM pets;' });
    expect(r.error).toBeNull();
    expect(r.resultSets[0]).toMatchObject({ file: 'b.sql', rows: [[4]] });
  });

  it('caps huge results instead of hanging', () => {
    const r = run('WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n) SELECT i FROM n;');
    expect(r.resultSets[0]!.rows).toHaveLength(MAX_ROWS);
    expect(r.resultSets[0]!.truncated).toBe(true);
  });

  it('says so when there is nothing to run', () => {
    expect(run('-- write your query here\n').console[0]!.text).toMatch(/nothing to run/);
  });

  it('blames a broken setup on the challenge, not the learner', () => {
    const r = runSql(SQL, { setup: 'CREATE TABLE (', files: { 'query.sql': 'SELECT 1;' }, tests: [test(1, 'noErrors', {})] });
    expect(r.setupOk).toBe(false);
    expect(r.results[0]!.message).toMatch(/starting database didn't build/);
  });
});

describe('SQL tests', () => {
  it('sqlResultEquals: any order by default, columns case-insensitive', () => {
    const t = test(1, 'sqlResultEquals', { columns: ['NAME'], rows: [['Juni'], ['Moss']] });
    expect(run('SELECT name FROM pets WHERE age > 1;', [t]).results[0]!.passed).toBe(true);
    expect(run('SELECT name FROM pets;', [t]).results[0]!.message).toBe('Nope. (Your query returned 3 rows; it should return 2.)');
    expect(run('SELECT kind FROM pets WHERE age > 1;', [t]).results[0]!.message).toBe('Nope. (Column 1 should be called NAME. Your columns were: kind.)');
    expect(run('SELECT name, age FROM pets;', [t]).results[0]!.message).toMatch(/should have 1 column \(NAME\)/);
    expect(run("SELECT name FROM pets WHERE name <> 'Moss';", [t]).results[0]!.message).toBe("Nope. (This row shouldn't be there: 'Pip'.)");
  });

  it('sqlResultEquals: ordered', () => {
    const t = test(1, 'sqlResultEquals', { rows: [['Pip'], ['Moss'], ['Juni']], ordered: true });
    expect(run('SELECT name FROM pets ORDER BY age;', [t]).results[0]!.passed).toBe(true);
    expect(run('SELECT name FROM pets;', [t]).results[0]!.message).toBe('Nope. (The right rows are there, but in a different order.)');
  });

  it('sqlResultEquals: sortedBy allows ties in any order', () => {
    const tie = "INSERT INTO pets (name, kind, age) VALUES ('Fern', 'cat', 3);\n";
    const t = test(1, 'sqlResultEquals', { rows: [['Juni', 3], ['Fern', 3], ['Moss', 2], ['Pip', 1]], sortedBy: ['age desc'] });
    expect(run(tie + 'SELECT name, age FROM pets ORDER BY age DESC, name;', [t]).results[0]!.passed).toBe(true);
    expect(run(tie + 'SELECT name, age FROM pets ORDER BY age DESC, name DESC;', [t]).results[0]!.passed).toBe(true);
    expect(run(tie + 'SELECT name, age FROM pets ORDER BY age;', [t]).results[0]!.message)
      .toBe("Nope. (The right rows are there, but they aren't sorted by age (biggest first). Rows 1 and 2 are the wrong way round.)");
    const two = test(2, 'sqlResultEquals', { rows: [['Juni', 3], ['Fern', 3]], sortedBy: ['age', 'name'] });
    expect(run(tie + "SELECT name, age FROM pets WHERE age = 3 ORDER BY name;", [two]).results[0]!.passed).toBe(true);
    expect(run(tie + "SELECT name, age FROM pets WHERE age = 3 ORDER BY name DESC;", [two]).results[0]!.message).toMatch(/sorted by age \(smallest first\), then name \(A to Z\)\. Rows 1 and 2/);
    expect(run(tie + "SELECT name FROM pets WHERE age = 3;", [test(3, 'sqlResultEquals', { rows: [['Juni'], ['Fern']], sortedBy: ['age'] })]).results[0]!.message)
      .toMatch(/needs a column called age/);
  });

  it('sqlResultEquals: numbers within a tolerance, nulls, and which result set', () => {
    expect(run('SELECT AVG(age) FROM pets;', [test(1, 'sqlResultEquals', { rows: [[2.0]] })]).results[0]!.passed).toBe(true);
    expect(run('SELECT NULL;', [test(1, 'sqlResultEquals', { rows: [[null]] })]).results[0]!.passed).toBe(true);
    const first = test(1, 'sqlResultEquals', { result: 0, rows: [[3]] });
    expect(run('SELECT COUNT(*) FROM pets; SELECT 9;', [first]).results[0]!.passed).toBe(true);
    expect(run('INSERT INTO toys (toy) VALUES (\'x\');', [first]).results[0]!.message).toMatch(/didn't return any rows/);
    const second = test(2, 'sqlResultEquals', { result: 1, rows: [[1]] });
    expect(run('SELECT 1;', [second]).results[0]!.message).toBe('Nope. (Your code gave only 1 result, and this check looks at result 2. Write one query for each step.)');
  });

  it('sqlRowCount', () => {
    expect(run('SELECT * FROM pets;', [test(1, 'sqlRowCount', { equals: 3 })]).results[0]!.passed).toBe(true);
    expect(run('SELECT * FROM pets LIMIT 1;', [test(1, 'sqlRowCount', { min: 2 })]).results[0]!.message).toBe('Nope. (Your result has 1 row.)');
  });

  it('sqlCheckQuery: a value or rows, against the database after the run', () => {
    const count = test(1, 'sqlCheckQuery', { query: 'SELECT COUNT(*) FROM pets', equals: 4 });
    expect(run("INSERT INTO pets (name) VALUES ('Fern');", [count]).results[0]!.passed).toBe(true);
    expect(run('SELECT 1;', [count]).results[0]!.message).toBe('Nope. (It was 3.)');
    const rows = test(2, 'sqlCheckQuery', { query: "SELECT age FROM pets WHERE name = 'Juni'", equals: [[4]] });
    expect(run("UPDATE pets SET age = 4 WHERE name = 'Juni';", [rows]).results[0]!.passed).toBe(true);
    const fern = test(4, 'sqlCheckQuery', { query: "SELECT name FROM pets WHERE name = 'Fern'", equals: [['Fern']] });
    expect(run('SELECT 1;', [fern]).results[0]!.message).toBe("Nope. (The check didn't find it in the table.)");
    const all = test(5, 'sqlCheckQuery', { query: 'SELECT name FROM pets', equals: [['Juni'], ['Pip']] });
    expect(run('SELECT 1;', [all]).results[0]!.message).toBe('Nope. (The check found 3 rows in the table; it should find 2.)');
    const missing = test(3, 'sqlCheckQuery', { query: 'SELECT COUNT(*) FROM snacks', equals: 0 });
    expect(run('SELECT 1;', [missing]).results[0]!.message).toMatch(/While checking: There's no table called snacks/);
  });

  it('noErrors, sqlError and sourceMatches', () => {
    expect(run('SELECT 1;', [test(1, 'noErrors', {})]).results[0]!.passed).toBe(true);
    expect(run('SELEC 1;', [test(1, 'noErrors', {})]).results[0]!.message).toMatch(/^Nope\. \(query\.sql, line 1: SQL got confused/);
    expect(run('SELECT nope FROM pets;', [test(1, 'sqlError', { contains: 'no such column' })]).results[0]!.passed).toBe(true);
    const where = test(1, 'sourceMatches', { pattern: '\\bwhere\\b' });
    expect(run('SELECT * FROM pets WHERE age > 1;', [where]).results[0]!.passed).toBe(true);
    expect(run('-- WHERE goes here\nSELECT * FROM pets;', [where]).results[0]!.passed).toBe(false);
    const noStar = test(2, 'sourceMatches', { pattern: '\\*', max: 0 });
    expect(run("SELECT '*' FROM pets;", [noStar]).results[0]!.passed).toBe(false); // text in quotes still counts as code
    expect(run('SELECT name FROM pets;', [noStar]).results[0]!.passed).toBe(true);
  });
});

describe('helpers', () => {
  it('strips comments but not quoted text', () => {
    expect(stripComments("SELECT '--not' -- yes\n/* gone */ FROM t")).toBe("SELECT '--not'       \n           FROM t");
  });

  it('blames the line the error is about, not just where the statement starts', () => {
    expect(run('SELECT kind, SUM(age) AS total\nFROM pets\nWHERE SUM(age) > 3\nGROUP BY kind;').error?.line).toBe(3);
    expect(run('SELECT name,\n       kind,\n       agee\nFROM pets;').error?.line).toBe(3);
    expect(run('SELECT name\nFROM pet\nWHERE age > 1;').error?.line).toBe(2);
    expect(run('SELECT p.name\nFROM pets AS q;').error?.line).toBe(1); // p.name: the word is on line 1
  });

  it('explains an aggregate in WHERE, however SQLite words it', () => {
    expect(run('SELECT kind FROM pets WHERE SUM(age) > 3;').error?.friendly)
      .toBe("SUM() works on groups of rows, so it can't go in WHERE, which checks one row at a time. To filter groups, use HAVING after GROUP BY.");
    expect(run('SELECT kind FROM pets WHERE COUNT(*) > 1 GROUP BY kind;').error?.friendly).toMatch(/^COUNT\(\) works on groups/);
  });

  it('has plain words for the errors beginners hit most', () => {
    for (const message of [
      'near "FORM": syntax error', 'incomplete input', 'no such table: pet', 'no such column: nme',
      'ambiguous column name: id', 'table pets has 4 columns but 2 values were supplied', '1 values for 2 columns',
      'NOT NULL constraint failed: pets.name', 'UNIQUE constraint failed: pets.id', 'FOREIGN KEY constraint failed',
      'misuse of aggregate: COUNT()', 'a GROUP BY clause is required before HAVING', 'unrecognized token: "\'Juni"',
    ]) {
      expect(friendlyError(message), message).not.toBe('');
    }
  });
});
