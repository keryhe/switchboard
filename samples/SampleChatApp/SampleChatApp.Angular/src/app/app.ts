import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AuthService } from './auth/auth.service';
import { ChatRoomComponent } from './chat/chat-room/chat-room.component';
import { RoomListComponent } from './chat/room-list/room-list.component';

@Component({
  selector: 'app-root',
  imports: [FormsModule, RoomListComponent, ChatRoomComponent],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  usernameInput = '';
  readonly activeRoomId = signal<string | null>(null);
  readonly loginError = signal<string | null>(null);

  constructor(readonly auth: AuthService) {}

  async login(): Promise<void> {
    this.loginError.set(null);
    try {
      await this.auth.login(this.usernameInput);
    } catch (err) {
      this.loginError.set('Login failed — is SampleChatApp.Api running?');
    }
  }

  logout(): void {
    this.activeRoomId.set(null);
    this.auth.logout();
  }

  onRoomSelected(roomId: string): void {
    this.activeRoomId.set(roomId);
  }
}
