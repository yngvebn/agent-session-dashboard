import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface ClaudeIncident {
  id: string;
  title: string;
  status: 'Identified' | 'Investigating';
  link: string;
  publishedAt: string;
}

interface ClaudeStatusResponse {
  operational: boolean;
  incidents: ClaudeIncident[];
  checkedAt: string | null;
}

// The backend refreshes the feed every 5 minutes; polling it faster than this
// only re-reads the same snapshot.
const POLL_INTERVAL_MS = 60 * 1000;

@Injectable({ providedIn: 'root' })
export class ClaudeStatusService {
  private http = inject(HttpClient);

  private _incidents = signal<ClaudeIncident[]>([]);
  readonly incidents = this._incidents.asReadonly();
  readonly hasIncident = computed(() => this._incidents().length > 0);

  constructor() {
    this.check();
    setInterval(() => this.check(), POLL_INTERVAL_MS);
  }

  private check(): void {
    this.http.get<ClaudeStatusResponse>('/api/claude-status').subscribe({
      next: (res) => this._incidents.set(res.incidents ?? []),
      error: () => {},
    });
  }
}
