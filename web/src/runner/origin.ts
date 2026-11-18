/**
 * Where learner code runs: runner.html (HTML/CSS/JS/React/TypeScript/Angular previews) and
 * csharp-runner/ (C#). Always a different origin from the app, so learner code can never reach
 * the app's cookies, storage or API. In development that's 127.0.0.1 vs localhost (see vite.config.ts).
 */
export const PREVIEW_ORIGIN: string = import.meta.env.VITE_PREVIEW_ORIGIN ?? 'http://127.0.0.1:5173';
