import { Component, Input, OnChanges, OnDestroy, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ChatMessage, ChatService } from '../chat.service';

interface FeedEntry {
  kind: 'message' | 'joined' | 'left' | 'system';
  text: string;
}

@Component({
  selector: 'app-chat-room',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './chat-room.component.html',
})
export class ChatRoomComponent implements OnChanges, OnDestroy {
  @Input({ required: true }) roomId!: string;

  readonly feed = signal<FeedEntry[]>([]);
  readonly draft = signal('');
  private previousRoomId: string | null = null;
  private subscriptions: Subscription[] = [];

  constructor(private readonly chat: ChatService) {}

  ngOnChanges(): void {
    if (this.roomId === this.previousRoomId) {
      return;
    }

    void this.switchRoom(this.previousRoomId, this.roomId);
    this.previousRoomId = this.roomId;
  }

  ngOnDestroy(): void {
    this.subscriptions.forEach((s) => s.unsubscribe());
    if (this.previousRoomId) {
      void this.chat.leaveRoom(this.previousRoomId);
    }
  }

  async sendMessage(): Promise<void> {
    const text = this.draft().trim();
    if (!text) {
      return;
    }

    await this.chat.sendMessage(this.roomId, text);
    this.draft.set('');
  }

  private async switchRoom(previousRoomId: string | null, nextRoomId: string): Promise<void> {
    await this.chat.start();

    if (previousRoomId) {
      await this.chat.leaveRoom(previousRoomId);
    }

    this.feed.set([]);
    this.subscriptions.forEach((s) => s.unsubscribe());
    this.subscriptions = [
      this.chat.messageReceived$.subscribe((msg: ChatMessage) =>
        this.append({ kind: 'message', text: `${msg.from}: ${msg.text}` }),
      ),
      this.chat.userJoined$.subscribe((userId) => this.append({ kind: 'joined', text: `${userId} joined` })),
      this.chat.userLeft$.subscribe((userId) => this.append({ kind: 'left', text: `${userId} left` })),
      this.chat.systemMessage$.subscribe((text) => this.append({ kind: 'system', text })),
    ];

    await this.chat.joinRoom(nextRoomId);
  }

  private append(entry: FeedEntry): void {
    this.feed.update((entries) => [...entries, entry]);
  }
}
