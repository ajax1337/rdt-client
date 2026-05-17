import { Component, OnInit, inject } from '@angular/core';
import { Setting } from '../models/setting.model';
import { NgClass, KeyValuePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Nl2BrPipe } from '../nl2br.pipe';
import { FileSizePipe } from '../filesize.pipe';
import { SettingsService } from '../settings.service';

@Component({
  selector: 'app-settings',
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.scss'],
  imports: [NgClass, FormsModule, KeyValuePipe, Nl2BrPipe, FileSizePipe],
  standalone: true,
})
export class SettingsComponent implements OnInit {
  private settingsService = inject(SettingsService);

  public activeTab = 0;

  public tabs: Setting[] = [];

  public saving = false;
  public error: string;

  public testPathError: string;
  public testPathSuccess: boolean;

  public testDownloadSpeedError: string;
  public testDownloadSpeedSuccess: number;

  public testWriteSpeedError: string;
  public testWriteSpeedSuccess: number;

  public testAria2cConnectionError: string = null;
  public testAria2cConnectionSuccess: string = null;

  public canRegisterMagnetHandler = false;

  public categoryRows: {
    name: string;
    removeFromDashboard: boolean;
    removeFromProvider: boolean;
    removeLocalFiles: boolean;
    includeRegex: string;
    excludeRegex: string;
  }[] = [];

  // Symlink Downloader enum index. Kept in sync with RdtClient.Data.Enums.DownloadClient.
  private static readonly DOWNLOAD_CLIENT_SYMLINK = 2;

  public get isSymlinkMode(): boolean {
    const v = this.findSetting('DownloadClient:Client')?.value;
    if (v === null || v === undefined) {
      return false;
    }
    return Number(v) === SettingsComponent.DOWNLOAD_CLIENT_SYMLINK;
  }

  ngOnInit(): void {
    this.reset();
    this.canRegisterMagnetHandler = !!(window.isSecureContext && 'registerProtocolHandler' in navigator);
  }

  public reset(): void {
    this.settingsService.get().subscribe((settings) => {
      this.tabs = settings.filter((m) => m.key.indexOf(':') === -1);

      for (let tab of this.tabs) {
        tab.settings = settings.filter((m) => m.key.indexOf(`${tab.key}:`) > -1);
      }

      const categoriesSetting = this.findSetting('General:Categories');
      this.categoryRows = this.parseCategoriesValue(categoriesSetting?.value as string | null | undefined);
    });
  }

  public addCategoryRow(): void {
    this.categoryRows.push({
      name: '',
      removeFromDashboard: false,
      removeFromProvider: false,
      removeLocalFiles: false,
      includeRegex: '',
      excludeRegex: '',
    });
    this.syncCategories();
  }

  public removeCategoryRow(index: number): void {
    this.categoryRows.splice(index, 1);
    this.syncCategories();
  }

  public syncCategories(): void {
    const categoriesSetting = this.findSetting('General:Categories');
    if (!categoriesSetting) {
      return;
    }

    const cleaned = this.categoryRows
      .map((r) => {
        const include = (r.includeRegex ?? '').trim();
        const exclude = (r.excludeRegex ?? '').trim();
        // Omit blank regex keys from the JSON so the round-trip is stable and the
        // server-side resolver clearly sees "no per-category override".
        const row: {
          name: string;
          removeFromDashboard: boolean;
          removeFromProvider: boolean;
          removeLocalFiles: boolean;
          includeRegex?: string;
          excludeRegex?: string;
        } = {
          name: (r.name ?? '').trim(),
          removeFromDashboard: !!r.removeFromDashboard,
          removeFromProvider: !!r.removeFromProvider,
          removeLocalFiles: !!r.removeLocalFiles,
        };
        if (include.length > 0) row.includeRegex = include;
        if (exclude.length > 0) row.excludeRegex = exclude;
        return row;
      })
      .filter((r) => r.name.length > 0);

    categoriesSetting.value = JSON.stringify(cleaned);
  }

  private findSetting(key: string): Setting | undefined {
    for (const tab of this.tabs) {
      for (const s of tab.settings ?? []) {
        if (s.key === key) {
          return s;
        }
      }
    }
    return undefined;
  }

