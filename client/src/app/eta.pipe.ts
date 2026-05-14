import { Pipe, PipeTransform } from '@angular/core';
import { Torrent } from './models/torrent.model';

/**
 * Formats the estimated time remaining for a torrent's local (host) download.
 *
 * Sums speed and remaining bytes across the torrent's in-flight Download rows
 * (the same source used by the Status pipe), so the ETA reflects what aria2c
 * is actually doing — not the provider-side rdSpeed (which is 0 for cached
 * torrents). Falls back to rdSize/rdProgress/rdSpeed only if no download is
 * actively in progress (e.g. while still cached on provider but not yet
 * fetched locally).
 *
 * Returns:
 *   - "—" when no meaningful ETA can be computed (no active download, no
 *     speed, already complete)
 *   - "5s" / "2m 15s" / "1h 23m" / "1d 5h" otherwise
 */
@Pipe({ name: 'eta', standalone: true })
export class EtaPipe implements PipeTransform {
  transform(torrent: Torrent | null | undefined): string {
    if (!torrent) {
      return '—';
    }

    // Prefer per-Download stats — this is what aria2c actually reports.
    const downloads = torrent.downloads ?? [];
    let speedSum = 0;
    let remainingSum = 0;

    for (const dl of downloads) {
      if (dl.downloadStarted && !dl.downloadFinished && (dl.bytesDone ?? 0) > 0) {
        const speed = Number(dl.speed ?? 0);
        const done = Number(dl.bytesDone ?? 0);
        const total = Number(dl.bytesTotal ?? 0);
        if (speed > 0 && total > done) {
          speedSum += speed;
          remainingSum += total - done;
        }
      }
    }

    if (speedSum > 0 && remainingSum > 0) {
      return EtaPipe.formatSeconds(Math.max(0, Math.round(remainingSum / speedSum)));
    }

    // Fallback: provider-side speed (rare path — typically rdSpeed is 0 for
    // cached debrid torrents).
    const size = Number(torrent.rdSize ?? 0);
    const progress = Number(torrent.rdProgress ?? 0);
    const speed = Number(torrent.rdSpeed ?? 0);

    if (size > 0 && speed > 0 && progress < 100) {
      const remaining = size * (1 - progress / 100);
      if (remaining > 0) {
        return EtaPipe.formatSeconds(Math.max(0, Math.round(remaining / speed)));
      }
    }

    return '—';
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
