import { HttpErrorResponse } from '@angular/common/http';

/** RFC 7807 ProblemDetails / ValidationProblemDetails as returned by the services. */
interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

/** Turns any HTTP failure into one readable line, using the server's ProblemDetails when present. */
export function describeError(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return error instanceof Error ? error.message : 'Unexpected error';
  }

  if (error.status === 0) {
    return 'The service cannot be reached. Is the stack running (docker compose up)?';
  }

  const problem = (typeof error.error === 'object' && error.error !== null ? error.error : {}) as Problem;
  const fieldErrors = problem.errors
    ? Object.entries(problem.errors).map(([field, messages]) => `${field}: ${messages.join(' ')}`)
    : [];
  const headline = problem.detail ?? problem.title ?? error.statusText ?? 'Request failed';
  return [`${error.status} — ${headline}`, ...fieldErrors].join(' · ');
}
