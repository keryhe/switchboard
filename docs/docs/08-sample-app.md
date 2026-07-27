# Sample Application Architecture

The sample application is a real-time chat app that demonstrates the full end-to-end flow through the Switchboard. It is intentionally simple — the goal is to exercise every integration point, not to be a production chat product.

`SampleChatApp.Api` maps `ChatHub` at the bare `/chatHub`, not `/api/chatHub` — the service's own routes (`/{hub}/negotiate`, `/{hub}`, `/server/{hub}`) use a single-segment `{hub}` route parameter, which can't span multiple path segments (found during Phase 1 implementation). Every `/chatHub` path below reflects that; only the REST endpoints genuinely under `/api` (`/api/auth/login`, `/api/rooms/{roomId}/system-message`) use that prefix. `SampleChatApp.Angular` exists as of Phase 2 (Slice 9) and this document has been verified against its actual, running implementation — not just against the API side, as in Phase 1.

---

## Components

```
SampleChatApp/
├── SampleChatApp.Api/        # ASP.NET Core Web API — ChatHub, auth, negotiate endpoint
└── SampleChatApp.Angular/    # Angular SPA — chat UI, SignalR client
```

The proxy service (`Keryhe.Switchboard.Server`) runs separately and is not part of the sample solution. The sample connects to a running proxy instance.

---

## System Topology

```mermaid
graph LR
    subgraph Browser
        ANG[Angular App<br/>@microsoft/signalr]
    end

    subgraph SampleChatApp.Api
        NEG[/chatHub/negotiate]
        HUB[ChatHub]
        AUTH[Auth Middleware]
    end

    subgraph Switchboard["Switchboard"]
        PROXY[Proxy + Backplane]
    end

    ANG -->|1. POST /chatHub/negotiate| AUTH
    AUTH --> NEG
    NEG -->|forward negotiate| PROXY
    PROXY -->|url + accessToken| NEG
    NEG -->|redirect response| ANG
    ANG -->|2. WebSocket| PROXY
    PROXY <-->|persistent WebSocket pool| HUB
```

---

## Connection Flow (Step by Step)

### Step 1 — Angular negotiates through the API

```
POST http://localhost:5001/chatHub/negotiate
Authorization: Bearer <user JWT from login>
```

The API's auth middleware validates the user JWT, extracts the `userId` and any claims, then calls the proxy service's negotiate endpoint on the user's behalf:

```
POST https://localhost:7000/chatHub/negotiate
Authorization: Bearer <server access token>
X-Switchboard-UserId: alice
```

The proxy issues a short-lived client JWT and returns a **redirect response** (only `url` + `accessToken`; note `url` is `https://`, not `wss://`):

```json
{
  "url": "https://localhost:7000/chatHub",
  "accessToken": "<60s JWT binding connectionId to alice>"
}
```

The API passes this response directly back to Angular.

### Step 2 — Angular re-negotiates with the proxy

The `@microsoft/signalr` client follows the redirect automatically — it negotiates a **second** time, now against the proxy `url`, presenting the access token from step 1:

```
POST https://localhost:7000/chatHub/negotiate?negotiateVersion=1
Authorization: Bearer <accessToken from step 1>
```

The proxy validates the token and returns a standard (non-redirect) negotiate response with the connection token and transports:

```json
{
  "connectionId": "<connectionId>",
  "connectionToken": "<connectionToken>",
  "negotiateVersion": 1,
  "availableTransports": [
    { "transport": "WebSockets", "transferFormats": ["Text", "Binary"] }
  ]
}
```

### Step 3 — Angular opens WebSocket to the proxy

The client now opens the transport against the proxy `url` (scheme switched to `wss`), using the `connectionToken` as `id`:

```
GET wss://localhost:7000/chatHub?id=<connectionToken>&access_token=<jwt>
Upgrade: websocket
```

The proxy validates the JWT, registers the connection, sends `open_connection` to the API's server connection, and completes the hub protocol handshake.

### Step 4 — API pushes messages through the proxy

