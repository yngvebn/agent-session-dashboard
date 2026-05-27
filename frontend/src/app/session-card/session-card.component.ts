import { Component, Input, OnDestroy, OnInit, signal, computed, inject, input } from '@angular/core';
import { UpperCasePipe, DatePipe } from '@angular/common';
import { Session, SessionEvent, SessionSseService } from '../services/session-sse.service';

@Component({
  selector: 'app-session-card',
  standalone: true,
  imports: [UpperCasePipe, DatePipe],
  templateUrl: './session-card.component.html',
  styleUrl: './session-card.component.scss',
})
export class SessionCardComponent implements OnInit, OnDestroy {
  @Input({ required: true }) session!: Session;
  @Input() compact = false;

  private sseService = inject(SessionSseService);
  private now = signal(Date.now());
  private intervalId: ReturnType<typeof setInterval> | null = null;
  copied = signal(false);
  timelineOpen = signal(false);

  readonly events = computed<SessionEvent[]>(() => {
    const map = this.sseService.sessionEvents();
    return map.get(this.session.sessionId) ?? [];
  });

  readonly cost = computed(() => {
    const inCost = (this.session.tokensIn / 1_000_000) * 3;
    const outCost = (this.session.tokensOut / 1_000_000) * 15;
    return inCost + outCost;
  });

  get hasTokens(): boolean {
    return this.session.tokensIn > 0 || this.session.tokensOut > 0;
  }

  toggleTimeline(): void {
    const willOpen = !this.timelineOpen();
    this.timelineOpen.set(willOpen);
    if (willOpen) {
      this.sseService.loadEvents(this.session.sessionId);
    }
  }

  formatTokens(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`;
    return `${n}`;
  }

  get isDismissable(): boolean {
    return this.session.status === 'closed' || this.session.status === 'crashed';
  }

  get isInactive(): boolean {
    return this.session.status === 'closed' || this.session.status === 'crashed';
  }

  dismiss(): void {
    this.sseService.deleteSession(this.session.sessionId);
  }

  lastSeenAgo = computed(() => this.relativeTime(this.session.lastSeen, this.now()));
  startedAgo = computed(() => this.relativeTime(this.session.startedAt, this.now()));
  worktreeName = computed(() => {
    const wt = this.session.worktree;
    if (!wt) return '';
    return wt.split(/[\\/]/).filter(Boolean).pop() ?? wt;
  });

  get resumeCommand(): string {
    const dir = this.session.workingDir || '.';
    return `cd "${dir}" && claude --resume ${this.session.sessionId}`;
  }

  async copyResumeCommand(): Promise<void> {
    await navigator.clipboard.writeText(this.resumeCommand);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 2000);
  }

  elapsedDuration = computed(() => {
    const elapsed = Math.max(0, this.now() - new Date(this.session.startedAt).getTime());
    const s = Math.floor(elapsed / 1000) % 60;
    const m = Math.floor(elapsed / 60000) % 60;
    const h = Math.floor(elapsed / 3600000);
    if (h > 0) return `${h}h ${m}m`;
    if (m > 0) return `${m}m ${s}s`;
    return `${s}s`;
  });

  get showDuration(): boolean {
    return this.session.status === 'running' || this.session.status === 'idle';
  }

  ngOnInit(): void {
    this.intervalId = setInterval(() => this.now.set(Date.now()), 1_000);
  }

  ngOnDestroy(): void {
    if (this.intervalId !== null) {
      clearInterval(this.intervalId);
    }
  }

  get statusIcon(): string {
    switch (this.session.status) {
      case 'running':
        return '●';
      case 'idle':
        return '○';
      case 'closed':
        return '✕';
      case 'crashed':
        return '○';
      default:
        return '?';
    }
  }

  get statusLabel(): string {
    return this.session.status === 'crashed' ? 'inactive' : this.session.status;
  }

  private relativeTime(isoTimestamp: string, now: number): string {
    const then = new Date(isoTimestamp).getTime();
    const diffMs = now - then;
    if (diffMs < 0) return 'just now';
    const diffSec = Math.floor(diffMs / 1000);
    if (diffSec < 10) return 'just now';
    if (diffSec < 60) return `${diffSec}s ago`;
    const diffMin = Math.floor(diffSec / 60);
    if (diffMin < 60) return `${diffMin}m ago`;
    const diffHr = Math.floor(diffMin / 60);
    if (diffHr < 24) return `${diffHr}h ago`;
    const diffDays = Math.floor(diffHr / 24);
    return `${diffDays}d ago`;
  }
}
