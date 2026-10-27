import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, Input, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { Subscription, filter, take, timer, switchMap } from 'rxjs';
import { JobsApi } from '../api/jobs-api.service';
import { AuditEntry, Job, JobDocumentResult, isTerminal } from '../api/models';
import { describeError } from '../api/problem';
import { ProgressBarComponent } from '../shared/progress-bar.component';
import { StatusBadgeComponent } from '../shared/status-badge.component';

/**
 * One job: live status over SSE, cancel / delete, paged results once Completed, and the audit entry
 * once the Job Service has published it (a few seconds after the job finishes).
 */
@Component({
  selector: 'app-job-detail',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink, ProgressBarComponent, StatusBadgeComponent],
  templateUrl: './job-detail.component.html'
})
export class JobDetailComponent implements OnInit {
  static readonly ResultsPageSize = 50;
  static readonly AuditPollMs = 2000;
  static readonly AuditPollAttempts = 15;

  /** Route parameter (component input binding). */
  @Input({ required: true }) id!: string;

  private readonly api = inject(JobsApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private auditPoll?: Subscription;

  readonly job = signal<Job | null>(null);
  readonly live = signal(false);
  readonly results = signal<JobDocumentResult[]>([]);
  readonly resultsTotal = signal(0);
  readonly resultsPage = signal(1);
  readonly audit = signal<AuditEntry | null>(null);
  readonly auditPending = signal(false);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  readonly isTerminal = isTerminal;

  ngOnInit(): void {
    this.api.get(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: job => this.apply(job),
      error: error => this.error.set(describeError(error))
    });
  }

  get resultsPageCount(): number {
    return Math.max(1, Math.ceil(this.resultsTotal() / JobDetailComponent.ResultsPageSize));
  }

  get canCancel(): boolean {
    const job = this.job();
    return job !== null && !isTerminal(job.status);
  }

  get canDelete(): boolean {
    const job = this.job();
    return job !== null && isTerminal(job.status);
  }

  cancel(): void {
    this.busy.set(true);
    this.api.cancel(this.id).subscribe({
      next: job => {
        this.busy.set(false);
        this.apply(job);
      },
      error: error => {
        this.busy.set(false);
        this.error.set(describeError(error));
      }
    });
  }

  delete(): void {
    if (!confirm('Delete this job and its results?')) {
      return;
    }
    this.busy.set(true);
    this.api.delete(this.id).subscribe({
      next: () => this.router.navigate(['/jobs']),
      error: error => {
        this.busy.set(false);
        this.error.set(describeError(error));
      }
    });
  }

  loadResults(page: number): void {
    this.api.results(this.id, page, JobDetailComponent.ResultsPageSize).subscribe({
      next: result => {
        this.results.set(result.items);
        this.resultsTotal.set(result.totalCount);
        this.resultsPage.set(result.page);
      },
      error: error => this.error.set(describeError(error))
    });
  }

  loadAudit(): void {
    this.auditPoll?.unsubscribe();
    this.auditPending.set(true);
    // The Job Service publishes summaries every few seconds; poll until the entry appears.
    this.auditPoll = timer(0, JobDetailComponent.AuditPollMs).pipe(
      take(JobDetailComponent.AuditPollAttempts),
      switchMap(() => this.api.audit(this.id)),
      filter((entry): entry is AuditEntry => entry !== null),
      take(1),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: entry => {
        this.audit.set(entry);
        this.auditPending.set(false);
      },
      error: error => {
        this.auditPending.set(false);
        this.error.set(describeError(error));
      },
      complete: () => this.auditPending.set(false)
    });
  }

  shortHash(hash: string | null): string {
    return hash ? `${hash.substring(0, 12)}…` : '—';
  }

  topWords(result: JobDocumentResult): string {
    return result.result?.topWords.map(w => `${w.word} (${w.count})`).join(', ') || '—';
  }

  /** Applies a new snapshot; follows the live stream while the job runs and loads the outcome when it ends. */
  private apply(job: Job): void {
    const wasTerminal = this.job() !== null && isTerminal(this.job()!.status);
    this.job.set(job);
    this.error.set(null);

    if (!isTerminal(job.status)) {
      this.followLive();
    } else if (!wasTerminal) {
      if (job.status === 'Completed') {
        this.loadResults(1);
      }
      this.loadAudit();
    }
  }

  private followLive(): void {
    if (this.live()) {
      return;
    }
    this.live.set(true);
    this.api.events(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: job => this.apply(job),
      complete: () => this.live.set(false),
      error: () => this.live.set(false)
    });
  }
}
