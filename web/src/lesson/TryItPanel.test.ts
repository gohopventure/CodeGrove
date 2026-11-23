import { describe, expect, it } from 'vitest';
import { blankIsNumber, findBlank } from './TryItPanel';

const server = { running: true, url: 'http://localhost:5000', endpoints: ['GET /pets/{id:int}', 'GET /hello/{name}', 'GET /books/{page:min(1)}'] };

describe('Try it path blanks', () => {
  it('finds the first {blank} and where it is', () => {
    expect(findBlank('/hello/{name}')).toEqual({ start: 7, end: 13, name: 'name' });
    expect(findBlank('/pets/{id}/toys/{toy}')).toEqual({ start: 6, end: 10, name: 'id' });
    expect(findBlank('/pets/3')).toBeNull();
    expect(findBlank('/files/{*rest}')?.name).toBe('rest');
    expect(findBlank('/pets/{}')?.name).toBe('value');
  });

  it('knows which blanks the server wants as numbers', () => {
    expect(blankIsNumber(server, 'id')).toBe(true);
    expect(blankIsNumber(server, 'page')).toBe(true);
    expect(blankIsNumber(server, 'name')).toBe(false);
    expect(blankIsNumber(server, 'i')).toBe(false);
    expect(blankIsNumber(null, 'id')).toBe(false);
    expect(blankIsNumber(server, 'a+b(')).toBe(false); // typed text is escaped, not a pattern
  });
});
