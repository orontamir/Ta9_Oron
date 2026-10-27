/** Types of the Job Service and Audit Service REST contracts (JSON is camelCase, enums are strings). */

export type JobStatus = 'Pending' | 'Queued' | 'Processing' | 'Completed' | 'Failed' | 'Cancelled';

export const JOB_STATUSES: JobStatus[] = ['Pending', 'Queued', 'Processing', 'Completed', 'Failed', 'Cancelled'];

export const TERMINAL_STATUSES: JobStatus[] = ['Completed', 'Failed', 'Cancelled'];

export function isTerminal(status: JobStatus): boolean {
  return TERMINAL_STATUSES.includes(status);
}

export interface ProcessingSummary {
  totalDocuments: number;
  succeeded: number;
  failed: number;
  totalWords: number;
  durationMs: number;
}

export interface Job {
  id: string;
  name: string | null;
  status: JobStatus;
  totalDocuments: number;
  processedDocuments: number;
  progressPercent: number;
  summary: ProcessingSummary | null;
  failureReason: string | null;
  createdAt: string;
  updatedAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface WordCount {
  word: string;
  count: number;
}

export interface DocumentResult {
  index: number;
  name: string;
  status: 'Succeeded' | 'Failed';
  wordCount: number;
  uniqueWordCount: number;
  topWords: WordCount[];
  sha256: string | null;
  characterCount: number;
  lineCount: number;
  averageWordLength: number;
  longestWord: string | null;
  error: string | null;
}

export interface JobDocumentResult {
  index: number;
  name: string;
  result: DocumentResult | null;
}

export interface NewDocument {
  name: string;
  content: string;
}

export interface CreateJobRequest {
  name: string | null;
  documents: NewDocument[];
}

export interface AuditEntry {
  jobId: string;
  name: string | null;
  status: string;
  totalDocuments: number;
  succeededDocuments: number;
  failedDocuments: number;
  totalWords: number;
  failureReason: string | null;
  createdAt: string;
  completedAt: string;
  recordedAt: string;
}

/** Limits enforced by the Job Service (kept here only to warn early; the server is the authority). */
export const LIMITS = {
  maxDocuments: 1000,
  maxDocumentBytes: 100 * 1024,
  maxNameLength: 200
};
