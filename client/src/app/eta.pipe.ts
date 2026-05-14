import { Pipe, PipeTransform } from '@angular/core';
import { Torrent } from './models/torrent.model';

/**
 * Formats the estimated time remaining for a torrent's local download.
 *
 * Uses rdSize, rdProgress (0-100), and rdSpeed (bytes/sec).
 * Returns:
 *   - "—" if the torrent isn't downloading (no speed, completed, missing data)
 *   - "5s" / "2m 15s" / "1h 23m" otherwise
 */
@Pipe({ name: 'eta', standalone: true })
export class EtaPipe implements PipeTransform {
  transform(torrent: Torrent | null | undefined): string {
    if (!torrent) {
      return '—';
    }

    const size = Number(torrent.rdSize ?? 0);
    const progress = Number(torrent.rdProgress ?? 0);
    const speed = Number(torrent.rdSpeed ?? 0);

    if (size <= 0 || speed <= 0 || progress >= 100) {
      return '—';
    }

    const remainingBytes = size * (1 - progress / 100);
    if (remainingBytes <= 0) {
      return '—';
    }

    const seconds = Math.max(0, Math.round(remainingBytes / speed));
    return EtaPipe.formatSeconds(seconds);
  }

  private static formatSeconds(s: number): string {
    if (s < 60) {
      return `${s}s`;
    }
    if (s < 3600) {
      const m = Math.floor(s / 60);
      const sec = s % 60;
      return sec === 0 ? `${m}m` : `${m}m ${sec}s`;
    }
    if (s < 86400) {
      const h = Math.floor(s / 3600);
      const m = Math.floor((s % 3600) / 60);
      return m === 0 ? `${h}h` : `${h}h ${m}m`;
    }
    const d = Math.floor(s / 86400);
    const h = Math.floor((s % 86400) / 3600);
    return h === 0 ? `${d}d` : `${d}d ${h}h`;
  }
}
