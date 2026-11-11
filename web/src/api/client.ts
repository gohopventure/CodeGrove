/**
 * Thin fetch wrapper. Auth is an HttpOnly cookie, so there's no token handling here.
 * Every request sends JSON (the API rejects state-changing requests that don't, as a CSRF guard).
 */

export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly problem: ProblemDetails;

  constructor(status: number, problem: ProblemDetails) {
    super(problem.title ?? `Request failed (${status})`);
    this.status = status;
    this.problem = problem;
  }

  /** First error message for a form field, matched case-insensitively ("UserName" or "userName"). */
  fieldError(field: string): string | undefined {
    const entry = Object.entries(this.problem.errors ?? {}).find(([k]) => k.toLowerCase() === field.toLowerCase());
    return entry?.[1]?.[0];
  }
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE';

async function request<T>(method: Method, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: { Accept: 'application/json', ...(method !== 'GET' ? { 'Content-Type': 'application/json' } : {}) },
    body: method !== 'GET' ? JSON.stringify(body ?? {}) : undefined,
  });

  const text = await res.text();
  const data = text ? JSON.parse(text) : undefined;

  // 423 from the heartbeat still carries a useful body; callers check status via ApiError.
  if (!res.ok) throw new ApiError(res.status, (data as ProblemDetails) ?? { title: res.statusText });
  return data as T;
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T = void>(url: string, body?: unknown) => request<T>('POST', url, body),
  put: <T = void>(url: string, body: unknown) => request<T>('PUT', url, body),
  del: <T = void>(url: string) => request<T>('DELETE', url),
};

/** The learner's local calendar date (YYYY-MM-DD), so "today" means their day, not UTC's. */
export function localDate(d = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
