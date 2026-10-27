import { Routes } from '@angular/router';
import { JobCreateComponent } from './job-create/job-create.component';
import { JobDetailComponent } from './job-detail/job-detail.component';
import { JobsListComponent } from './jobs-list/jobs-list.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'jobs' },
  { path: 'jobs', component: JobsListComponent, title: 'Jobs · TA9' },
  { path: 'jobs/new', component: JobCreateComponent, title: 'New job · TA9' },
  { path: 'jobs/:id', component: JobDetailComponent, title: 'Job · TA9' },
  { path: '**', redirectTo: 'jobs' }
];
