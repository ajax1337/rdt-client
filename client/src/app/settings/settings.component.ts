import { Component, OnInit, inject } from '@angular/core';
import { Setting } from '../models/setting.model';
import { NgClass, KeyValuePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Nl2BrPipe } from '../nl2br.pipe';
import { FileSizePipe } from '../filesize.pipe';
import { SettingsService } from '../settings.service';

// Pre-fill recipe for the well-known "video" category names. When a category with one
// of these names is opened in Settings and has no persisted IncludeRegex (or its
// IncludeRegex is exactly the regex this list serializes to), the chip UI surfaces
// these extensions. The user can add or remove chips and the regex is regenerated on
// save. Any extension you can pull off a torrent file index counts here.
const VIDEO_CATEGORY_NAMES_LOWER = new Set(['movies', 'tv shows', 'other videos']);
const VIDEO_DEFAULT_EXTENSIONS = ['mkv', 'mp4', 'avi', 'mov', 'm4v', 'webm', 'mpg', 'mpeg', 'm2ts', 'ts', 'vob', 'wmv', 'flv', '3gp'];

// Pattern emitted by extensionsToRegex below. Used by regexToExtensions to round-trip
// a persisted IncludeRegex back into a chip list. Anything more elaborate (paths,
// anchors, custom groups) won't parse — the chips will start empty and a save will
// overwrite the regex. We accept that trade-off in exchange for a simpler UI; users
// with complex requirements can keep the global Provider.Default.IncludeRegex.
const EXTENSION_LIST_REGEX_PATTERN = /^\\\.\(([a-z0-9|?]+)\)\$$/i;

