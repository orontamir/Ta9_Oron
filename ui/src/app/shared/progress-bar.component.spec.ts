import { ProgressBarComponent } from './progress-bar.component';

describe('ProgressBarComponent', () => {
  it('computes a rounded percentage and a label', () => {
    const bar = new ProgressBarComponent();
    bar.processed = 1;
    bar.total = 3;

    expect(bar.percent).toBe(33);
    expect(bar.label).toBe('1 / 3');
  });

  it('shows 0% for an empty job', () => {
    const bar = new ProgressBarComponent();

    expect(bar.percent).toBe(0);
  });
});
