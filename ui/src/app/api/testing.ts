import { Job, JobStatus } from './models';

/** A job as the Job Service returns it, with overridable fields. */
export function aJob(overrides: Partial<Job> = {}): Job {
  return {
    id: '11111111-2222-3333-4444-555555555555',
    name: 'test job',
    status: 'Pending' as JobStatus,
    totalDocuments: 3,
    processedDocuments: 0,
    progressPercent: 0,
    summary: null,
    failureReason: null,
    createdAt: '2026-10-27T10:00:00Z',
    updatedAt: '2026-10-27T10:00:00Z',
    startedAt: null,
    completedAt: null,
    ...overrides
  };
}
