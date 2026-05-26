import { Component, inject, computed, signal } from '@angular/core';
import { SessionSseService } from './services/session-sse.service';
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

  readonly connected = this.sseService.connected;

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
