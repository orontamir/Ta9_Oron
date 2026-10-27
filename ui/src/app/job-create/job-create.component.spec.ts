import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { JobsApi } from '../api/jobs-api.service';
import { aJob } from '../api/testing';
import { JobCreateComponent } from './job-create.component';

describe('JobCreateComponent', () => {
  let fixture: ComponentFixture<JobCreateComponent>;
  let component: JobCreateComponent;
  let api: jasmine.SpyObj<JobsApi>;
  let router: Router;

  beforeEach(() => {
    api = jasmine.createSpyObj<JobsApi>('JobsApi', ['create']);
    TestBed.configureTestingModule({
      imports: [JobCreateComponent],
      providers: [provideRouter([]), { provide: JobsApi, useValue: api }]
    });
    fixture = TestBed.createComponent(JobCreateComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  });

  it('submits the name and documents and opens the new job', () => {
    api.create.and.returnValue(of(aJob({ id: 'created-id' })));
    component.name = '  my job  ';
    component.documents = [{ name: 'a.txt', content: 'hello world' }];

    component.submit();

    expect(api.create).toHaveBeenCalledWith({ name: 'my job', documents: [{ name: 'a.txt', content: 'hello world' }] });
    expect(router.navigate).toHaveBeenCalledWith(['/jobs', 'created-id']);
  });

  it('does not submit while there are problems', () => {
    component.documents = [];

    component.submit();

    expect(component.problems).toContain('Add at least one document.');
    expect(api.create).not.toHaveBeenCalled();
  });

  it('generates up to 1,000 sample documents', () => {
    component.sampleCount = 5000;

    component.generateSample();

    expect(component.documents.length).toBe(1000);
    expect(component.problems).toEqual([]);
  });

  it('shows the server validation message', () => {
    api.create.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { title: 'Invalid', errors: { name: ['Too long.'] } } })));
    component.documents = [{ name: 'a.txt', content: '' }];

    component.submit();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.alert').textContent).toContain('name: Too long.');
  });
});
