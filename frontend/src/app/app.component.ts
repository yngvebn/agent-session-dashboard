import { Component, inject, computed, signal, effect } from '@angular/core';
import { Session, SessionSseService } from './services/session-sse.service';
import { VersionService } from './services/version.service';
import { ClaudeStatusService } from './services/claude-status.service';
import { QuestionService } from './services/question.service';
import { SessionCardComponent } from './session-card/session-card.component';
import { SessionShelfComponent } from './session-shelf/session-shelf.component';
import { ShortcutSidebarComponent } from './shortcut-sidebar/shortcut-sidebar.component';
import { QuestionCardComponent } from './question-card/question-card.component';

/** Ordering within the live grid. Inactive and closed never reach it. */
const STATUS_RANK: Record<string, number> = {
  running: 0,
  idle: 1,
};

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [SessionCardComponent, SessionShelfComponent, ShortcutSidebarComponent, QuestionCardComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  private sseService = inject(SessionSseService);
  private versionService = inject(VersionService);
  private questionService = inject(QuestionService);

  readonly openQuestions = this.questionService.openQuestions;
  readonly pendingQuestions = this.questionService.pendingQuestions;
  readonly heartbeatLabel = this.questionService.heartbeatLabel;
  readonly heartbeatStale = this.questionService.isStale;
  readonly runInFlight = this.questionService.runInFlight;
  readonly runRequested = this.questionService.runRequested;

  requestRun(): void { this.questionService.requestRun(); }

  readonly updateAvailable = this.versionService.updateAvailable;
  dismissUpdate(): void { this.versionService.dismiss(); }

  private claudeStatusService = inject(ClaudeStatusService);
  readonly claudeIncidents = this.claudeStatusService.incidents;

  readonly connected = this.sseService.connected;
  readonly notificationPermission = this.sseService.notificationPermission;
  readonly soundEnabled = this.sseService.soundEnabled;

  requestNotifications(): void {
    this.sseService.requestNotifications();
  }

  toggleSound(): void {
    this.sseService.toggleSound();
  }

  private sparklineHistory = signal<number[]>([]);

  constructor() {
    effect(() => {
      const count = this.sseService.sessions().filter(s => s.status !== 'closed').length;
      this.sparklineHistory.update(h => [...h.slice(-59), count]);
    });
  }

  readonly sparklinePoints = computed(() => {
    const h = this.sparklineHistory();
    if (h.length < 2) return '';
    const max = Math.max(...h, 1);
    const w = 80, height = 24;
    return h.map((v, i) => {
      const x = (i / (h.length - 1)) * w;
      const y = height - (v / max) * (height - 4) - 2;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    }).join(' ');
  });

  /**
   * The grid is for sessions that are still doing something. Inactive and closed
   * ones live on shelves below it — a full card each is how four dead sessions
   * push the live ones off the screen.
   *
   * Filtered by exclusion rather than by listing running/idle, so a status the
   * backend grows later shows up in the grid instead of vanishing from the UI.
   */
  readonly activeSessions = computed(() => {
    const sessions = this.sseService.sessions()
      .filter(s => s.status !== 'closed' && s.status !== 'crashed');
    return sessions.sort((a, b) => {
      const rankDiff = (STATUS_RANK[a.status] ?? 99) - (STATUS_RANK[b.status] ?? 99);
      if (rankDiff !== 0) return rankDiff;
      return new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime();
    });
  });

  /** `crashed` is what the card labels INACTIVE — resumable, just not running. */
  readonly inactiveSessions = computed(() => this.byLastSeen('crashed'));

  readonly closedSessions = computed(() => this.byLastSeen('closed'));

  private byLastSeen(status: Session['status']): Session[] {
    return this.sseService.sessions()
      .filter(s => s.status === status)
      .sort((a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime());
  }

  dismissAllClosed(): void {
    this.sseService.deleteAllClosed();
  }
}
