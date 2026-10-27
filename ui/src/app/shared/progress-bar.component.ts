import { Component, Input } from '@angular/core';

@Component({
  selector: 'app-progress-bar',
  standalone: true,
  template: `
    <div class="progress" [attr.aria-valuenow]="percent" role="progressbar" aria-valuemin="0" aria-valuemax="100">
      <div class="progress-fill" [style.width.%]="percent"></div>
    </div>
    <span class="progress-label">{{ label }}</span>`
})
export class ProgressBarComponent {
  @Input({ required: true }) processed = 0;
  @Input({ required: true }) total = 0;

  get percent(): number {
    return this.total === 0 ? 0 : Math.round((100 * this.processed) / this.total);
  }

  get label(): string {
    return `${this.processed} / ${this.total}`;
  }
}
