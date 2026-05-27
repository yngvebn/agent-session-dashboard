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
  tokensIn: number;
  tokensOut: number;
}

export interface SessionEvent {
  id: number;
  sessionId: string;
  timestamp: string;
  message: string;
}

@Injectable({ providedIn: 'root' })
export class SessionSseService implements OnDestroy {
  private http = inject(HttpClient);

  private _sessions = signal<Session[]>([]);
  private _connected = signal(false);
  private _notificationPermission = signal<NotificationPermission>(
    'Notification' in window ? Notification.permission : 'denied'
  );
  private _sessionEvents = signal<Map<string, SessionEvent[]>>(new Map());

  readonly sessions = this._sessions.asReadonly();
  readonly connected = this._connected.asReadonly();
  readonly notificationPermission = this._notificationPermission.asReadonly();
  readonly sessionEvents = this._sessionEvents.asReadonly();

  private eventSource: EventSource | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private audioCtx: AudioContext | null = null;
  private idleTimers = new Map<string, ReturnType<typeof setTimeout>>();

  private _soundEnabled = signal(localStorage.getItem('soundEnabled') !== 'false');
  readonly soundEnabled = this._soundEnabled.asReadonly();

  toggleSound(): void {
    const next = !this._soundEnabled();
    this._soundEnabled.set(next);
    localStorage.setItem('soundEnabled', String(next));
  }

  constructor() {
    this.loadInitialSessions();
  }

  async requestNotifications(): Promise<void> {
    if (!('Notification' in window)) return;
    const result = await Notification.requestPermission();
    this._notificationPermission.set(result);
  }

  private notify(title: string, body: string): void {
    if (!('Notification' in window) || Notification.permission !== 'granted') return;
    new Notification(title, { body, silent: true });
  }

  private scheduleIdleAlert(sessionId: string, label: string): void {
    this.cancelIdleTimer(sessionId);
    const timer = setTimeout(() => {
      this.idleTimers.delete(sessionId);
      if (document.hidden) {
        this.notify('Session waiting for input', label);
        this.playSound('idle');
      }
    }, 20_000);
    this.idleTimers.set(sessionId, timer);
  }

  private cancelIdleTimer(sessionId: string): void {
    const timer = this.idleTimers.get(sessionId);
    if (timer !== undefined) {
      clearTimeout(timer);
      this.idleTimers.delete(sessionId);
    }
  }

  private playSound(type: 'idle' | 'crashed' | 'closed'): void {
    if (!this._soundEnabled()) return;
    try {
      this.audioCtx ??= new AudioContext();
      const ctx = this.audioCtx;
      const gain = ctx.createGain();
      gain.connect(ctx.destination);

      const configs: Record<string, { freq: number; freq2?: number; duration: number; shape: OscillatorType }> = {
        idle:    { freq: 880, duration: 0.18, shape: 'sine' },
        crashed: { freq: 220, freq2: 180, duration: 0.4, shape: 'sawtooth' },
        closed:  { freq: 660, freq2: 440, duration: 0.25, shape: 'sine' },
      };
      const c = configs[type];
      const t = ctx.currentTime;

      const osc = ctx.createOscillator();
      osc.type = c.shape;
      osc.frequency.setValueAtTime(c.freq, t);
      if (c.freq2) osc.frequency.linearRampToValueAtTime(c.freq2, t + c.duration);
      osc.connect(gain);

      gain.gain.setValueAtTime(0.18, t);
      gain.gain.exponentialRampToValueAtTime(0.001, t + c.duration);

      osc.start(t);
      osc.stop(t + c.duration);
    } catch {
      // AudioContext not available or blocked — fail silently
    }
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
        const existing = this._sessions().find(s => s.sessionId === sessionId);
        if (existing && existing.status !== 'closed') {
          this.cancelIdleTimer(sessionId);
          this.notify('Session closed', existing.name || sessionId);
          this.playSound('closed');
        }
        this._sessions.update((sessions) =>
          sessions.map((s) => (s.sessionId === sessionId ? { ...s, status: 'closed' } : s))
        );
      } catch {
        console.error('Failed to parse session-closed event', event.data);
      }
    });

    es.addEventListener('session-event', (event: MessageEvent) => {
      try {
        const ev: SessionEvent = JSON.parse(event.data);
        this._sessionEvents.update(map => {
          const next = new Map(map);
          const existing = next.get(ev.sessionId) ?? [];
          next.set(ev.sessionId, [...existing, ev].slice(-50));
          return next;
        });
      } catch {
        console.error('Failed to parse session-event', event.data);
      }
    });

    es.onerror = () => {
      this._connected.set(false);
      this.closeEventSource();
      this.scheduleRetry();
    };
  }

  loadEvents(sessionId: string): void {
    if (this._sessionEvents().has(sessionId)) return;
    this.http.get<SessionEvent[]>(`/api/sessions/${sessionId}/events`).subscribe({
      next: (events) => {
        this._sessionEvents.update(map => {
          const next = new Map(map);
          next.set(sessionId, events);
          return next;
        });
      },
      error: () => {},
    });
  }

  deleteSession(sessionId: string): void {
    this.http.delete(`/api/sessions/${sessionId}`).subscribe({
      error: (e) => console.error('Delete failed', e),
    });
    this._sessions.update((sessions) => sessions.filter((s) => s.sessionId !== sessionId));
  }

  private upsertSession(session: Session): void {
    const existing = this._sessions().find(s => s.sessionId === session.sessionId);
    if (existing && existing.status !== session.status) {
      const label = session.name || session.sessionId;
      if (session.status === 'crashed') {
        this.cancelIdleTimer(session.sessionId);
      } else if (session.status === 'idle' && existing.status === 'running') {
        this.scheduleIdleAlert(session.sessionId, label);
      } else if (session.status === 'running') {
        this.cancelIdleTimer(session.sessionId);
      }
    }

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
    for (const timer of this.idleTimers.values()) clearTimeout(timer);
    this.idleTimers.clear();
  }
}
