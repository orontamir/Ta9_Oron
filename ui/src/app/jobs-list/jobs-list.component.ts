import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription, interval } from 'rxjs';
import { JobsApi } from '../api/jobs-api.service';
import { JOB_STATUSES, Job, JobStatus } from '../api/models';
import { describeError } from '../api/problem';
import { ProgressBarComponent } from '../shared/progress-bar.component';
import { StatusBadgeComponent } from '../shared/status-badge.component';

/** GET /api/jobs: newest first, filter by status, paging, optional auto-refresh. */
@Component({
  selector: 'app-jobs-list',
  standalone: true,
  imports: [DatePipe, FormsModule, RouterLink, ProgressBarComponent, StatusBadgeComponent],
  templateUrl: './jobs-list.component.html'
})
export class JobsListComponent implements OnInit {
  static readonly PageSize = 20;
  static readonly RefreshMs = 3000;

  private readonly api = inject(JobsApi);
  private readonly destroyRef = inject(DestroyRef);
  private refresh?: Subscription;

  readonly statuses = JOB_STATUSES;
  readonly jobs = signal<Job[]>([]);
  readonly totalCount = signal(0);
  readonly page = signal(1);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  status: JobStatus | null = null;
  autoRefresh = true;

  ngOnInit(): void {
    this.load();
    this.setAutoRefresh(this.autoRefresh);
  }

  get pageCount(): number {
    return Math.max(1, Math.ceil(this.totalCount() / JobsListComponent.PageSize));
  }

  load(): void {
    this.loading.set(true);
    this.api.list(this.status, this.page(), JobsListComponent.PageSize)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: result => {
          this.jobs.set(result.items);
          this.totalCount.set(result.totalCount);
          this.error.set(null);
          this.loading.set(false);
        },
        error: error => {
          this.error.set(describeError(error));
          this.loading.set(false);
        }
      });
  }

  onFilterChange(): void {
    this.page.set(1);
    this.load();
  }

  goToPage(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pageCount));
    this.load();
  }

  setAutoRefresh(enabled: boolean): void {
    this.autoRefresh = enabled;
    this.refresh?.unsubscribe();
    if (enabled) {
      this.refresh = interval(JobsListComponent.RefreshMs)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe(() => this.load());
    }
  }
}