// What we accept inside a chip. Strict on purpose — the regex this becomes is a
// straight \.(a|b|c)$ so anything other than alphanumerics would either break the
// regex or change its meaning.
const VALID_EXTENSION_CHIP = /^[a-z0-9]+$/i;

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
    extensions: string[];
    extensionInput: string;
    // Persisted ExcludeRegex is preserved across save cycles even though the chip
    // UI doesn't edit it. If the user has set ExcludeRegex by hand-editing the
    // categories JSON, the chip UI will not blow it away.
    excludeRegex: string;
    // Becomes true on parse when the persisted IncludeRegex doesn't match the
    // simple \\.(ext1|ext2)$ pattern. The UI then shows a small note so the user
    // knows their regex will be replaced by the chip list on save.
    hasComplexIncludeRegex: boolean;
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
      // parseCategoriesValue may have injected a suggested IncludeRegex for the
      // well-known video categories. Push that back into categoriesSetting.value so
      // a Save click commits the suggestion (no other UI interaction needed).
      this.syncCategories();
    });
  }

  public addCategoryRow(): void {
    this.categoryRows.push({
      name: '',
      removeFromDashboard: false,
      removeFromProvider: false,
      removeLocalFiles: false,
      extensions: [],
      extensionInput: '',
      excludeRegex: '',
      hasComplexIncludeRegex: false,
    });
    this.syncCategories();
  }

  public removeCategoryRow(index: number): void {
    this.categoryRows.splice(index, 1);
    this.syncCategories();
  }

  /**
   * Add the extension typed into the row's input. Normalises (strips leading dot,
   * lowercases, trims), validates (alphanumerics only), and dedupes. No-op if the
   * input is blank or invalid.
   */
  public addExtension(rowIndex: number): void {
    const row = this.categoryRows[rowIndex];
    if (!row) return;

    const raw = (row.extensionInput ?? '').trim().replace(/^\.+/, '').toLowerCase();
    if (raw.length === 0) return;
    if (!VALID_EXTENSION_CHIP.test(raw)) {
      // Refuse silently — the input keeps its content so the user can fix it.
      return;
    }
    if (row.extensions.includes(raw)) {
      row.extensionInput = '';
      return;
    }

    row.extensions = [...row.extensions, raw];
    row.extensionInput = '';
    // Adding a chip overwrites whatever complex regex was there; the warning is
    // no longer accurate.
    row.hasComplexIncludeRegex = false;
    this.syncCategories();
  }

  public removeExtension(rowIndex: number, extIndex: number): void {
    const row = this.categoryRows[rowIndex];
    if (!row) return;
    if (extIndex < 0 || extIndex >= row.extensions.length) return;

    row.extensions = row.extensions.filter((_, i) => i !== extIndex);
    row.hasComplexIncludeRegex = false;
    this.syncCategories();
  }

  public syncCategories(): void {
    const categoriesSetting = this.findSetting('General:Categories');
    if (!categoriesSetting) {
      return;
    }

    const cleaned = this.categoryRows
      .map((r) => {
        const include = this.extensionsToRegex(r.extensions);
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

  /**
   * Build a \.(ext1|ext2|...)$ regex from a chip list. Returns empty string when
   * the list is empty — caller skips writing the key so the server-side resolver
   * falls back to the source default.
   */
  private extensionsToRegex(extensions: string[]): string {
    const clean = (extensions ?? [])
      .map((e) => e.trim().toLowerCase())
      .filter((e) => e.length > 0 && VALID_EXTENSION_CHIP.test(e));
    if (clean.length === 0) return '';
    return `\\.(${clean.join('|')})$`;
  }

  /**
   * Try to recover a chip list from a persisted IncludeRegex. Returns the
   * extracted extensions plus a flag indicating whether the regex matched the
   * simple pattern we emit. If it didn't, the regex is complex (hand-edited or
   * left over from an earlier UI) and the chip list starts empty — the UI shows
   * a warning so the user knows a save will replace it.
   */
  private regexToExtensions(includeRegex: string | null | undefined): {
    extensions: string[];
    isComplex: boolean;
  } {
    const raw = (includeRegex ?? '').trim();
    if (raw.length === 0) {
      return { extensions: [], isComplex: false };
    }

    const match = raw.match(EXTENSION_LIST_REGEX_PATTERN);
    if (!match) {
      return { extensions: [], isComplex: true };
    }

    const inner = match[1];
    const extensions = inner
      .split('|')
      .map((e) => e.trim().toLowerCase())
      .filter((e) => e.length > 0 && VALID_EXTENSION_CHIP.test(e));

    // Dedupe while preserving order.
    const seen = new Set<string>();
    const deduped: string[] = [];
    for (const e of extensions) {
      if (!seen.has(e)) {
        seen.add(e);
        deduped.push(e);
      }
    }

    return { extensions: deduped, isComplex: false };
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
    extensions: string[];
    extensionInput: string;
    excludeRegex: string;
    hasComplexIncludeRegex: boolean;
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
                const name = String(c.name).trim();
                const dashboard = !!c.removeFromDashboard;
                const provider = !!c.removeFromProvider;
                const local = !!c.removeLocalFiles;
                // Migrate the legacy single-flag form to "dashboard + provider" only if no
                // granular flag is set yet — newer choices always win.
                const legacy = !!c.autoRemoveOnFinish && !dashboard && !provider && !local;
                const persistedInclude = typeof c.includeRegex === 'string' ? c.includeRegex : '';
                const persistedExclude = typeof c.excludeRegex === 'string' ? c.excludeRegex : '';

                // Try to recover a chip list from the persisted regex. If it doesn't
                // match our simple pattern we still want to show the chip UI so the
                // user can opt into it — but we flag it so the UI can warn that a save
                // will overwrite the existing regex.
                const recovered = this.regexToExtensions(persistedInclude);
                let extensions = recovered.extensions;
                const isComplex = recovered.isComplex;

                // First-read suggestion: when one of the well-known video category
                // names has no persisted IncludeRegex (and therefore no recovered
                // extensions), pre-populate with the default video extension list so
                // a single Save click commits the filter.
                if (extensions.length === 0 && !isComplex && VIDEO_CATEGORY_NAMES_LOWER.has(name.toLowerCase())) {
                  extensions = [...VIDEO_DEFAULT_EXTENSIONS];
                }

                return {
                  name,
                  removeFromDashboard: legacy ? true : dashboard,
                  removeFromProvider: legacy ? true : provider,
                  removeLocalFiles: local,
                  extensions,
                  extensionInput: '',
                  excludeRegex: persistedExclude,
                  hasComplexIncludeRegex: isComplex,
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
        extensions: VIDEO_CATEGORY_NAMES_LOWER.has(name.toLowerCase()) ? [...VIDEO_DEFAULT_EXTENSIONS] : [],
        extensionInput: '',
        excludeRegex: '',
        hasComplexIncludeRegex: false,
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
