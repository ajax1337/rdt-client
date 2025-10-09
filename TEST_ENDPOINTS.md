# Quick Endpoint Testing Reference

## Base URL
Replace `YOUR_HOST` and `YOUR_PORT` with your RDT Client details:
```
http://YOUR_HOST:YOUR_PORT/api/v2
```

---

## 1. Health Check (No Auth Required)

### Get API Version
```bash
curl http://localhost:6500/api/v2/app/webapiVersion
```
**Expected:** `"2.7"`

### Get App Version
```bash
curl http://localhost:6500/api/v2/app/version
```
**Expected:** `"v4.3.2"`

### Get Build Info
```bash
curl http://localhost:6500/api/v2/app/buildInfo
```
**Expected:** JSON object with build details

---

## 2. Authentication

### Login
```bash
curl -X POST http://localhost:6500/api/v2/auth/login \
  -d "username=YOUR_USERNAME&password=YOUR_PASSWORD"
```
**Expected:** `"Ok."` + session cookie

### Save Cookie for Next Requests
```bash
curl -X POST http://localhost:6500/api/v2/auth/login \
  -d "username=YOUR_USERNAME&password=YOUR_PASSWORD" \
  -c cookies.txt
```

---

## 3. Torrent List (Authenticated)

### Get All Torrents
```bash
curl http://localhost:6500/api/v2/torrents/info \
  -b cookies.txt
```

### Filter by Category
```bash
curl "http://localhost:6500/api/v2/torrents/info?category=movies" \
  -b cookies.txt
```

### Filter by Status (Completed)
```bash
curl "http://localhost:6500/api/v2/torrents/info?filter=completed" \
  -b cookies.txt
```

### Filter by Hashes
```bash
curl "http://localhost:6500/api/v2/torrents/info?hashes=abc123|def456" \
  -b cookies.txt
```

### Combined Filters
```bash
curl "http://localhost:6500/api/v2/torrents/info?filter=completed&category=movies" \
  -b cookies.txt
```

---

## 4. Torrent Details (Authenticated)

### Get Torrent Properties
```bash
curl "http://localhost:6500/api/v2/torrents/properties?hash=YOUR_HASH" \
  -b cookies.txt
```
**Look for:** `"is_private": false`

### Get Torrent Files
```bash
curl "http://localhost:6500/api/v2/torrents/files?hash=YOUR_HASH" \
  -b cookies.txt
```

### Get Torrent Trackers (NEW)
```bash
curl "http://localhost:6500/api/v2/torrents/trackers?hash=YOUR_HASH" \
  -b cookies.txt
```
**Expected:** Array of tracker objects with DHT, PeX, LSD

---

## 5. Torrent Management (Authenticated)

### Delete Torrent (Without Data)
```bash
curl -X POST "http://localhost:6500/api/v2/torrents/delete" \
  -d "hashes=YOUR_HASH&deleteFiles=false" \
  -b cookies.txt
```

### Delete Torrent (With Data)
```bash
curl -X POST "http://localhost:6500/api/v2/torrents/delete" \
  -d "hashes=YOUR_HASH&deleteFiles=true" \
  -b cookies.txt
```

### Set Category
```bash
curl -X POST "http://localhost:6500/api/v2/torrents/setCategory" \
  -d "hashes=YOUR_HASH&category=movies" \
  -b cookies.txt
```

---

## 6. Categories (Authenticated)

### List Categories
```bash
curl "http://localhost:6500/api/v2/torrents/categories" \
  -b cookies.txt
```

### Create Category
```bash
curl -X POST "http://localhost:6500/api/v2/torrents/createCategory" \
  -d "category=movies" \
  -b cookies.txt
```

---

## PowerShell Examples (Windows)

### Health Check
```powershell
Invoke-RestMethod -Uri "http://localhost:6500/api/v2/app/webapiVersion"
```

### Login
```powershell
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$body = @{
    username = "YOUR_USERNAME"
    password = "YOUR_PASSWORD"
}
Invoke-RestMethod -Uri "http://localhost:6500/api/v2/auth/login" `
    -Method Post -Body $body -WebSession $session
