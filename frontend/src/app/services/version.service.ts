import { Injectable, signal, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

const REPO = 'yngvebn/agent-session-dashboard';
const POLL_INTERVAL_MS = 30 * 60 * 1000; // 30 minutes

@Injectable({ providedIn: 'root' })
export class VersionService {
  private http = inject(HttpClient);

  private _updateAvailable = signal(false);
  private _latestSha = signal<string | null>(null);
  readonly updateAvailable = this._updateAvailable.asReadonly();
  readonly latestSha = this._latestSha.asReadonly();

  private dismissed = false;

  constructor() {
    this.check();
    setInterval(() => this.check(), POLL_INTERVAL_MS);
  }

  dismiss(): void {
    this.dismissed = true;
    this._updateAvailable.set(false);
  }

  private check(): void {
    this.http.get<{ sha: string }>('/api/version').subscribe({
      next: ({ sha: localSha }) => {
        if (localSha === 'unknown') return;
        this.http
          .get<{ sha: string }>(`https://api.github.com/repos/${REPO}/commits/main`, {
            headers: { Accept: 'application/vnd.github.sha' },
            responseType: 'text' as 'json',
          })
          .subscribe({
            next: (remoteSha: unknown) => {
              const remote = (remoteSha as string).trim();
              this._latestSha.set(remote);
              if (!this.dismissed && remote && !remote.startsWith(localSha) && !localSha.startsWith(remote)) {
                this._updateAvailable.set(true);
              }
            },
            error: () => {},
          });
      },
      error: () => {},
    });
  }
}
