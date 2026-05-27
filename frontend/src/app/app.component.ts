import { Component, inject, computed, signal, effect } from '@angular/core';
import { SessionSseService } from './services/session-sse.service';
import { VersionService } from './services/version.service';
import { SessionCardComponent } from './session-card/session-card.component';
import { ShortcutSidebarComponent } from './shortcut-sidebar/shortcut-sidebar.component';

const STATUS_RANK: Record<string, number> = {
  running: 0,
  idle: 1,
  crashed: 2,
};

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [SessionCardComponent, ShortcutSidebarComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  private sseService = inject(SessionSseService);
  private versionService = inject(VersionService);

  readonly updateAvailable = this.versionService.updateAvailable;
  dismissUpdate(): void { this.versionService.dismiss(); }

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

  readonly activeSessions = computed(() => {
    const sessions = this.sseService.sessions().filter(s => s.status !== 'closed');
    return sessions.sort((a, b) => {
      const rankDiff = (STATUS_RANK[a.status] ?? 99) - (STATUS_RANK[b.status] ?? 99);
      if (rankDiff !== 0) return rankDiff;
      return new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime();
    });
  });

  readonly closedSessions = computed(() => {
    return this.sseService.sessions()
      .filter(s => s.status === 'closed')
      .sort((a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime());
  });
}
