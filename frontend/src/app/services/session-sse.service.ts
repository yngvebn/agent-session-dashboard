import { Injectable, OnDestroy, inject, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface Session {
  sessionId: string;
  name: string;
  status: 'running' | 'idle' | 'closed' | 'crashed';
  startedAt: string;
  lastSeen: string;
  pid: number;
  workingDir: string;
  lastActivity: string | null;
  worktree: string | null;
  branch: string | null;
}

@Injectable({ providedIn: 'root' })
export class SessionSseService implements OnDestroy {
  private http = inject(HttpClient);

  private _sessions = signal<Session[]>([]);
  private _connected = signal(false);

  readonly sessions = this._sessions.asReadonly();
  readonly connected = this._connected.asReadonly();

  private eventSource: EventSource | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.loadInitialSessions();
  }

  private loadInitialSessions(): void {
    this.http.get<Session[]>('/api/sessions').subscribe({
      next: (sessions) => {
        this._sessions.set(sessions);
        this.connect();
      },
      error: () => {
        // Even if initial load fails, still try to connect SSE
        this.connect();
      },
    });
  }

  private connect(): void {
    this.closeEventSource();

    const es = new EventSource('/api/sessions/stream');
    this.eventSource = es;

    es.onopen = () => {
      this._connected.set(true);
    };

    es.addEventListener('session-update', (event: MessageEvent) => {
      try {
        const session: Session = JSON.parse(event.data);
        this.upsertSession(session);
      } catch {
        console.error('Failed to parse session-update event', event.data);
      }
    });

    es.addEventListener('session-closed', (event: MessageEvent) => {
      try {
        const { sessionId } = JSON.parse(event.data) as { sessionId: string };
        this._sessions.update((sessions) =>
          sessions.map((s) => (s.sessionId === sessionId ? { ...s, status: 'closed' } : s))
        );
      } catch {
        console.error('Failed to parse session-closed event', event.data);
      }
    });

    es.onerror = () => {
      this._connected.set(false);
      this.closeEventSource();
      this.scheduleRetry();
    };
  }

  deleteSession(sessionId: string): void {
    this.http.delete(`/api/sessions/${sessionId}`).subscribe({
      error: (e) => console.error('Delete failed', e),
    });
    this._sessions.update((sessions) => sessions.filter((s) => s.sessionId !== sessionId));
  }

  private upsertSession(session: Session): void {
    this._sessions.update((sessions) => {
      const idx = sessions.findIndex((s) => s.sessionId === session.sessionId);
      if (idx >= 0) {
        const updated = [...sessions];
        updated[idx] = session;
        return updated;
      }
      return [...sessions, session];
    });
  }

  private scheduleRetry(): void {
    if (this.retryTimer !== null) return;
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      this.connect();
    }, 3000);
  }

  private closeEventSource(): void {
    if (this.eventSource) {
      this.eventSource.close();
      this.eventSource = null;
    }
  }

  ngOnDestroy(): void {
    this.closeEventSource();
    if (this.retryTimer !== null) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
  }
}