When the API calls `hubContext.Clients.All.SendAsync(...)`, the connector library forwards it to the proxy over the persistent server connection. The proxy fans it out to all connected Angular clients.

---

## SampleChatApp.Api

### Project Structure

```
SampleChatApp.Api/
├── Hubs/
│   └── ChatHub.cs
├── Controllers/
│   ├── AuthController.cs         # POST /api/auth/login → issues user JWT
│   └── RoomsController.cs        # POST /api/rooms/{roomId}/system-message → demo trigger for the SystemMessage push below (Phase 2)
├── Services/
│   └── MessageService.cs         # Business logic; injects IHubContext<ChatHub>
├── Program.cs
└── appsettings.json
```

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(/* validate user JWTs from AuthController */);

builder.Services.AddAuthorization();

builder.Services.AddSignalR()
    .AddSwitchboardConnector(options =>
    {
        options.ServiceUrl = builder.Configuration["Switchboard:Url"];
        options.ServerAccessToken = builder.Configuration["Switchboard:ServerToken"];
    });

builder.Services.AddControllers();
builder.Services.AddScoped<MessageService>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/chatHub");   // negotiate endpoint lives here

app.Run();
```

### appsettings.json

```json
{
  "Switchboard": {
    "Url": "https://localhost:7000",
    "ServerToken": "<long-lived server access token>"
  }
}
```

### ChatHub.cs

```csharp
[Authorize]
public class ChatHub : Hub
{
    public async Task SendMessage(string roomId, string text)
    {
        var sender = Context.UserIdentifier;
        await Clients.Group(roomId).SendAsync("ReceiveMessage", new
        {
            From = sender,
            Text = text,
            SentAt = DateTimeOffset.UtcNow
        });
    }

    public async Task JoinRoom(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserJoined", Context.UserIdentifier);
    }

