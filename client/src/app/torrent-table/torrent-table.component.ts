import { Component, DestroyRef, OnDestroy, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { Torrent } from '../models/torrent.model';
import { DiskSpaceStatus } from '../models/disk-space-status.model';
import { RateLimitStatus } from '../models/rate-limit-status.model';
import { TorrentService } from '../torrent.service';
import { forkJoin, Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { NgClass, DecimalPipe, DatePipe } from '@angular/common';
import { getTorrentStatus } from '../torrent-status.pipe';
import { SortDirection, getSortFieldValue, sortItems } from '../sort.pipe';
import { FileSizePipe } from '../filesize.pipe';
import { EtaPipe } from '../eta.pipe';

type StatusKind = 'sending' | 'queued' | 'processing' | 'waiting' | 'downloading' | 'retrying' | 'finished' | 'error';

// Window after a torrent is added during which we show "Sending to provider…" instead of
// "Not Yet Added to Provider". 30 s covers TorBox's typical AddMagnet round-trip plus the
// post-add UpdateTorrentClientData. After 30 s, if RdId is still null, something is wrong
// (rate-limit, provider outage) and the user should see the truthful queued state.
const OPTIMISTIC_SENDING_WINDOW_MS = 30_000;

interface KpiSnapshot {
  total: number;
  active: number;
  queued: number;
  errors: number;
  aggregateSpeed: number;
  finished: number;
}

@Component({
  selector: 'app-torrent-table',
  templateUrl: './torrent-table.component.html',
  styleUrls: ['./torrent-table.component.scss'],
  imports: [FormsModule, NgClass, DecimalPipe, DatePipe, FileSizePipe, EtaPipe],
  standalone: true,
})
export class TorrentTableComponent implements OnInit, OnDestroy {
  private destroyRef = inject(DestroyRef);
  private router = inject(Router);
  private torrentService = inject(TorrentService);
  private selectedTorrentIds = new Set<string>();

  public torrents: Torrent[] = [];
  public sortedTorrents: Torrent[] = [];
  public visibleTorrents: Torrent[] = [];
  public selectedTorrents: string[] = [];
  public error: string;
  public sortProperty = 'added';
  public sortDirection: SortDirection = 'desc';

  public searchText = '';
  public statusFilter: 'all' | StatusKind = 'all';
  public categoryFilter = '';
  public availableCategories: string[] = [];

  public kpis: KpiSnapshot = { total: 0, active: 0, queued: 0, errors: 0, aggregateSpeed: 0, finished: 0 };

  public isDeleteModalActive: boolean;
  public deleteError: string;
  public deleting: boolean;
  public deleteSelectAll: boolean;
  public deleteData: boolean;
  public deleteRdTorrent: boolean;
  public deleteLocalFiles: boolean;

  public isRetryModalActive: boolean;
  public retryError: string;
  public retrying: boolean;

  public isChangeSettingsModalActive: boolean;
  public changeSettingsError: string;
  public changingSettings: boolean;

  public updateSettingsDownloadClient: number;
  public updateSettingsHostDownloadAction: number;
  public updateSettingsCategory: string;
  public updateSettingsPriority: number;
  public updateSettingsDownloadRetryAttempts: number;
  public updateSettingsTorrentRetryAttempts: number;
  public updateSettingsDeleteOnError: number;
  public updateSettingsTorrentLifetime: number;

  public diskSpaceStatus: DiskSpaceStatus | null = null;
  public rateLimitStatus: RateLimitStatus | null = null;

  public isMobile = false;
  private mobileQuery: MediaQueryList;
  private mobileQueryListener: (e: MediaQueryListEvent) => void;

  ngOnInit(): void {
    this.mobileQuery = window.matchMedia('(max-width: 768px)');
    this.isMobile = this.mobileQuery.matches;
    this.mobileQueryListener = (e: MediaQueryListEvent) => {
      this.isMobile = e.matches;
    };
    this.mobileQuery.addEventListener('change', this.mobileQueryListener);

    try {
      const sp = localStorage.getItem('torrentTable.sortProperty');
      const sd = localStorage.getItem('torrentTable.sortDirection');
      if (sp) {
        this.sortProperty = sp;
      }
      if (sd === 'asc' || sd === 'desc') {
        this.sortDirection = sd as 'asc' | 'desc';
      }
    } catch (_) {
      // Ignore storage errors (e.g., disabled storage)
    }

    this.torrentService
      .getDiskSpaceStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (status) => {
          this.diskSpaceStatus = status;
        },
      });

    this.torrentService.diskSpaceStatus$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((status) => {
      this.diskSpaceStatus = status;
    });

    this.torrentService
      .getRateLimitStatus()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (status) => {
          this.rateLimitStatus = status;
        },
      });

    this.torrentService.rateLimitStatus$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((status) => {
      this.rateLimitStatus = status;
    });

    this.torrentService.update$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((result) => {
      this.setTorrents(result);
    });

    this.torrentService
      .getList()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.setTorrents(result);
        },
        error: (err) => {
          this.error = err.error;
        },
      });
  }

  public sort(property: string): void {
    if (this.sortProperty === property) {
      this.sortDirection = this.sortDirection === 'asc' ? 'desc' : 'asc';
    } else {
      this.sortProperty = property;
      this.sortDirection = 'desc';
    }

    try {
      localStorage.setItem('torrentTable.sortProperty', this.sortProperty);
      localStorage.setItem('torrentTable.sortDirection', this.sortDirection);
    } catch (_) {
      // Ignore storage errors
    }

    this.applySorting();
  }

  public sortGlyph(property: string): string {
    if (this.sortProperty !== property) {
      return '';
    }
    return this.sortDirection === 'asc' ? '▲' : '▼';
  }

  public setStatusFilter(kind: 'all' | StatusKind): void {
    this.statusFilter = kind;
    this.applyFiltering();
  }

  public onSearchChange(): void {
    this.applyFiltering();
  }

  public onCategoryFilterChange(): void {
    this.applyFiltering();
  }

  public clearFilters(): void {
    this.searchText = '';
    this.statusFilter = 'all';
    this.categoryFilter = '';
    this.applyFiltering();
  }

  public openTorrent(torrentId: string): void {
    this.router.navigate([`/torrent/${torrentId}`]);
  }

  public toggleDeleteSelectAll(event: Event) {
    const checked = (event.target as HTMLInputElement).checked;

    this.selectedTorrentIds = checked ? new Set(this.torrents.map((torrent) => torrent.torrentId)) : new Set<string>();
    this.syncSelectedTorrents();
  }

  public toggleSelect(torrentId: string) {
    if (this.selectedTorrentIds.has(torrentId)) {
      this.selectedTorrentIds.delete(torrentId);
    } else {
      this.selectedTorrentIds.add(torrentId);
    }

    this.syncSelectedTorrents();
  }

  public isSelected(torrentId: string): boolean {
    return this.selectedTorrentIds.has(torrentId);
  }

  public showDeleteModal(): void {
    this.deleteData = false;
    this.deleteRdTorrent = false;
    this.deleteLocalFiles = false;
    this.deleteError = null;

    this.isDeleteModalActive = true;
  }

  public deleteCancel(): void {
    this.isDeleteModalActive = false;
  }

  public deleteOk(): void {
    this.deleting = true;

    const calls: Observable<void>[] = [];

    this.selectedTorrents.forEach((torrentId) => {
      calls.push(this.torrentService.delete(torrentId, this.deleteData, this.deleteRdTorrent, this.deleteLocalFiles));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isDeleteModalActive = false;
        this.deleting = false;

        this.clearSelectedTorrents();
      },
      error: (err) => {
        this.deleteError = err.error;
        this.deleting = false;
      },
    });
  }

  public showRetryModal(): void {
    this.retryError = null;

    this.isRetryModalActive = true;
  }

  public retryCancel(): void {
    this.isRetryModalActive = false;
  }

  public retryOk(): void {
    this.retrying = true;

    const calls: Observable<void>[] = [];

    this.selectedTorrents.forEach((torrentId) => {
      calls.push(this.torrentService.retry(torrentId));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isRetryModalActive = false;
        this.retrying = false;

        this.clearSelectedTorrents();
      },
      error: (err) => {
        this.retryError = err.error;
        this.retrying = false;
      },
    });
  }

  public changeSettingsModal(): void {
    this.changeSettingsError = null;

    const selectedTorrents = this.getSelectedTorrentModels();

    this.updateSettingsDownloadClient = selectedTorrents.every(
      (m, _, arr) => m.downloadClient === arr[0].downloadClient,
    )
      ? selectedTorrents[0].downloadClient
      : null;
    this.updateSettingsHostDownloadAction = selectedTorrents.every(
      (m, _, arr) => m.hostDownloadAction === arr[0].hostDownloadAction,
    )
      ? selectedTorrents[0].hostDownloadAction
      : null;
    this.updateSettingsCategory = selectedTorrents.every((m, _, arr) => m.category === arr[0].category)
      ? selectedTorrents[0].category
      : null;
    this.updateSettingsPriority = selectedTorrents.every((m, _, arr) => m.priority === arr[0].priority)
      ? selectedTorrents[0].priority
      : null;
    this.updateSettingsDownloadRetryAttempts = selectedTorrents.every(
      (m, _, arr) => m.downloadRetryAttempts === arr[0].downloadRetryAttempts,
    )
      ? selectedTorrents[0].downloadRetryAttempts
      : null;
    this.updateSettingsTorrentRetryAttempts = selectedTorrents.every(
      (m, _, arr) => m.torrentRetryAttempts === arr[0].torrentRetryAttempts,
    )
      ? selectedTorrents[0].torrentRetryAttempts
      : null;
    this.updateSettingsDeleteOnError = selectedTorrents.every((m, _, arr) => m.deleteOnError === arr[0].deleteOnError)
      ? selectedTorrents[0].deleteOnError
      : null;
    this.updateSettingsTorrentLifetime = selectedTorrents.every((m, _, arr) => m.lifetime === arr[0].lifetime)
      ? selectedTorrents[0].lifetime
      : null;

    this.isChangeSettingsModalActive = true;
  }

  public changeSettingsCancel(): void {
    this.isChangeSettingsModalActive = false;
  }

  public changeSettingsOk(): void {
    this.changingSettings = true;

    const calls: Observable<void>[] = [];

    const selectedTorrents = this.getSelectedTorrentModels();

    selectedTorrents.forEach((torrent) => {
      if (this.updateSettingsDownloadClient != null) {
        torrent.downloadClient = this.updateSettingsDownloadClient;
      }
      if (this.updateSettingsHostDownloadAction != null) {
        torrent.hostDownloadAction = this.updateSettingsHostDownloadAction;
      }
      if (this.updateSettingsCategory != null) {
        torrent.category = this.updateSettingsCategory;
      }
      if (this.updateSettingsPriority != null) {
        torrent.priority = this.updateSettingsPriority;
      }
      if (this.updateSettingsDownloadRetryAttempts != null) {
        torrent.downloadRetryAttempts = this.updateSettingsDownloadRetryAttempts;
      }
      if (this.updateSettingsTorrentRetryAttempts != null) {
        torrent.torrentRetryAttempts = this.updateSettingsTorrentRetryAttempts;
      }
      if (this.updateSettingsDeleteOnError != null) {
        torrent.deleteOnError = this.updateSettingsDeleteOnError;
      }
      if (this.updateSettingsTorrentLifetime != null) {
        torrent.lifetime = this.updateSettingsTorrentLifetime;
      }

      calls.push(this.torrentService.update(torrent));
    });

    forkJoin(calls).subscribe({
      complete: () => {
        this.isChangeSettingsModalActive = false;
        this.changingSettings = false;

        this.clearSelectedTorrents();
      },
      error: (err) => {
        this.changeSettingsError = err.error;
        this.changingSettings = false;
      },
    });
  }
  toggleDeleteSelectAllOptions() {
    this.deleteData = this.deleteSelectAll;
    this.deleteRdTorrent = this.deleteSelectAll;
    this.deleteLocalFiles = this.deleteSelectAll;
  }

  updateDeleteSelectAll() {
    this.deleteSelectAll = this.deleteData && this.deleteRdTorrent && this.deleteLocalFiles;
  }

  ngOnDestroy(): void {
    this.mobileQuery?.removeEventListener('change', this.mobileQueryListener);
  }

  // ----------------------------------------------------------------------
  // Derived view state — KPIs, status kind mapping, filters
  // ----------------------------------------------------------------------

  public statusKind(torrent: Torrent): StatusKind {
    if (torrent.error) {
      return 'error';
    }
    // Per-download retry / permanent-failure detection. TorrentRunner now persists
    // the transient error before each retry, so the SignalR push contains it within
    // ~1 s of the failure. Surface that to the pill so the user sees the actual
    // state instead of a fake "downloading".
    const downloads = torrent.downloads ?? [];
    if (downloads.some((d) => d.error && d.completed != null)) {
      return 'error';
    }
    if (downloads.some((d) => d.error && d.completed == null)) {
      return 'retrying';
    }
    // Optimistic "sending" while the dequeue HTTP call to the provider is in flight.
    // Gate on: no provider id yet + queued/unset status + freshly added.
    if (!torrent.rdId && (torrent.rdStatus === 0 || torrent.rdStatus == null) && this.isFreshlyAdded(torrent)) {
      return 'sending';
    }
    switch (torrent.rdStatus) {
      case 0:
        return 'queued';
      case 1:
        return 'processing';
      case 2:
        return 'waiting';
      case 3:
        return 'downloading';
      case 4:
      case 5:
        return 'finished';
      case 99:
        return 'error';
      default:
        return 'queued';
    }
  }

  private isFreshlyAdded(torrent: Torrent): boolean {
    if (!torrent.added) {
      return false;
    }
    const addedMs = new Date(torrent.added).getTime();
    if (Number.isNaN(addedMs)) {
      return false;
    }
    return Date.now() - addedMs < OPTIMISTIC_SENDING_WINDOW_MS;
  }

  public progressPercent(torrent: Torrent): number {
    // Prefer the server-computed local-download progress when it's available. The server
    // computes it from the same per-download view used to build the status text, so the
    // bar and the pill can never disagree. Fall back to provider rdProgress when there
    // are no local downloads yet (e.g. while the torrent is still queued at the provider).
    const localProgress = torrent.localProgress;
    if (localProgress != null) {
      const n = Number(localProgress);
      if (!Number.isNaN(n)) {
        return Math.min(100, Math.max(0, n));
      }
    }

    const p = Number(torrent.rdProgress ?? 0);
    if (Number.isNaN(p)) return 0;
    return Math.min(100, Math.max(0, p));
  }

  public torrentSpeed(torrent: Torrent): number {
    // Prefer the server's aggregated rdSpeed; fall back to summing per-download speeds.
    if (typeof torrent.rdSpeed === 'number' && torrent.rdSpeed > 0) {
      return torrent.rdSpeed;
    }
    return (torrent.downloads ?? []).reduce((acc, d) => acc + (d.speed ?? 0), 0);
  }

  public typeBadge(torrent: Torrent): { label: string; cls: string } {
    return torrent.type === 1
      ? { label: 'NZB', cls: 'bg-violet-500/15 text-violet-300 ring-violet-500/30' }
      : { label: 'TOR', cls: 'bg-sky-500/15 text-sky-300 ring-sky-500/30' };
  }

  public statusLabel(torrent: Torrent): string {
    if (this.statusKind(torrent) === 'sending') {
      return 'Sending to provider…';
    }
    return torrent.statusText ?? getTorrentStatus(torrent);
  }

  // Surface the real torrent name from metadata. TorBox returns a magnet-URI-like
  // string in `rdName` when the magnet had no &dn= display name (we have seen
  // values like "magnet:?xt=urn:btih:<hash>" and the stripped form
  // "magnetxt=urnbtih<hash>"). In that case the actual file/directory name lives
  // in `torrent.files[0].path` once the provider has parsed the torrent.
  public getDisplayName(torrent: Torrent): string {
    const raw = (torrent.rdName ?? '').trim();
    if (raw && !this.looksLikeMagnetFallback(raw, torrent.hash)) {
      return raw;
    }

    const fromFiles = this.deriveNameFromFiles(torrent);
    if (fromFiles) {
      return fromFiles;
    }

    return raw || torrent.hash || '—';
  }

  private looksLikeMagnetFallback(name: string, hash?: string): boolean {
    const lower = name.toLowerCase();
    if (lower.startsWith('magnet:?xt=') || lower.startsWith('magnetxt=')) {
      return true;
    }
    if (hash && lower.includes(hash.toLowerCase())) {
      return true;
    }
    return false;
  }

  private deriveNameFromFiles(torrent: Torrent): string | null {
    const files = torrent.files;
    if (!files || files.length === 0) {
      return null;
    }
    const firstPath = (files[0].path ?? '').replace(/^\/+/, '').trim();
    if (!firstPath) {
      return null;
    }
    // Multi-file torrents typically share a top-level directory — surface that
    // as the display name. Single-file torrents fall through to the filename.
    const slashIndex = firstPath.indexOf('/');
    return slashIndex > 0 ? firstPath.substring(0, slashIndex) : firstPath;
  }

  public isLowSpace(): boolean {
    return !!this.diskSpaceStatus?.isPaused;
  }

  private setTorrents(torrents: Torrent[]): void {
    this.torrents = torrents;
    this.refreshDerivedFromTorrents();
    this.pruneSelectedTorrents();
    this.applySorting();
  }

  private applySorting(): void {
    this.sortedTorrents = sortItems(this.torrents, this.sortProperty, this.sortDirection, (torrent, field) => {
      switch (field) {
        case 'files.length':
          return torrent.filesCount ?? torrent.files?.length ?? 0;
        case 'downloads.length':
          return torrent.downloadsCount ?? torrent.downloads?.length ?? 0;
        case 'status':
          return torrent.statusText ?? getTorrentStatus(torrent);
        default:
          return getSortFieldValue(torrent, field);
      }
    });
    this.applyFiltering();
  }

  private applyFiltering(): void {
    const search = this.searchText.trim().toLowerCase();
    this.visibleTorrents = this.sortedTorrents.filter((t) => {
      if (search) {
        const haystack = `${t.rdName ?? ''} ${t.category ?? ''} ${t.hash ?? ''}`.toLowerCase();
        if (!haystack.includes(search)) {
          return false;
        }
      }
      if (this.statusFilter !== 'all') {
        // 'sending' is a sub-state of 'queued' (no provider id yet) — keep them in the same bucket.
        const kind = this.statusKind(t);
        const bucket = kind === 'sending' ? 'queued' : kind;
        if (bucket !== this.statusFilter) {
          return false;
        }
      }
      if (this.categoryFilter && (t.category ?? '') !== this.categoryFilter) {
        return false;
      }
      return true;
    });
  }

  private refreshDerivedFromTorrents(): void {
    const cats = new Set<string>();
    let active = 0;
    let queued = 0;
    let errors = 0;
    let finished = 0;
    let aggregateSpeed = 0;
    for (const t of this.torrents) {
      if (t.category) cats.add(t.category);
      const kind = this.statusKind(t);
      if (kind === 'downloading' || kind === 'processing' || kind === 'waiting' || kind === 'retrying') active++;
      if (kind === 'queued' || kind === 'sending') queued++;
      if (kind === 'error') errors++;
      if (kind === 'finished') finished++;
      aggregateSpeed += this.torrentSpeed(t);
    }
    this.availableCategories = Array.from(cats).sort((a, b) => a.localeCompare(b));
    this.kpis = { total: this.torrents.length, active, queued, errors, aggregateSpeed, finished };
  }

  private getSelectedTorrentModels(): Torrent[] {
    return this.torrents.filter((torrent) => this.selectedTorrentIds.has(torrent.torrentId));
  }

  private pruneSelectedTorrents(): void {
    const torrentIds = new Set(this.torrents.map((torrent) => torrent.torrentId));

    for (const torrentId of this.selectedTorrentIds) {
      if (!torrentIds.has(torrentId)) {
        this.selectedTorrentIds.delete(torrentId);
      }
    }

    this.syncSelectedTorrents();
  }

  private clearSelectedTorrents(): void {
    this.selectedTorrentIds.clear();
    this.syncSelectedTorrents();
  }

  private syncSelectedTorrents(): void {
    this.selectedTorrents = this.torrents
      .filter((torrent) => this.selectedTorrentIds.has(torrent.torrentId))
      .map((torrent) => torrent.torrentId);
  }
}
