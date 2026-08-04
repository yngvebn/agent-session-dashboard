import { Component, OnInit, input, signal } from '@angular/core';
import { Session } from '../services/session-sse.service';
import { SessionCardComponent } from '../session-card/session-card.component';

/**
 * A collapsible shelf of compact session rows, parked below the live grid.
 *
 * Both shelves — inactive and closed — are things you occasionally want and
 * mostly don't, so they get the same treatment: one line of chrome when shut,
 * a list of compact rows when open. Full cards are reserved for sessions that
 * are actually doing something.
 */
@Component({
  selector: 'app-session-shelf',
  standalone: true,
  imports: [SessionCardComponent],
  templateUrl: './session-shelf.component.html',
  styleUrl: './session-shelf.component.scss',
})
export class SessionShelfComponent implements OnInit {
  readonly label = input.required<string>();
  readonly sessions = input.required<Session[]>();

  /** localStorage key holding the open/shut choice, so it survives a reload. */
  readonly storageKey = input.required<string>();

  /** Where the shelf sits before the user has ever touched it. */
  readonly startCollapsed = input(false);

  readonly collapsed = signal(false);

  ngOnInit(): void {
    // Read in ngOnInit rather than a field initialiser: signal inputs aren't
    // populated until after construction.
    const saved = localStorage.getItem(this.storageKey());
    this.collapsed.set(saved === null ? this.startCollapsed() : saved === 'true');
  }

  toggle(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    localStorage.setItem(this.storageKey(), String(next));
  }
}
