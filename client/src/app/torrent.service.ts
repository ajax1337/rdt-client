import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Observable, Subject } from 'rxjs';
import { Torrent, TorrentFileAvailability } from './models/torrent.model';
import { DiskSpaceStatus } from './models/disk-space-status.model';
import { RateLimitStatus } from './models/rate-limit-status.model';
import { APP_BASE_HREF } from '@angular/common';

export type SignalRConnectionState = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

@Injectable({
  providedIn: 'root',
})
export class TorrentService {
  private http = inject(HttpClient);
  private baseHref = inject(APP_BASE_HREF);

  public update$: Subject<Torrent[]> = new Subject();
  public diskSpaceStatus$: Subject<DiskSpaceStatus> = new Subject();
  public rateLimitStatus$: Subject<RateLimitStatus> = new Subject();
  // BehaviorSubject so late subscribers immediately get the current state. The UI
  // uses this to grey out speed/ETA fields when we're not actually receiving live
  // pushes — that single signal removes most of the "is the dashboard stuck?"
  // perception without needing to make the SignalR cadence tighter on the server.
  public connectionState$: Subject<SignalRConnectionState> = new Subject();
  private currentConnectionState: SignalRConnectionState = 'connecting';

  private connection: signalR.HubConnection;

  constructor() {
    this.connect();
  }

  public getConnectionState(): SignalRConnectionState {
    return this.currentConnectionState;
  }

  private setConnectionState(state: SignalRConnectionState): void {
    this.currentConnectionState = state;
    this.connectionState$.next(state);
  }

  public connect(): void {
    if (this.connection != null) {
      return;
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.baseHref}hub`)
      .withAutomaticReconnect()
      .build();

    this.connection.on('update', (torrents: Torrent[]) => {
      this.update$.next(torrents);
    });

    this.connection.on('diskSpaceStatus', (status: any) => {
      this.diskSpaceStatus$.next(status);
    });

    this.connection.on('rateLimitStatus', (status: any) => {
      this.rateLimitStatus$.next(status);
    });

    this.connection.onreconnecting(() => {
      this.setConnectionState('reconnecting');
    });

    this.connection.onreconnected(() => {
      this.setConnectionState('connected');
      // Force an immediate snapshot so visible rows don't sit on whatever stale
      // data the last push delivered before the disconnect.
      this.getList().subscribe({
        next: (list) => this.update$.next(list ?? []),
      });
      this.getDiskSpaceStatus().subscribe({
        next: (status) => {
          if (status) {
            this.diskSpaceStatus$.next(status);
          }
        },
      });
    });

    this.connection.onclose(() => {
      this.setConnectionState('disconnected');
    });

    this.setConnectionState('connecting');
    this.connection
      .start()
      .then(() => this.setConnectionState('connected'))
      .catch((err) => {
        console.error(err);
        this.setConnectionState('disconnected');
      });
  }

  public getList(): Observable<Torrent[]> {
    return this.http.get<Torrent[]>(`${this.baseHref}Api/Torrents`);
  }

  public get(torrentId: string): Observable<Torrent> {
    return this.http.get<Torrent>(`${this.baseHref}Api/Torrents/Get/${torrentId}`);
  }

  public getDiskSpaceStatus(): Observable<DiskSpaceStatus | null> {
    return this.http.get<DiskSpaceStatus | null>(`${this.baseHref}Api/Torrents/DiskSpaceStatus`);
  }

  public getRateLimitStatus(): Observable<RateLimitStatus | null> {
    return this.http.get<RateLimitStatus | null>(`${this.baseHref}Api/Torrents/RateLimitStatus`);
  }

  public uploadMagnet(magnetLink: string, torrent: Torrent): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadMagnet`, {
      magnetLink,
      torrent,
    });
  }

  public uploadFile(file: File, torrent: Torrent): Observable<void> {
    const formData: FormData = new FormData();
    formData.append('file', file);
    formData.append('formData', JSON.stringify({ torrent }));
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadFile`, formData);
  }

  public uploadNzbLink(nzbLink: string, torrent: Torrent): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadNzbLink`, {
      nzbLink,
      torrent,
    });
  }

  public uploadNzbFile(file: File, torrent: Torrent): Observable<void> {
    const formData: FormData = new FormData();
    formData.append('file', file);
    formData.append('formData', JSON.stringify({ torrent }));
    return this.http.post<void>(`${this.baseHref}Api/Torrents/UploadNzbFile`, formData);
  }

  public checkFilesMagnet(magnetLink: string): Observable<TorrentFileAvailability[]> {
    return this.http.post<TorrentFileAvailability[]>(`${this.baseHref}Api/Torrents/CheckFilesMagnet`, {
      magnetLink,
    });
  }

  public checkFiles(file: File): Observable<TorrentFileAvailability[]> {
    const formData: FormData = new FormData();
    formData.append('file', file);
    return this.http.post<TorrentFileAvailability[]>(`${this.baseHref}Api/Torrents/CheckFiles`, formData);
  }

  public delete(
    torrentId: string,
    deleteData: boolean,
    deleteRdTorrent: boolean,
    deleteLocalFiles: boolean,
  ): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/Delete/${torrentId}`, {
      deleteData,
      deleteRdTorrent,
      deleteLocalFiles,
    });
  }

  public retry(torrentId: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/Retry/${torrentId}`, {});
  }

  public retryDownload(downloadId: string): Observable<void> {
    return this.http.post<void>(`${this.baseHref}Api/Torrents/RetryDownload/${downloadId}`, {});
  }

  public update(torrent: Torrent): Observable<void> {
    return this.http.put<void>(`${this.baseHref}Api/Torrents/Update`, torrent);
  }

  public verifyRegex(
    includeRegex: string,
    excludeRegex: string,
    magnetLink: string,
  ): Observable<{ includeError: string; excludeError: string; selectedFiles: TorrentFileAvailability[] }> {
    return this.http.post<{ includeError: string; excludeError: string; selectedFiles: TorrentFileAvailability[] }>(
      `${this.baseHref}Api/Torrents/VerifyRegex`,
      {
        includeRegex,
        excludeRegex,
        magnetLink,
      },
    );
  }
}