```

### Get Torrents
```powershell
Invoke-RestMethod -Uri "http://localhost:6500/api/v2/torrents/info" `
    -WebSession $session
```

### Get Trackers
```powershell
Invoke-RestMethod -Uri "http://localhost:6500/api/v2/torrents/trackers?hash=YOUR_HASH" `
    -WebSession $session
```

---

## Cleanuparr Configuration

### Add Download Client
1. Open Cleanuparr → **Settings** → **Download Clients**
2. Click **+** → Select **qBittorrent**
3. Enter details:
   - **Name:** RDT Client
   - **Host:** `localhost` (or your RDT IP)
   - **Port:** `6500` (or your RDT port)
   - **Username:** Your RDT username
   - **Password:** Your RDT password
   - **Category:** Optional (e.g., `cleanuparr`)
4. Click **Test** → Should show **Success** ✅
5. Click **Save**

---

## Troubleshooting

### Health Check Returns HTML
**Problem:** Getting `<!doctype html>` instead of JSON  
**Solution:** Endpoints need `[AllowAnonymous]` attribute (already fixed)

### 401 Unauthorized
**Problem:** Authenticated endpoints return 401  
**Solution:** Login first and use cookies/session

### Empty Torrent List
**Problem:** `/torrents/info` returns `[]`  
**Solution:** Normal if no torrents exist in RDT Client

### Tracker Endpoint Not Found
**Problem:** 404 on `/torrents/trackers`  
**Solution:** Rebuild and restart RDT Client with latest changes

### Filter Not Working
**Problem:** Filter parameter ignored  
**Solution:** Ensure using correct values: `completed`, `downloading`, `paused`, `error`, `stalled`, `all`

---

## Expected JSON Responses

### Tracker Response
```json
[
  {
    "url": "udp://tracker.opentrackr.org:1337/announce",
    "status": 2,
    "tier": 0,
    "num_peers": 10,
    "num_seeds": 10,
    "num_leeches": 0,
    "num_downloaded": 100,
    "msg": "Working"
  },
  {
    "url": "** [DHT] **",
    "status": 2,
    "tier": -1,
    "num_peers": 20,
    "num_seeds": 20,
    "num_leeches": 0,
    "num_downloaded": 0,
    "msg": ""
  }
]
```

### Torrent Info Response (Partial)
```json
[
  {
    "hash": "abc123...",
    "name": "Example Torrent",
    "state": "downloading",
    "progress": 0.5,
    "category": "movies",
    "size": 1073741824,
    "dlspeed": 1048576,
    "eta": 1024,
    ...
  }
]
```

### Torrent Properties Response (Partial)
```json
{
  "addition_date": 1696867200,
  "save_path": "/downloads/movies",
  "is_private": false,
  "total_size": 1073741824,
  "dl_speed": 1048576,
  ...
}
```

---

## Quick Test Script (Bash)

```bash
#!/bin/bash
BASE_URL="http://localhost:6500/api/v2"
USERNAME="admin"
PASSWORD="password"

echo "1. Health Check..."
curl -s "$BASE_URL/app/webapiVersion"
echo -e "\n"

echo "2. Login..."
curl -s -X POST "$BASE_URL/auth/login" \
  -d "username=$USERNAME&password=$PASSWORD" \
  -c cookies.txt
echo -e "\n"

echo "3. Get Torrents..."
curl -s "$BASE_URL/torrents/info" -b cookies.txt | jq '.[0].hash' -r > hash.txt
HASH=$(cat hash.txt)
echo "Found hash: $HASH"
echo -e "\n"

echo "4. Get Trackers..."
curl -s "$BASE_URL/torrents/trackers?hash=$HASH" -b cookies.txt | jq .
echo -e "\n"

echo "5. Get Properties..."
curl -s "$BASE_URL/torrents/properties?hash=$HASH" -b cookies.txt | jq '.is_private'
echo -e "\n"

rm cookies.txt hash.txt
echo "Done!"
```

Save as `test-rdt.sh` and run: `bash test-rdt.sh`

