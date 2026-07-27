import { HttpClient } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

const STORAGE_KEY = 'sampleChatApp.accessToken';

/// Dev-only login against SampleChatApp.Api's AuthController — no password check on the API side,
/// so this just holds whatever username the user typed and stores the JWT it comes back with.
@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly username = signal<string | null>(sessionStorage.getItem(`${STORAGE_KEY}.username`));

  constructor(private readonly http: HttpClient) {}

  isLoggedIn(): boolean {
    return this.getAccessToken() !== null;
  }

  getAccessToken(): string | null {
    return sessionStorage.getItem(STORAGE_KEY);
  }

  async login(username: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<{ accessToken: string }>(`${environment.apiUrl}/api/auth/login`, { username }),
    );

    sessionStorage.setItem(STORAGE_KEY, response.accessToken);
    sessionStorage.setItem(`${STORAGE_KEY}.username`, username);
    this.username.set(username);
  }

  logout(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    sessionStorage.removeItem(`${STORAGE_KEY}.username`);
    this.username.set(null);
  }
}
