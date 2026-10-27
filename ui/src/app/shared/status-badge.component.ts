import { Component, Input } from '@angular/core';
import { JobStatus } from '../api/models';

@Component({
  selector: 'app-status-badge',
  standalone: true,
  template: `<span class="badge badge-{{ status.toLowerCase() }}">{{ status }}</span>`
})
export class StatusBadgeComponent {
  @Input({ required: true }) status!: JobStatus;
}
