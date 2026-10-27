import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { JobsApi } from './jobs-api.service';
import { AuditEntry } from './models';
import { aJob } from './testing';

describe('JobsApi', () => {
  let api: JobsApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(JobsApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists jobs with paging and an optional status filter', () => {
    api.list('Completed', 2, 20).subscribe();
    const request = http.expectOne(r => r.url === '/api/jobs');
    expect(request.request.params.get('status')).toBe('Completed');
    expect(request.request.params.get('page')).toBe('2');
    expect(request.request.params.get('pageSize')).toBe('20');
    request.flush({ items: [], page: 2, pageSize: 20, totalCount: 0 });
  });

  it('omits the status filter when none is chosen', () => {
    api.list(null, 1, 20).subscribe();
    const request = http.expectOne(r => r.url === '/api/jobs');
    expect(request.request.params.has('status')).toBeFalse();
    request.flush({ items: [], page: 1, pageSize: 20, totalCount: 0 });
  });

  it('creates a job with POST /api/jobs', () => {
    let created = '';
    api.create({ name: 'n', documents: [{ name: 'a.txt', content: 'x' }] }).subscribe(job => (created = job.id));
    const request = http.expectOne('/api/jobs');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.documents.length).toBe(1);
    request.flush(aJob({ id: 'new-id' }));
    expect(created).toBe('new-id');
  });

  it('cancels with POST /cancel and deletes with DELETE', () => {
    api.cancel('j1').subscribe();
    const cancel = http.expectOne('/api/jobs/j1/cancel');
    expect(cancel.request.method).toBe('POST');
    cancel.flush(aJob({ status: 'Cancelled' }), { status: 202, statusText: 'Accepted' });

    api.delete('j1').subscribe();
    const remove = http.expectOne('/api/jobs/j1');
    expect(remove.request.method).toBe('DELETE');
    remove.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('pages results', () => {
    api.results('j1', 3, 50).subscribe();
    const request = http.expectOne(r => r.url === '/api/jobs/j1/results');
    expect(request.request.params.get('page')).toBe('3');
    request.flush({ items: [], page: 3, pageSize: 50, totalCount: 0 });
  });

  it('returns null for an audit entry that is not recorded yet (404)', () => {
    let entry: AuditEntry | null | undefined;
    api.audit('j1').subscribe(value => (entry = value));
    http.expectOne('/api/audit/j1').flush({ title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    expect(entry).toBeNull();
  });

  it('propagates other audit errors', () => {
    let failure: HttpErrorResponse | undefined;
    api.audit('j1').subscribe({ error: error => (failure = error) });
    http.expectOne('/api/audit/j1').flush({}, { status: 503, statusText: 'Service Unavailable' });
    expect(failure?.status).toBe(503);
  });
});
