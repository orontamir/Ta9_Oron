import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header class="topbar">
      <a routerLink="/jobs" class="brand">TA9 · Document Processing</a>
      <nav>
        <a routerLink="/jobs" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Jobs</a>
        <a routerLink="/jobs/new" routerLinkActive="active">New job</a>
      </nav>
    </header>
    <main class="container">
      <router-outlet />
    </main>
    <footer class="footer muted">
      Job Service <a href="http://localhost:5001/swagger" target="_blank" rel="noopener">Swagger</a> ·
      Processor <a href="http://localhost:5002/swagger" target="_blank" rel="noopener">Swagger</a> ·
      Audit <a href="http://localhost:5003/swagger" target="_blank" rel="noopener">Swagger</a>
    </footer>`
})
export class AppComponent {}
