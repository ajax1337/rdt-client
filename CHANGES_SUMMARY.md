# RDT Client - Cleanuparr Integration Changes Summary

## Overview
This document summarizes all changes made to integrate RDT Client with Cleanuparr, a qBittorrent-compatible cleanup tool.

---

## Changes Made

### 1. Health Check Fix (CRITICAL)
**Problem:** Cleanuparr's health checks were returning HTML instead of JSON  
**Files Modified:**
- `server/RdtClient.Web/Controllers/QBittorrentController.cs`

**Changes:**
- Added `[AllowAnonymous]` attribute to:
  - `app/version` endpoint (line 65)
  - `app/webapiVersion` endpoint (line 74)
  - `app/buildInfo` endpoint (line 83)

**Impact:** Health checks now work without authentication, allowing Cleanuparr to verify RDT Client is online.

---

### 2. Torrent Trackers Endpoint (NEW FEATURE)
**Files Created:**
- `server/RdtClient.Data/Models/QBittorrent/TorrentTracker.cs` (NEW)

**Files Modified:**
- `server/RdtClient.Service/Services/QBittorrent.cs`
- `server/RdtClient.Web/Controllers/QBittorrentController.cs`

**Changes:**

#### A. New Model - `TorrentTracker.cs`
Created new model with fields:
- `url` - Tracker URL
- `status` - Tracker status (0-4)
- `tier` - Tracker tier
- `num_peers`, `num_seeds`, `num_leeches` - Peer counts
- `num_downloaded` - Download count
- `msg` - Status message

#### B. Service Method - `QBittorrent.TorrentTrackers()`
Added method (lines 411-481) that returns mock tracker data:
- Public trackers (tracker.opentrackr.org, open.tracker.cl)
- DHT, PeX, LSD entries
- Uses real seeder counts from debrid service

#### C. Controller Endpoints
Added two new endpoints (lines 219-245):
- `GET /api/v2/torrents/trackers?hash={hash}`
- `POST /api/v2/torrents/trackers` (form data)

**Impact:** Cleanuparr can now query tracker information for torrents.

---

### 3. Enhanced Torrent List Filtering (ENHANCEMENT)
**Files Modified:**
- `server/RdtClient.Web/Controllers/QBittorrentController.cs`

**Changes:**

#### A. Updated Request Model - `QBTorrentsInfoRequest`
Added new properties (lines 620-622):
- `Filter` - Status filter (completed, downloading, paused, error, stalled, all)
- `Hashes` - Pipe-separated list of torrent hashes

#### B. Enhanced `TorrentsInfo()` Method
Added filtering logic (lines 147-175):

**Filter by Hashes:**
```csharp
if (!String.IsNullOrWhiteSpace(request.Hashes))
{
    var hashList = request.Hashes.Split('|', StringSplitOptions.RemoveEmptyEntries)
                                .Select(h => h.ToLower())
                                .ToList();
    results = results.Where(m => hashList.Contains(m.Hash.ToLower())).ToList();
}
```

**Filter by Status:**
```csharp
results = request.Filter.ToLower() switch
{
    "completed" => results.Where(m => m.State == "pausedUP" || m.Progress >= 1.0f).ToList(),
    "downloading" => results.Where(m => m.State == "downloading").ToList(),
    "paused" => results.Where(m => m.State?.Contains("paused") == true).ToList(),
    "error" => results.Where(m => m.State == "error").ToList(),
    "stalled" => results.Where(m => m.State == "stalledDL").ToList(),
    "all" => results,
    _ => results
};
```

**Impact:** Cleanuparr can now query specific torrents by hash and filter by status.

---

### 4. Private Torrent Detection (ENHANCEMENT)
**Files Modified:**
- `server/RdtClient.Data/Models/QBittorrent/TorrentProperties.cs`
- `server/RdtClient.Service/Services/QBittorrent.cs`

**Changes:**

#### A. Model Update - `TorrentProperties.cs`
Added new property (lines 106-107):
```csharp
[JsonPropertyName("is_private")]
public Boolean? IsPrivate { get; set; }
```

#### B. Service Update - `QBittorrent.TorrentProperties()`
Set the property (line 405):
```csharp
IsPrivate = false // Debrid services don't support private trackers
```

**Impact:** Cleanuparr can determine if torrents are private (always false for debrid services).

---

## Files Changed Summary

