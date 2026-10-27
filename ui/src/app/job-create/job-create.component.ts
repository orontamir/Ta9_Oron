import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { JobsApi } from '../api/jobs-api.service';
import { LIMITS, NewDocument } from '../api/models';
import { describeError } from '../api/problem';

/** POST /api/jobs: a name and 1–1,000 text documents, typed in, loaded from .txt files, or generated. */
@Component({
  selector: 'app-job-create',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './job-create.component.html'
})
export class JobCreateComponent {
  private readonly api = inject(JobsApi);
  private readonly router = inject(Router);

  readonly limits = LIMITS;
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  name = '';
  documents: NewDocument[] = [{ name: 'doc-1.txt', content: '' }];
  sampleCount = 100;

  addDocument(): void {
    this.documents = [...this.documents, { name: `doc-${this.documents.length + 1}.txt`, content: '' }];
  }

  removeDocument(index: number): void {
    this.documents = this.documents.filter((_, i) => i !== index);
  }

  /** Reads the chosen .txt files and appends them as documents (an empty first row is replaced). */
  async addFiles(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    const loaded = await Promise.all(files.map(async file => ({ name: file.name, content: await file.text() })));
    const keep = this.documents.filter(d => d.content.trim().length > 0);
    this.documents = [...keep, ...loaded];
    input.value = '';
  }

  /** Quick way to try the concurrency requirement (up to 1,000 documents per job). */
  generateSample(): void {
    const count = Math.min(Math.max(1, Math.floor(this.sampleCount)), LIMITS.maxDocuments);
    const words = ['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta', 'eta', 'theta'];
    this.documents = Array.from({ length: count }, (_, i) => ({
      name: `sample-${i + 1}.txt`,
      content: Array.from({ length: 30 }, (_, w) => words[(i + w * 3) % words.length]).join(' ')
    }));
    if (!this.name) {
      this.name = `sample of ${count} documents`;
    }
  }

  /** Client-side checks only for a quick hint; the server validates everything again. */
  get problems(): string[] {
    const problems: string[] = [];
    if (this.documents.length === 0) {
      problems.push('Add at least one document.');
    }
    if (this.documents.length > LIMITS.maxDocuments) {
      problems.push(`At most ${LIMITS.maxDocuments} documents per job.`);
    }
    if (this.documents.some(d => !d.name.trim())) {
      problems.push('Every document needs a name.');
    }
    if (this.documents.some(d => new TextEncoder().encode(d.content).length > LIMITS.maxDocumentBytes)) {
      problems.push('A document is larger than 100 KB.');
    }
    if (this.name.length > LIMITS.maxNameLength) {
      problems.push(`The job name is longer than ${LIMITS.maxNameLength} characters.`);
    }
    return problems;
  }

  submit(): void {
    if (this.problems.length > 0 || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.error.set(null);
    this.api.create({ name: this.name.trim() || null, documents: this.documents }).subscribe({
      next: job => this.router.navigate(['/jobs', job.id]),
      error: error => {
        this.error.set(describeError(error));
        this.submitting.set(false);
      }
    });
  }
}
