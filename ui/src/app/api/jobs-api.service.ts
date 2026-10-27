import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, NgZone, inject } from '@angular/core';
import { Observable, catchError, of, throwError } from 'rxjs';
import { AuditEntry, CreateJobRequest, Job, JobDocumentResult, JobStatus, Paged } from './models';

/**
 * Thin wrapper over the REST APIs. URLs are relative: nginx (Docker) or the dev-server proxy forwards
 * /api/jobs to the Job Service and /api/audit to the Audit Service, so the browser sees one origin (no CORS).
 */
@Injectable({ providedIn: 'root' })
export class JobsApi {
  private readonly http = inject(HttpClient);
  private readonly zone = inject(NgZone);

  list(status: JobStatus | null, page: number, pageSize: number): Observable<Paged<Job>> {
    let params = new HttpParams().set('page', page).set('pageSize', pageSize);
    if (status) {
      params = params.set('status', status);
    }
    return this.http.get<Paged<Job>>('/api/jobs', { params });
  }

  get(id: string): Observable<Job> {
    return this.http.get<Job>(`/api/jobs/${id}`);
  }

  create(request: CreateJobRequest): Observable<Job> {
    return this.http.post<Job>('/api/jobs', request);
  }

  cancel(id: string): Observable<Job> {
    return this.http.post<Job>(`/api/jobs/${id}/cancel`, null);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/jobs/${id}`);
  }

  results(id: string, page: number, pageSize: number): Observable<Paged<JobDocumentResult>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<Paged<JobDocumentResult>>(`/api/jobs/${id}/results`, { params });
  }

  /** The audit entry of a finished job, or null while it has not been recorded yet (404). */
  audit(id: string): Observable<AuditEntry | null> {
    return this.http.get<AuditEntry>(`/api/audit/${id}`).pipe(
      catchError((error: HttpErrorResponse) => (error.status === 404 ? of(null) : throwError(() => error))));
  }

  /**
   * Live status of one job (Server-Sent Events, `event: status`). The server sends a snapshot, then every change,
   * and closes the stream at a terminal state. The browser's EventSource reconnects by itself if the link drops.
   */
  events(id: string): Observable<Job> {
    return new Observable<Job>(subscriber => {
      const source = new EventSource(`/api/jobs/${id}/events`);
      source.addEventListener('status', event => {
        const job = JSON.parse((event as MessageEvent<string>).data) as Job;
        this.zone.run(() => subscriber.next(job));
        if (['Completed', 'Failed', 'Cancelled'].includes(job.status)) {
          source.close();
          this.zone.run(() => subscriber.complete());
        }
      });
      source.onerror = () => {
        // A closed stream (job deleted, or the service is down) ends the subscription; the page keeps the last state.
        if (source.readyState === EventSource.CLOSED) {
          this.zone.run(() => subscriber.complete());
        }
      };
      return () => source.close();
    });
  }
}