    public async Task LeaveRoom(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).SendAsync("UserLeft", Context.UserIdentifier);
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }
}
```

### Hub Features Demonstrated

| Feature | Hub Method | Angular Handler |
|---|---|---|
| Send to group | `SendMessage` → `Clients.Group(roomId)` | `ReceiveMessage` |
| Join group | `JoinRoom` → `Groups.AddToGroupAsync` | `UserJoined` |
| Leave group | `LeaveRoom` → `Groups.RemoveFromGroupAsync` | `UserLeft` |
| Connected confirmation | `OnConnectedAsync` → `Clients.Caller` | `Connected` |
| Server push (no client trigger) | `MessageService` → `IHubContext` | `SystemMessage` |

The `SystemMessage` event (sent via `IHubContext` from `MessageService`) demonstrates server-initiated push — a common pattern where something outside a hub method (a background job, a webhook handler, etc.) needs to push to clients. `RoomsController`'s `POST /api/rooms/{roomId}/system-message` is that "something outside a hub method" for the sample — a plain authenticated HTTP endpoint, not a hub method, exists purely to give this push path a way to actually fire when running the sample by hand.

---

## SampleChatApp.Angular

### Project Structure

```
SampleChatApp.Angular/
├── src/
│   ├── app/
│   │   ├── auth/
│   │   │   ├── auth.service.ts           # Login, token storage
│   │   │   └── auth.interceptor.ts       # Attach Bearer token to API requests
│   │   ├── chat/
│   │   │   ├── chat.service.ts           # SignalR connection management
│   │   │   ├── chat-room/
│   │   │   │   ├── chat-room.component.ts
│   │   │   │   └── chat-room.component.html
│   │   │   └── room-list/
│   │   │       └── room-list.component.ts
│   │   └── app.config.ts
│   └── environments/
│       └── environment.ts
└── package.json
```

### Key Dependency

```bash
npm install @microsoft/signalr
```

### ChatService (connection management)

```typescript
import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { AuthService } from '../auth/auth.service';
import { Subject } from 'rxjs';

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

  constructor(private auth: AuthService) {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/chatHub', {
        // The API validates this token to extract userId before forwarding negotiate
        accessTokenFactory: () => this.auth.getAccessToken()
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

    this.connection.on('Connected', (connectionId: string) => {
      console.log('Connected to SignalR proxy, connectionId:', connectionId);
    });

    this.connection.onreconnecting(() => console.log('Reconnecting...'));
    this.connection.onreconnected(() => console.log('Reconnected'));
    this.connection.onclose(() => console.log('Connection closed'));
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
```

### environment.ts

```typescript
export const environment = {
  production: false,
  apiUrl: 'https://localhost:5001'   // API base URL; SignalR negotiates through here
  // No proxy service URL needed — Angular never talks to it directly
};
```

---

## Configuration Summary

Four URLs are in play, but Angular only needs to know one:

| URL | Known by | Purpose |
|---|---|---|
| `http://localhost:5001/chatHub` | Angular | Negotiate endpoint (points at API) |
| `https://localhost:7000` | API only | Proxy service — API connects here as app server |
| `https://localhost:7000/chatHub` | Angular (auto, from redirect) | Redirect target — client re-negotiates here (step 2) |
| `wss://localhost:7000/chatHub` | Angular (auto, derived) | Final WebSocket connection (scheme switched from the redirect `url`) |

Angular is configured with only the API URL. The proxy URL is an implementation detail of the API's configuration.

---

## Local Development Setup

Running the sample locally requires three processes:

```
Terminal 1:  dotnet run --project src/Keryhe.Switchboard.Server --urls http://127.0.0.1:7000 -- --Switchboard:PublicUrl http://127.0.0.1:7000 --Switchboard:AllowedOrigins:0 http://localhost:4200
Terminal 2:  dotnet run --project samples/SampleChatApp/SampleChatApp.Api --urls http://127.0.0.1:5001 -- --Switchboard:Url http://127.0.0.1:7000 --Switchboard:ServerToken <token from `token generate --role appserver`>
Terminal 3:  cd samples/SampleChatApp/SampleChatApp.Angular && ng serve   # Angular on :4200
```

Plain `http`/`ws` (not `https`/`wss`) is what was actually verified end to end for local
development — it avoids needing a trusted local dev TLS certificate on all three processes, and
nothing here is exposed beyond localhost. Use `https`/`wss` (and drop `--secure: false` below) for
any real deployment.

Angular's `proxy.conf.json` proxies **both** `/api` (login, the `RoomsController` demo endpoint)
and `/chatHub` to the API — they're separate prefixes on the real API, unlike the single `/api`
prefix this document originally illustrated:

```json
{
  "/api": {
    "target": "http://localhost:5001",
    "secure": false,
    "changeOrigin": true
  },
  "/chatHub": {
    "target": "http://localhost:5001",
    "secure": false,
    "changeOrigin": true,
    "ws": true
  }
}
```

This means Angular always calls `/chatHub/negotiate` and the dev proxy forwards it — no CORS configuration needed in development. The actual `SampleChatApp.Angular/proxy.conf.json` proxies both `/api` (login, the `RoomsController` demo endpoint) and `/chatHub` to the API, since the two live under different path prefixes on the real API.

---

## What This Sample Exercises

| Proxy Feature | Demonstrated By |
|---|---|
| Client negotiate + redirect | Angular `HubConnectionBuilder` connecting |
| JWT issuance and validation | API forwarding userId; Angular using short-lived token |
| WebSocket client transport | Default transport in `@microsoft/signalr` |
| Server connection pool | API connecting to proxy on startup |
| `open_connection` / `close_connection` | Angular connect/disconnect |
| Hub method invocation (client → server) | `SendMessage`, `JoinRoom`, `LeaveRoom` |
| Targeted client response (`Clients.Caller`) | `Connected` confirmation on connect |
| Group membership | `JoinRoom` / `LeaveRoom` via `Groups.AddToGroupAsync` |
| Group fan-out | `SendMessage` → `Clients.Group(roomId)` |
| Server-initiated push (IHubContext) | `MessageService` sending `SystemMessage` |
| Automatic reconnect | Angular `withAutomaticReconnect()` + proxy `allowReconnect: true` |
