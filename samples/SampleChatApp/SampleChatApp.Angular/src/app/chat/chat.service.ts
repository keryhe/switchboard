import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { environment } from '../../environments/environment';

export interface ChatMessage {
  from: string;
  text: string;
  sentAt: string;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private connection: signalR.HubConnection;

  readonly messageReceived$ = new Subject<ChatMessage>();
  readonly userJoined$ = new Subject<string>();
  readonly userLeft$ = new Subject<string>();
  readonly systemMessage$ = new Subject<string>();
  readonly connectedConnectionId$ = new Subject<string>();

  constructor(private auth: AuthService) {
    this.connection = new signalR.HubConnectionBuilder()
      // The API's own route is /chatHub, not /api/chatHub (docs/docs/08-sample-app.md's Route
      // correction note) — the service's single-segment {hub} route parameter can't span
      // multiple path segments, so SampleChatApp.Api maps ChatHub at the bare /chatHub instead.
      .withUrl(`${environment.apiUrl}/chatHub`, {
        // The API validates this token to extract userId before forwarding negotiate.
        accessTokenFactory: () => this.auth.getAccessToken() ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.registerHandlers();
  }

  private registerHandlers(): void {
    this.connection.on('ReceiveMessage', (msg: ChatMessage) => {
      this.messageReceived$.next(msg);
    });

    this.connection.on('UserJoined', (userId: string) => {
      this.userJoined$.next(userId);
    });

    this.connection.on('UserLeft', (userId: string) => {
      this.userLeft$.next(userId);
    });

    this.connection.on('SystemMessage', (text: string) => {
      this.systemMessage$.next(text);
    });

    this.connection.on('Connected', (connectionId: string) => {
      console.log('Connected to SignalR proxy, connectionId:', connectionId);
      this.connectedConnectionId$.next(connectionId);
    });

    this.connection.onreconnecting(() => console.log('Reconnecting...'));
    this.connection.onreconnected(() => console.log('Reconnected'));
    this.connection.onclose(() => console.log('Connection closed'));
  }

  get state(): signalR.HubConnectionState {
    return this.connection.state;
  }

  async start(): Promise<void> {
    if (this.connection.state === signalR.HubConnectionState.Disconnected) {
      await this.connection.start();
    }
  }

  async stop(): Promise<void> {
    await this.connection.stop();
  }

  async joinRoom(roomId: string): Promise<void> {
    await this.connection.invoke('JoinRoom', roomId);
  }

  async leaveRoom(roomId: string): Promise<void> {
    await this.connection.invoke('LeaveRoom', roomId);
  }

  async sendMessage(roomId: string, text: string): Promise<void> {
    await this.connection.invoke('SendMessage', roomId, text);
  }
}
