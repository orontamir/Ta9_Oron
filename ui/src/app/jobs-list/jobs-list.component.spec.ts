import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { JobsApi } from '../api/jobs-api.service';
import { aJob } from '../api/testing';
import { JobsListComponent } from './jobs-list.component';

describe('JobsListComponent', () => {
  let fixture: ComponentFixture<JobsListComponent>;
  let api: jasmine.SpyObj<JobsApi>;

  beforeEach(() => {
    api = jasmine.createSpyObj<JobsApi>('JobsApi', ['list']);
    api.list.and.returnValue(of({
      items: [aJob({ name: 'first', status: 'Completed', processedDocuments: 3 }), aJob({ id: 'second-id', name: 'second', status: 'Processing' })],
      page: 1, pageSize: 20, totalCount: 2
    }));
    TestBed.configureTestingModule({
      imports: [JobsListComponent],
      providers: [provideRouter([]), { provide: JobsApi, useValue: api }]
    });
    fixture = TestBed.createComponent(JobsListComponent);
    fixture.componentInstance.autoRefresh = false;
    fixture.detectChanges();
  });

  afterEach(() => fixture.destroy());

  it('shows one row per job with its status', () => {
    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('first');
    expect(rows[0].querySelector('.badge').textContent.trim()).toBe('Completed');
    expect(fixture.nativeElement.textContent).toContain('Page 1 of 1 · 2 jobs');
  });

  it('reloads from page 1 with the chosen status', () => {
    const list = fixture.componentInstance;
    list.status = 'Failed';

    list.onFilterChange();

    expect(api.list).toHaveBeenCalledWith('Failed', 1, JobsListComponent.PageSize);
  });
});
