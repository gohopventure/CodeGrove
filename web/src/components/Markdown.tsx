import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

/**
 * Renders authored lesson markdown. Raw HTML in markdown is NOT rendered (no rehype-raw), so
 * content can show code like `<h1>` safely and an authoring mistake can't inject markup.
 */
export function Markdown({ children, className = '' }: { children: string; className?: string }) {
  return (
    <div className={`prose-lesson ${className}`}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          // Lesson h2s sit under the page's h1; keep the outline valid.
          h1: ({ children }) => <h2>{children}</h2>,
          a: ({ href, children }) => (
            <a href={href} className="link" target="_blank" rel="noreferrer noopener">
              {children}
              <span className="sr-only"> (opens in a new tab)</span>
            </a>
          ),
          pre: ({ children }) => <pre tabIndex={0}>{children}</pre>,
          // Wide tables scroll inside their own box instead of pushing past the page edge.
          // tabIndex lets keyboard users focus the box and scroll it with arrow keys.
          table: ({ children }) => (
            <div className="table-wrap" tabIndex={0} role="region" aria-label="Table">
              <table>{children}</table>
            </div>
          ),
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}