  private parseCategoriesValue(raw: string | null | undefined): {
    name: string;
    removeFromDashboard: boolean;
    removeFromProvider: boolean;
    removeLocalFiles: boolean;
    includeRegex: string;
    excludeRegex: string;
  }[] {
    if (!raw) {
      return [];
    }

    const trimmed = raw.trim();
    if (trimmed.startsWith('[')) {
      try {
        const parsed = JSON.parse(trimmed);
        if (Array.isArray(parsed)) {
          return parsed
            .filter(
              (c: { name?: unknown }) => c && typeof c.name === 'string' && (c.name as string).trim().length > 0,
            )
            .map(
              (c: {
                name: string;
                autoRemoveOnFinish?: unknown;
                removeFromDashboard?: unknown;
                removeFromProvider?: unknown;
                removeLocalFiles?: unknown;
                includeRegex?: unknown;
                excludeRegex?: unknown;
              }) => {
                const dashboard = !!c.removeFromDashboard;
                const provider = !!c.removeFromProvider;
                const local = !!c.removeLocalFiles;
                // Migrate the legacy single-flag form to "dashboard + provider" only if no
                // granular flag is set yet — newer choices always win.
                const legacy = !!c.autoRemoveOnFinish && !dashboard && !provider && !local;
                return {
                  name: String(c.name).trim(),
                  removeFromDashboard: legacy ? true : dashboard,
                  removeFromProvider: legacy ? true : provider,
                  removeLocalFiles: local,
                  includeRegex: typeof c.includeRegex === 'string' ? c.includeRegex : '',
                  excludeRegex: typeof c.excludeRegex === 'string' ? c.excludeRegex : '',
                };
              },
            );
        }
      } catch {
        // fall through to legacy comma-list parse
      }
    }

    return raw
      .split(',')
      .map((s) => s.trim())
      .filter((s) => s.length > 0)
      .map((name) => ({
        name,
        removeFromDashboard: false,
        removeFromProvider: false,
        removeLocalFiles: false,
        includeRegex: '',
        excludeRegex: '',
      }));
  }

  public ok(): void {
    this.saving = true;

    const settingsToSave = this.tabs.flatMap((m) => m.settings).filter((m) => m.type !== 'Object');

    this.settingsService.update(settingsToSave).subscribe({
      next: () =>
        setTimeout(() => {
          this.saving = false;
        }, 1000),
      error: (err) => {
        this.saving = false;
        this.error = err;
      },
    });
  }

  public testDownloadPath(): void {
    const settingDownloadPath = this.tabs
      .find((m) => m.key === 'DownloadClient')
      .settings.find((m) => m.key === 'DownloadClient:DownloadPath').value as string;

    this.saving = true;
    this.testPathError = null;
    this.testPathSuccess = false;

    this.settingsService.testPath(settingDownloadPath).subscribe({
      next: () => {
        this.saving = false;
        this.testPathSuccess = true;
      },
      error: (err) => {
        this.testPathError = err.error;
        this.saving = false;
      },
    });
  }

  public testDownloadSpeed(): void {
    this.saving = true;
    this.testDownloadSpeedError = null;
    this.testDownloadSpeedSuccess = 0;

    this.settingsService.testDownloadSpeed().subscribe({
      next: (result) => {
        this.saving = false;
        this.testDownloadSpeedSuccess = result;
      },
      error: (err) => {
        this.testDownloadSpeedError = err.error;
        this.saving = false;
      },
    });
  }
  public testWriteSpeed(): void {
    this.saving = true;
    this.testWriteSpeedError = null;
    this.testWriteSpeedSuccess = 0;

    this.settingsService.testWriteSpeed().subscribe({
      next: (result) => {
        this.saving = false;
        this.testWriteSpeedSuccess = result;
      },
      error: (err) => {
        this.testWriteSpeedError = err.error;
        this.saving = false;
      },
    });
  }

  public testAria2cConnection(): void {
    const settingAria2cUrl = this.tabs
      .find((m) => m.key === 'DownloadClient')
      .settings.find((m) => m.key === 'DownloadClient:Aria2cUrl').value as string;
    const settingAria2cSecret = this.tabs
      .find((m) => m.key === 'DownloadClient')
      .settings.find((m) => m.key === 'DownloadClient:Aria2cSecret').value as string;

    this.saving = true;
    this.testAria2cConnectionError = null;
    this.testAria2cConnectionSuccess = null;

    this.settingsService.testAria2cConnection(settingAria2cUrl, settingAria2cSecret).subscribe({
      next: (result) => {
        this.saving = false;
        this.testAria2cConnectionSuccess = result.version;
      },
      error: (err) => {
        this.testAria2cConnectionError = err.error;
        this.saving = false;
      },
    });
  }

  public registerMagnetHandler(): void {
    try {
      navigator.registerProtocolHandler('magnet', `${window.location.origin}/add?magnet=%s`);
      alert(
        'Success! Your browser will now prompt you to confirm and add the client as the default handler for magnet links.',
      );
    } catch (error) {
      alert('Magnet link registration failed.');
    }
  }
}