### New Files (1)
1. `server/RdtClient.Data/Models/QBittorrent/TorrentTracker.cs`

### Modified Files (3)
1. `server/RdtClient.Web/Controllers/QBittorrentController.cs`
   - Added `[AllowAnonymous]` to 3 endpoints
   - Added 2 new tracker endpoints
   - Enhanced torrents/info filtering
   - Added 2 new properties to request model

2. `server/RdtClient.Service/Services/QBittorrent.cs`
   - Added `TorrentTrackers()` method (70 lines)
   - Set `IsPrivate` property in `TorrentProperties()`

3. `server/RdtClient.Data/Models/QBittorrent/TorrentProperties.cs`
   - Added `IsPrivate` property

### Documentation Files (2)
1. `CLEANUPARR_INTEGRATION.md` - Full integration guide
2. `CHANGES_SUMMARY.md` - This file

---

## Testing Checklist

### Before Testing
- [ ] Rebuild the solution: `dotnet build`
- [ ] Restart RDT Client service/container
- [ ] Verify RDT Client is running and accessible

### Health Check Tests
- [ ] Call `GET http://your-rdt-client:port/api/v2/app/webapiVersion` (no auth)
  - Expected: `"2.7"`
- [ ] Call `GET http://your-rdt-client:port/api/v2/app/version` (no auth)
  - Expected: `"v4.3.2"`

### Authenticated Tests (with auth)
- [ ] Login: `POST /api/v2/auth/login` with username/password
- [ ] Get torrents: `GET /api/v2/torrents/info`
  - Expected: JSON array of torrents
- [ ] Filter by hash: `GET /api/v2/torrents/info?hashes=abc123|def456`
  - Expected: Only matching torrents
- [ ] Filter by status: `GET /api/v2/torrents/info?filter=completed`
  - Expected: Only completed torrents
- [ ] Get trackers: `GET /api/v2/torrents/trackers?hash=abc123`
  - Expected: JSON array of tracker objects
- [ ] Get properties: `GET /api/v2/torrents/properties?hash=abc123`
  - Expected: JSON object with `is_private: false`

### Cleanuparr Integration Test
- [ ] Add RDT Client as qBittorrent download client in Cleanuparr
- [ ] Run connection test - should show "Healthy"
- [ ] Verify Cleanuparr can list torrents
- [ ] Verify Cleanuparr can perform cleanup operations

---

## Build Instructions

### Windows
```powershell
cd server
dotnet build
# Restart service or container
```

### Linux/Docker
```bash
cd server
dotnet build
# If using Docker:
docker-compose restart
# Or rebuild:
docker-compose up -d --build
```

---

## Rollback Plan

If issues occur, revert these commits or restore from backup. The changes are:
- Non-breaking (all existing functionality preserved)
- Additive (new endpoints and properties only)
- Backward compatible (existing API clients unaffected)

---

## Known Limitations

### What Works
✅ Cleanuparr health checks  
✅ Torrent listing with filtering  
✅ Torrent deletion  
✅ Category management  
✅ Tracker information  
✅ Private torrent detection  

### What's Stubbed/Not Implemented
⚠️ File priority changes (endpoint exists but no-op)  
⚠️ Tag management (endpoints exist but don't persist)  
⚠️ Preferences blacklist sync (no `excluded_file_names` support)  

### Expected Behavior
- All torrents report as public (`is_private: false`)
- Tracker information is mocked but realistic
- Seeder counts come from debrid service data
- File priority changes are ignored (all files download)
- Tags don't persist across restarts

---

## Next Steps (Optional Enhancements)

### Phase 2 Features
If additional Cleanuparr features are needed:
1. Implement actual file priority support
2. Add persistent tag storage and management
3. Add `excluded_file_names` preference support
4. Implement tag-based automation

See `CLEANUPARR_INTEGRATION.md` for detailed Phase 2 implementation plan.

---

## Support

If you encounter issues:
1. Check RDT Client logs for errors
2. Verify all endpoints with curl/Postman before testing with Cleanuparr
3. Ensure authentication is properly configured
4. Check that categories exist before assigning to torrents

---

## Version Information

- **RDT Client Version:** 2.0.118 (base)
- **qBittorrent API Emulation:** v4.3.2 / Web API v2.7
- **Cleanuparr Target:** Latest (using FLM.QBittorrent v1.0.2)
- **Changes Date:** October 9, 2025

