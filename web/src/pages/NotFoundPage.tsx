import { Link } from 'react-router-dom';

export function NotFoundPage() {
  return (
    <div className="card mx-auto max-w-md space-y-3 text-center">
      <h1 className="text-3xl font-bold">404: page not found</h1>
      <p>That's an HTTP status code you'll learn about in "How the Web Works"!</p>
      <Link to="/" className="btn-primary">Go home</Link>
    </div>
  );
}
