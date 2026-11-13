/** Provided by vite-plugins/reactRuntime.ts: React's development builds, ready for the module loader. */
declare module 'virtual:cg-react-runtime' {
  const runtime: string;
  export default runtime;
}

/** Provided by vite-plugins/angularRuntime.ts: Angular's packages, ready for the module loader. */
declare module 'virtual:cg-angular-runtime' {
  const runtime: string;
  export default runtime;
}

/** Provided by vite-plugins/angularRuntime.ts: Angular's .d.ts files, keyed by /node_modules/… path. */
declare module 'virtual:cg-angular-types' {
  const files: Record<string, string>;
  export default files;
}
