import { HttpErrorResponse } from '@angular/common/http';
import { describeError } from './problem';

describe('describeError', () => {
  it('uses the ProblemDetails title and the field errors of a 400', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { title: 'One or more validation errors occurred.', errors: { documents: ['At least one document is required.'] } }
    });

    expect(describeError(error)).toBe('400 — One or more validation errors occurred. · documents: At least one document is required.');
  });

  it('prefers the detail of a ProblemDetails', () => {
    const error = new HttpErrorResponse({ status: 503, error: { title: 'Storage unavailable', detail: 'Retry later.' } });

    expect(describeError(error)).toBe('503 — Retry later.');
  });

  it('explains an unreachable service', () => {
    expect(describeError(new HttpErrorResponse({ status: 0 }))).toContain('cannot be reached');
  });
});
