import { Component, output } from '@angular/core';
import { FormsModule } from '@angular/forms';

/// Trivial room picker — the sample has no room directory API, so it just lets the user type or
/// pick a room id to join.
@Component({
  selector: 'app-room-list',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="room-list">
      <h3>Rooms</h3>
      <ul>
        @for (room of suggestions; track room) {
          <li>
            <button type="button" (click)="select(room)">{{ room }}</button>
          </li>
        }
      </ul>
      <form (ngSubmit)="submit()">
        <input name="roomId" [(ngModel)]="roomId" placeholder="room id" autocomplete="off" />
        <button type="submit" [disabled]="!roomId">Join</button>
      </form>
    </div>
  `,
})
export class RoomListComponent {
  readonly suggestions = ['general', 'random', 'switchboard-dev'];
  roomId = '';
  readonly roomSelected = output<string>();

  select(room: string): void {
    this.roomSelected.emit(room);
  }

  submit(): void {
    if (this.roomId) {
      this.roomSelected.emit(this.roomId);
    }
  }
}
