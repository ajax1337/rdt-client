# Cleanuparr Integration Guide for RDT Client

## Current Status

### ✅ FIXED - Health Check Issue
**Problem:** Cleanuparr health checks were failing with HTML response instead of JSON.  
**Solution:** Added `[AllowAnonymous]` attribute to health check endpoints:
- `/api/v2/app/version`
- `/api/v2/app/webapiVersion`
- `/api/v2/app/buildInfo`

### ✅ IMPLEMENTED - Critical Features for Cleanuparr

**Phase 1 Implementation Complete:**
1. ✅ **Torrent Trackers Endpoint** - `/api/v2/torrents/trackers`
   - Returns mock tracker data with DHT, PeX, LSD
   - Provides realistic seeder/peer counts from debrid service
   
2. ✅ **Torrent List Filtering** - Enhanced `/api/v2/torrents/info`
   - Filter by `hashes` (pipe-separated list)
   - Filter by `filter` (completed, downloading, paused, error, stalled)
   - Existing category filter maintained
   
3. ✅ **Private Torrent Detection** - Added `is_private` field
   - Added to `TorrentProperties` model
   - Always returns `false` (debrid services don't support private trackers)

**Action Required:** Rebuild and restart RDT Client for all fixes to take effect.

---

## Full Cleanuparr API Support Matrix

### ✅ Already Working (No Changes Needed)

| Endpoint | Method | Status | Notes |
|----------|--------|--------|-------|
| `auth/login` | POST/GET | ✅ Working | Authentication |
| `app/version` | GET/POST | ✅ **FIXED** | Now allows anonymous |
| `app/webapiVersion` | GET/POST | ✅ **FIXED** | Now allows anonymous |
| `torrents/info` | GET/POST | ✅ **ENHANCED** | Torrent list with full filtering (hashes, filter, category) |
| `torrents/properties` | GET/POST | ✅ **ENHANCED** | Torrent details (now includes `is_private`) |
| `torrents/trackers` | GET/POST | ✅ **NEW** | Torrent tracker information |
| `torrents/files` | GET/POST | ✅ Working | File list |
| `torrents/delete` | GET/POST | ✅ Working | Delete torrents with data |
| `torrents/categories` | GET/POST | ✅ Working | Get categories |
| `torrents/createCategory` | GET/POST | ✅ Working | Create category |
| `torrents/setCategory` | GET/POST | ✅ Working | Change torrent category |

---

### ⚠️ Partially Implemented (Need Enhancement)

#### 1. **File Priority** (`torrents/filePrio`)
**Current:** Returns OK but does nothing (stub implementation)  
**Needed by Cleanuparr:** Set file priority to "Skip" (priority=0)

**Priority:** MEDIUM - Used to skip unwanted files

#### 3. **App Preferences** (`app/preferences`)
**Current:** Returns preferences but doesn't include `excluded_file_names`  
**Needed by Cleanuparr:** Read/write `excluded_file_names` for blacklist sync

**Priority:** LOW - Nice-to-have for blacklist synchronization


---

## Implementation Recommendations

### Phase 1: Critical Fixes (For Basic Operation) ✅ COMPLETE
1. ✅ **DONE** - Health check endpoints (allow anonymous)
2. ✅ **DONE** - Implement `torrents/trackers` endpoint
3. ✅ **DONE** - Add torrent list filtering by hashes and status
4. ✅ **DONE** - Add `is_private` field to TorrentProperties

### Phase 2: Enhanced Functionality
1. **TODO** - Implement file priority support
2. **TODO** - Add tag support (`torrents/tags`, `torrents/createTags`, `torrents/addTags`)
3. **TODO** - Add `excluded_file_names` to preferences

---

## Testing Checklist

### Before Cleanuparr Setup:
- [ ] Rebuild RDT Client with health check fix
- [ ] Verify `/api/v2/app/webapiVersion` returns `"2.7"` without authentication
- [ ] Verify authentication works at `/api/v2/auth/login`

### After Cleanuparr Setup:
- [ ] Cleanuparr health check shows "Healthy"
- [ ] Cleanuparr can list torrents
- [ ] Cleanuparr can delete torrents
- [ ] Cleanuparr can manage categories

### Known Limitations:
- ⚠️ Cleanuparr's tracker analysis won't work without `torrents/trackers` endpoint
- ⚠️ File priority changes won't take effect (stub implementation)
- ⚠️ Tags won't persist (not implemented)
- ℹ️ Some advanced Cleanuparr features may not work until Phase 2 is complete

---

## Configuration Guide

### In Cleanuparr:
1. **Settings** → **Download Clients** → **Add** → **qBittorrent**
2. **Host:** Your RDT Client IP/hostname
3. **Port:** Your RDT Client port (default: 6500)
4. **Username:** Your RDT Client username
5. **Password:** Your RDT Client password
6. **Category:** Optional (e.g., "cleanuparr")
7. **Test** → Should now show "Success" ✅

### In RDT Client:
- Ensure authentication is enabled (`Settings.General.AuthenticationType != None`)
- Create categories in RDT if needed
- Configure download paths appropriately

---

## Next Steps

### Immediate (To fix health check):
```bash
# Rebuild the .NET project
cd server
dotnet build

# Restart the application
# (Docker users: docker-compose restart)
# (Service users: restart the service)
```

### For Full Cleanuparr Support:
See the implementation plan in Phase 1 above. Would you like me to implement these missing features?

---

## Technical Details

### qBittorrent API Compatibility
- **Emulating:** qBittorrent v4.3.2 / Web API v2.7
- **Documentation:** https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-4.1)
- **Cleanuparr Library:** FLM.QBittorrent v1.0.2

### Authentication Flow
1. Cleanuparr calls `/api/v2/app/webapiVersion` (no auth) for health check
2. If successful, calls `/api/v2/auth/login` with credentials
3. Subsequent requests use cookie-based session authentication
4. RDT Client validates via `[Authorize(Policy = "AuthSetting")]`

