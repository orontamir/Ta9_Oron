import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { NEVER, of } from 'rxjs';
import { JobsApi } from '../api/jobs-api.service';
import { Job } from '../api/models';
import { aJob } from '../api/testing';
import { JobDetailComponent } from './job-detail.component';

describe('JobDetailComponent', () => {
  let api: jasmine.SpyObj<JobsApi>;

  function render(job: Job): ComponentFixture<JobDetailComponent> {
    api.get.and.returnValue(of(job));
    const fixture = TestBed.createComponent(JobDetailComponent);
    fixture.componentInstance.id = job.id;
    fixture.detectChanges();
    return fixture;
  }

  beforeEach(() => {
    api = jasmine.createSpyObj<JobsApi>('JobsApi', ['get', 'events', 'results', 'audit', 'cancel', 'delete']);
    api.events.and.returnValue(NEVER);
    api.audit.and.returnValue(of(null));
    TestBed.configureTestingModule({
      imports: [JobDetailComponent],
      providers: [provideRouter([]), { provide: JobsApi, useValue: api }]
    });
  });

  it('follows a running job live and offers cancel, not delete', () => {
    const fixture = render(aJob({ status: 'Processing', processedDocuments: 1 }));

    expect(api.events).toHaveBeenCalled();
    expect(fixture.componentInstance.canCancel).toBeTrue();
    expect(fixture.componentInstance.canDelete).toBeFalse();
    expect(fixture.nativeElement.textContent).toContain('live');
  });

  it('shows the results and the audit entry of a completed job', fakeAsync(() => {
    api.results.and.returnValue(of({
      items: [{
        index: 0, name: 'a.txt', result: {
          index: 0, name: 'a.txt', status: 'Succeeded', wordCount: 3, uniqueWordCount: 2,
          topWords: [{ word: 'hello', count: 2 }, { word: 'world', count: 1 }], sha256: 'abcdef0123456789abcdef',
          characterCount: 17, lineCount: 1, averageWordLength: 5, longestWord: 'hello', error: null
        }
      }],
      page: 1, pageSize: 50, totalCount: 1
    }));
    api.audit.and.returnValue(of({
      jobId: 'x', name: 'test job', status: 'Completed', totalDocuments: 1, succeededDocuments: 1, failedDocuments: 0,
      totalWords: 3, failureReason: null, createdAt: '', completedAt: '', recordedAt: '2026-10-27T10:00:05Z'
    }));

    const fixture = render(aJob({ status: 'Completed', processedDocuments: 1, totalDocuments: 1 }));
    tick(0); // the audit entry is polled on a timer
    fixture.detectChanges();

    expect(api.events).not.toHaveBeenCalled();
    const text: string = fixture.nativeElement.textContent;
    expect(text).toContain('hello (2), world (1)');
    expect(text).toContain('abcdef012345…');
    expect(text).toContain('Audit entry');
    expect(text).toContain('1 succeeded · 0 failed of 1');
    expect(fixture.componentInstance.canDelete).toBeTrue();
    discardPeriodicTasks();
  }));

  it('shows the failure reason of a failed job', fakeAsync(() => {
    const fixture = render(aJob({ status: 'Failed', failureReason: 'Processor unreachable after 5 attempts' }));
    tick(0);

    expect(fixture.nativeElement.textContent).toContain('Processor unreachable after 5 attempts');
    expect(api.results).not.toHaveBeenCalled();
    discardPeriodicTasks();
  }));
});
