/**
 * Floor Map Module JavaScript - Sprint 9.6 Enterprise RTLS Edition
 * Real-time SignalR broadcasts, search beacon filter with "No beacon found" state,
 * compact circular markers with soft pulses, smart non-clipping floating popup,
 * hover tooltips, bidirectional selection synchronization, and bottom status bar tracking.
 */

// ==========================================================================
// STATE MANAGEMENT & JSON DATA LOADING
// ==========================================================================

let floorMapData = {
    scanners: [],
    beacons: [],
    buildings: [],
    floors: []
};

// Currently selected beacon ID
let selectedBeaconId = null;

// Active search query
let activeSearchQuery = "";

// SignalR Connection instance reference
let signalRConnection = null;

// Room area mapping lookup dictionary (Normalized lowercase string -> CSS Grid room area ID)
const LOCATION_ROOM_MAP = {
    "sitel": "sitel",
    "sitel room": "sitel",
    "meeting": "meeting",
    "meeting room": "meeting",
    "storage": "storage",
    "storage room": "storage",
    "corridor": "corridor",
    "hallway": "corridor",
    "elevator": "elevator",
    "lift": "elevator",
    "toilet": "toilet",
    "restroom": "toilet",
    "bathroom": "toilet",
    "exit": "exit",
    "emergency exit": "exit",
    "corridor (east)": "corridor-east",
    "corridor east": "corridor-east",
    "east corridor": "corridor-east"
};

// ==========================================================================
// INITIALIZATION
// ==========================================================================

document.addEventListener("DOMContentLoaded", () => {
    loadDatabasePayload();
    populateBuildingFilter();
    populateFloorFilter();
    initSearchFilter();
    initPopupHandlers();
    applyMapFilters();
    initSignalRConnection();
    initOfflineTimeoutChecker();
});

/**
 * Load database JSON payload embedded in Razor view script element #floorMapData
 */
function loadDatabasePayload() {
    const dataElem = document.getElementById("floorMapData");
    if (!dataElem) return;

    try {
        const rawJson = dataElem.textContent || dataElem.innerText;
        if (rawJson && rawJson.trim().length > 0) {
            const parsed = JSON.parse(rawJson);
            floorMapData = {
                scanners: parsed.scanners || [],
                beacons: parsed.beacons || [],
                buildings: parsed.buildings || [],
                floors: parsed.floors || []
            };

            // Parse rawLastSeen for initial beacons
            floorMapData.beacons.forEach(b => {
                if (b.rawLastSeen) {
                    b.rawLastSeen = new Date(b.rawLastSeen);
                }
            });
        }
    } catch (err) {
        console.error("Error parsing floor map JSON data payload:", err);
    }
}

// ==========================================================================
// BLE ESTIMATED DISTANCE CALCULATION
// ==========================================================================

/**
 * Calculate estimated distance in meters from RSSI using standard BLE path-loss formula.
 * Formula: Distance = 10 ^ ((txPower - RSSI) / (10 * N))
 * @param {number} rssi - Received Signal Strength Indicator in dBm
 * @returns {string} Formatted distance string e.g. "0.8 m" or "N/A"
 */
function calculateEstimatedDistance(rssi) {
    if (rssi === undefined || rssi === null || rssi === 0 || isNaN(rssi)) {
        return "N/A";
    }

    const txPower = -59.0;
    const pathLossExponent = 2.5;
    const distance = Math.pow(10, (txPower - rssi) / (10.0 * pathLossExponent));
    return distance.toFixed(1) + " m";
}

// ==========================================================================
// SEARCH FILTER FUNCTIONALITY
// ==========================================================================

/**
 * Initialize real-time search input listener (searches while typing)
 */
function initSearchFilter() {
    const searchInput = document.getElementById("beacon-search-input");
    if (!searchInput) return;

    searchInput.addEventListener("input", (e) => {
        activeSearchQuery = (e.target.value || "").trim().toLowerCase();
        applyMapFilters();
    });
}

/**
 * Check if a beacon matches the current search query (Device Name, MAC, Scanner Name)
 */
function matchesSearchQuery(beacon) {
    if (!activeSearchQuery) return true;

    const deviceName = (beacon.deviceName || "").toLowerCase();
    const macAddress = (beacon.macAddress || "").toLowerCase();
    const scannerName = (beacon.scannerName || beacon.scannerId || "").toLowerCase();

    return deviceName.includes(activeSearchQuery) ||
           macAddress.includes(activeSearchQuery) ||
           scannerName.includes(activeSearchQuery);
}

// ==========================================================================
// SIGNALR INFRASTRUCTURE REUSE
// ==========================================================================

/**
 * Initialize SignalR Hub connection using existing /beaconHub endpoint and "BeaconUpdate" broadcast method
 */
function initSignalRConnection() {
    if (typeof signalR === "undefined") {
        console.warn("SignalR client library script is not loaded.");
        updateSignalRStatus("Disconnected", "status-disconnected");
        return;
    }

    signalRConnection = new signalR.HubConnectionBuilder()
        .withUrl("/beaconHub")
        .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
        .build();

    signalRConnection.onreconnecting(() => {
        updateSignalRStatus("Connecting...", "status-connecting");
    });

    signalRConnection.onreconnected(() => {
        updateSignalRStatus("Connected", "status-connected");
    });

    signalRConnection.onclose(() => {
        updateSignalRStatus("Disconnected", "status-disconnected");
        setTimeout(startSignalRConnection, 5000);
    });

    // Handle real-time telemetry broadcast from server
    signalRConnection.on("BeaconUpdate", (data) => {
        if (!data) return;

        // Ignore demo devices if any
        if (data.macAddress && data.macAddress.startsWith("00:11:22:33:44")) {
            return;
        }

        // Incrementally update beacon position & details
        updateBeaconPosition(data);
    });

    startSignalRConnection();
}

async function startSignalRConnection() {
    if (!signalRConnection) return;

    try {
        updateSignalRStatus("Connecting...", "status-connecting");
        await signalRConnection.start();
        updateSignalRStatus("Connected", "status-connected");
    } catch (err) {
        console.error("SignalR Connection Error:", err);
        updateSignalRStatus("Disconnected", "status-disconnected");
        setTimeout(startSignalRConnection, 5000);
    }
}

function updateSignalRStatus(text, statusClass) {
    const statusElem = document.getElementById("floormap-signalr-status");
    const textElem = document.getElementById("signalr-status-text");

    if (statusElem) {
        statusElem.className = `floormap-signalr-status ${statusClass}`;
    }
    if (textElem) {
        if (text === "Connected") {
            textElem.innerHTML = "🟢 Connected";
        } else if (text === "Disconnected" || text === "Connection Lost") {
            textElem.innerHTML = "🔴 Disconnected";
        } else {
            textElem.textContent = text;
        }
    }
}

// ==========================================================================
// REAL-TIME BEACON INCREMENTAL UPDATE
// ==========================================================================

function updateBeaconPosition(data) {
    if (!data) return;

    const mac = data.macAddress || "";
    const receiveTime = data.receiveTime ? new Date(data.receiveTime) : new Date();

    let beacon = floorMapData.beacons.find(b =>
        (b.macAddress && mac && b.macAddress.toLowerCase() === mac.toLowerCase()) ||
        (b.beaconId && data.beaconId && String(b.beaconId) === String(data.beaconId))
    );

    if (!beacon) {
        beacon = {
            beaconId: data.beaconId || mac,
            deviceName: data.deviceName || mac || "Unknown Beacon",
            macAddress: mac,
            scannerId: data.scannerId || null,
            scannerName: data.scannerName || data.scannerId || "Unknown Access Point",
            building: data.scannerBuilding || "Unknown",
            floor: data.scannerFloor || "Unknown",
            location: data.scannerLocation || "Unknown Location",
            rssi: data.rssi || 0,
            batteryLevel: data.batteryLevel !== undefined ? data.batteryLevel : 100,
            isMoving: data.isMoving || false,
            lastSeen: "just now",
            rawLastSeen: receiveTime,
            isOnline: true,
            status: data.isMoving ? "Moving" : "Online"
        };
        floorMapData.beacons.push(beacon);
        updateFiltersForNewAsset(beacon.building, beacon.floor);
    } else {
        if (data.deviceName) beacon.deviceName = data.deviceName;
        if (data.scannerId) beacon.scannerId = data.scannerId;
        if (data.scannerName) beacon.scannerName = data.scannerName;
        if (data.scannerBuilding) beacon.building = data.scannerBuilding;
        if (data.scannerFloor) beacon.floor = data.scannerFloor;
        if (data.scannerLocation) beacon.location = data.scannerLocation;

        beacon.rssi = data.rssi;
        if (data.batteryLevel !== undefined && data.batteryLevel !== null) {
            beacon.batteryLevel = data.batteryLevel;
        }
        beacon.isMoving = !!data.isMoving;
        beacon.rawLastSeen = receiveTime;
        beacon.lastSeen = "just now";
        beacon.isOnline = true;
        beacon.status = beacon.isMoving ? "Moving" : "Online";
    }

    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    const matchesBuildingFloor = (!selectedBuilding || beacon.building === selectedBuilding) &&
                                 (!selectedFloor || beacon.floor === selectedFloor);

    const isVisible = matchesBuildingFloor && matchesSearchQuery(beacon);

    updateSingleBeaconListItem(beacon, isVisible);
    updateSingleBeaconMarker(beacon, isVisible);

    if (String(selectedBeaconId) === String(beacon.beaconId || getSafeDomId(beacon))) {
        const markerElem = document.getElementById(`map-marker-${getSafeDomId(beacon)}`);
        updatePopupContent(beacon, markerElem);
    }

    updateStatusBar();
    checkEmptyStateVisibility();
}

/**
 * Incrementally update or insert a single Beacon List item row
 */
function updateSingleBeaconListItem(beacon, isVisible) {
    const container = document.getElementById("beacon-list-container");
    if (!container) return;

    const safeId = getSafeDomId(beacon);
    let cardElem = document.getElementById(`beacon-card-item-${safeId}`);

    if (!isVisible) {
        if (cardElem) cardElem.remove();
        return;
    }

    const beaconIdStr = String(beacon.beaconId || safeId);
    const isSelected = beaconIdStr === String(selectedBeaconId);
    const statusText = beacon.status || (beacon.isOnline ? "Online" : "Offline");
    const statusClass = getStatusClass(statusText, beacon.isOnline);
    const dotClass = beacon.isOnline ? "online" : "offline";

    const deviceName = beacon.deviceName || beacon.macAddress || "Unknown Beacon";
    const scannerName = beacon.scannerName || beacon.scannerId || "Unknown Access Point";
    const rssiText = (beacon.rssi && beacon.rssi !== 0) ? `${beacon.rssi} dBm` : "N/A";
    const distanceText = calculateEstimatedDistance(beacon.rssi);
    const batteryText = (beacon.batteryLevel !== null && beacon.batteryLevel !== undefined) ? `${beacon.batteryLevel}%` : "N/A";
    const lastSeenText = beacon.lastSeen || "just now";

    const innerHtml = `
        <div class="beacon-card-header">
            <div class="beacon-card-title-group">
                <span class="beacon-status-dot ${dotClass}"></span>
                <span class="beacon-name" title="${escapeHtml(deviceName)}">${escapeHtml(deviceName)}</span>
            </div>
            <span class="status-pill ${statusClass}">${escapeHtml(statusText)}</span>
        </div>
        <div class="beacon-details-grid">
            <div class="beacon-detail-item" title="Access Point: ${escapeHtml(scannerName)}">
                <i class="bi bi-router"></i>
                <span>${escapeHtml(scannerName)}</span>
            </div>
            <div class="beacon-detail-item">
                <i class="bi bi-wifi"></i>
                <span>${rssiText}</span>
            </div>
            <div class="beacon-detail-item">
                <i class="bi bi-ruler"></i>
                <span>${distanceText}</span>
            </div>
            <div class="beacon-detail-item">
                <i class="bi bi-battery-half"></i>
                <span>${batteryText}</span>
            </div>
            <div class="beacon-detail-item full-row">
                <i class="bi bi-clock-history"></i>
                <span class="last-seen-val">${escapeHtml(lastSeenText)}</span>
            </div>
        </div>
    `;

    const emptyMsg = container.querySelector(".text-muted");
    if (emptyMsg) emptyMsg.remove();

    if (cardElem) {
        cardElem.className = `beacon-card-item ${isSelected ? "selected" : ""}`;
        cardElem.innerHTML = innerHtml;
    } else {
        cardElem = document.createElement("div");
        cardElem.id = `beacon-card-item-${safeId}`;
        cardElem.className = `beacon-card-item ${isSelected ? "selected" : ""}`;
        cardElem.setAttribute("data-beacon-id", beaconIdStr);
        cardElem.innerHTML = innerHtml;

        cardElem.addEventListener("click", () => {
            highlightBeacon(beaconIdStr);
        });

        container.appendChild(cardElem);
    }
}

/**
 * Incrementally update or move a single Beacon map marker
 */
function updateSingleBeaconMarker(beacon, isVisible) {
    const safeId = getSafeDomId(beacon);
    const beaconIdStr = String(beacon.beaconId || safeId);
    let markerElem = document.getElementById(`map-marker-${safeId}`);

    if (!isVisible) {
        if (markerElem) markerElem.remove();
        return;
    }

    const roomAreaKey = getRoomAreaFromLocation(beacon.location);
    const targetContainer = document.getElementById(`markers-${roomAreaKey}`);
    if (!targetContainer) return;

    const isSelected = beaconIdStr === String(selectedBeaconId);
    const statusText = beacon.status || (beacon.isOnline ? "Online" : "Offline");
    const statusClass = getStatusClass(statusText, beacon.isOnline);
    const shortLabel = getBeaconShortLabel(beacon);

    const innerHtml = `
        <span>${escapeHtml(shortLabel)}</span>
        ${createTooltipHtml(beacon)}
    `;

    if (markerElem) {
        if (markerElem.parentElement !== targetContainer) {
            targetContainer.appendChild(markerElem);
        }

        markerElem.className = `map-marker beacon-marker ${statusClass} ${isSelected ? "selected" : ""}`;
        markerElem.innerHTML = innerHtml;
    } else {
        markerElem = document.createElement("div");
        markerElem.id = `map-marker-${safeId}`;
        markerElem.className = `map-marker beacon-marker ${statusClass} ${isSelected ? "selected" : ""}`;
        markerElem.setAttribute("data-beacon-id", beaconIdStr);
        markerElem.innerHTML = innerHtml;

        markerElem.addEventListener("click", (e) => {
            e.stopPropagation();
            highlightBeacon(beaconIdStr);
        });

        targetContainer.appendChild(markerElem);
    }
}

// ==========================================================================
// SHORT MARKER LABELS
// ==========================================================================

function getScannerShortLabel(scanner, index) {
    if (scanner.scannerName && scanner.scannerName.toLowerCase().startsWith("scanner-")) {
        const num = scanner.scannerName.substring(8);
        return `AP${parseInt(num, 10) || (index + 1)}`;
    }
    return `AP${index + 1}`;
}

function getBeaconShortLabel(beacon) {
    const idx = floorMapData.beacons.findIndex(b => String(b.beaconId) === String(beacon.beaconId));
    if (idx >= 0) {
        return `B${idx + 1}`;
    }
    return "B";
}

// ==========================================================================
// FLOATING DETAIL POPUP HANDLERS & SMART NON-CLIPPING POSITIONING
// ==========================================================================

function initPopupHandlers() {
    const closeBtn = document.getElementById("popup-close-btn");
    if (closeBtn) {
        closeBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            hideBeaconPopup();
            selectedBeaconId = null;
            clearSelections();
        });
    }
}

/**
 * Update popup content and position smartly so it NEVER clips outside the map area
 */
function updatePopupContent(beacon, markerElem) {
    const popup = document.getElementById("floormap-beacon-popup");
    if (!popup) return;

    const deviceName = beacon.deviceName || beacon.macAddress || "Unknown Beacon";
    const macAddress = beacon.macAddress || "N/A";
    const scannerName = beacon.scannerName || beacon.scannerId || "Unknown Access Point";
    const location = beacon.location || "Unknown Location";
    const rssiText = (beacon.rssi && beacon.rssi !== 0) ? `${beacon.rssi} dBm` : "N/A";
    const batteryText = (beacon.batteryLevel !== null && beacon.batteryLevel !== undefined) ? `${beacon.batteryLevel}%` : "N/A";
    const distanceText = calculateEstimatedDistance(beacon.rssi);
    const statusText = beacon.status || (beacon.isOnline ? "Online" : "Offline");
    const lastSeenText = beacon.lastSeen || "just now";

    document.getElementById("popup-device-name").textContent = deviceName;
    document.getElementById("popup-mac-address").textContent = macAddress;
    document.getElementById("popup-scanner-name").textContent = scannerName;
    document.getElementById("popup-location").textContent = location;
    document.getElementById("popup-rssi").textContent = rssiText;
    document.getElementById("popup-battery").textContent = batteryText;
    document.getElementById("popup-distance").textContent = distanceText;

    const statusElem = document.getElementById("popup-status");
    statusElem.textContent = statusText;
    statusElem.style.color = beacon.isOnline ? (statusText === "Moving" ? "#60a5fa" : "#34d399") : "#f87171";

    document.getElementById("popup-last-seen").textContent = lastSeenText;

    popup.classList.remove("d-none");

    // Smart positioning so popup stays fully within .floor-map-wrapper bounds
    if (markerElem) {
        positionBeaconPopup(markerElem);
    }
}

/**
 * Position floating detail popup smartly relative to marker without clipping map borders
 */
function positionBeaconPopup(markerElem) {
    const popup = document.getElementById("floormap-beacon-popup");
    const wrapper = document.querySelector(".floor-map-wrapper");
    if (!popup || !wrapper || !markerElem) return;

    const wrapperRect = wrapper.getBoundingClientRect();
    const markerRect = markerElem.getBoundingClientRect();

    const popupWidth = popup.offsetWidth || 320;
    const popupHeight = popup.offsetHeight || 220;

    // Default position: floating to the right of marker
    let left = markerRect.right - wrapperRect.left + 12;
    let top = markerRect.top - wrapperRect.top - 10;

    // If marker is near the right edge of wrapper -> position on the left of marker
    if (left + popupWidth > wrapperRect.width - 15) {
        left = markerRect.left - wrapperRect.left - popupWidth - 12;
    }

    // Clamp left coordinates inside wrapper
    left = Math.max(10, Math.min(left, wrapperRect.width - popupWidth - 10));

    // Clamp top coordinates inside wrapper
    top = Math.max(10, Math.min(top, wrapperRect.height - popupHeight - 10));

    popup.style.left = `${left}px`;
    popup.style.top = `${top}px`;
}

function hideBeaconPopup() {
    const popup = document.getElementById("floormap-beacon-popup");
    if (popup) {
        popup.classList.add("d-none");
    }
}

// ==========================================================================
// BOTTOM STATUS BAR UPDATER
// ==========================================================================

function updateStatusBar() {
    const totalScanners = floorMapData.scanners.length;
    const totalBeacons = floorMapData.beacons.length;
    const onlineBeacons = floorMapData.beacons.filter(b => b.isOnline).length;
    const offlineBeacons = floorMapData.beacons.filter(b => !b.isOnline).length;
    const lowBatteryBeacons = floorMapData.beacons.filter(b => b.batteryLevel !== null && b.batteryLevel < 20).length;

    const scannersElem = document.getElementById("stat-scanners-count");
    const beaconsElem = document.getElementById("stat-beacons-count");
    const onlineElem = document.getElementById("stat-online-count");
    const offlineElem = document.getElementById("stat-offline-count");
    const lowBatElem = document.getElementById("stat-low-battery-count");
    const lastUpdateElem = document.getElementById("stat-last-update-time");

    if (scannersElem) scannersElem.textContent = totalScanners;
    if (beaconsElem) beaconsElem.textContent = totalBeacons;
    if (onlineElem) onlineElem.textContent = onlineBeacons;
    if (offlineElem) offlineElem.textContent = offlineBeacons;
    if (lowBatElem) lowBatElem.textContent = lowBatteryBeacons;

    if (lastUpdateElem) {
        const now = new Date();
        const hh = String(now.getHours()).padStart(2, "0");
        const mm = String(now.getMinutes()).padStart(2, "0");
        const ss = String(now.getSeconds()).padStart(2, "0");
        lastUpdateElem.textContent = `${hh}:${mm}:${ss}`;
    }
}

// ==========================================================================
// ROOM LOCATION MAPPING
// ==========================================================================

function getRoomAreaFromLocation(location) {
    if (!location) return "corridor";

    const normalized = String(location).trim().toLowerCase();

    for (const key in LOCATION_ROOM_MAP) {
        if (normalized === key || normalized.includes(key)) {
            return LOCATION_ROOM_MAP[key];
        }
    }

    return "corridor";
}

// ==========================================================================
// FILTER FUNCTIONS
// ==========================================================================

function populateBuildingFilter() {
    const buildingSelect = document.getElementById("building-filter");
    if (!buildingSelect) return;

    const currentVal = buildingSelect.value;
    buildingSelect.innerHTML = "";

    let buildings = floorMapData.buildings && floorMapData.buildings.length > 0
        ? floorMapData.buildings
        : Array.from(new Set([
            ...floorMapData.scanners.map(s => s.building),
            ...floorMapData.beacons.map(b => b.building)
        ])).filter(b => b && b !== "Unknown");

    if (buildings.length === 0) {
        buildings = ["Siriraj"];
    }

    buildings.forEach(b => {
        const opt = document.createElement("option");
        opt.value = b;
        opt.textContent = b;
        buildingSelect.appendChild(opt);
    });

    if (currentVal && buildings.includes(currentVal)) {
        buildingSelect.value = currentVal;
    } else {
        buildingSelect.value = buildings[0];
    }

    buildingSelect.addEventListener("change", () => {
        populateFloorFilter();
        applyMapFilters();
    });
}

function populateFloorFilter() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    if (!floorSelect) return;

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const currentVal = floorSelect.value;
    floorSelect.innerHTML = "";

    const scannerFloors = floorMapData.scanners
        .filter(s => !selectedBuilding || s.building === selectedBuilding)
        .map(s => s.floor);

    const beaconFloors = floorMapData.beacons
        .filter(b => !selectedBuilding || b.building === selectedBuilding)
        .map(b => b.floor);

    let floors = Array.from(new Set([...scannerFloors, ...beaconFloors]))
        .filter(f => f && f !== "Unknown")
        .sort();

    if (floors.length === 0) {
        floors = ["6"];
    }

    floors.forEach(f => {
        const opt = document.createElement("option");
        opt.value = f;
        opt.textContent = f.startsWith("Floor") ? f : `Floor ${f}`;
        floorSelect.appendChild(opt);
    });

    if (currentVal && floors.includes(currentVal)) {
        floorSelect.value = currentVal;
    } else {
        floorSelect.value = floors[0];
    }

    floorSelect.addEventListener("change", () => applyMapFilters());
}

function updateFiltersForNewAsset(building, floor) {
    if (building && !floorMapData.buildings.includes(building)) {
        floorMapData.buildings.push(building);
        populateBuildingFilter();
    }
    if (floor && !floorMapData.floors.includes(floor)) {
        floorMapData.floors.push(floor);
        populateFloorFilter();
    }
}

function applyMapFilters() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    const filteredScanners = floorMapData.scanners.filter(s => {
        const matchBuilding = !selectedBuilding || s.building === selectedBuilding;
        const matchFloor = !selectedFloor || s.floor === selectedFloor;
        return matchBuilding && matchFloor;
    });

    const filteredBeacons = floorMapData.beacons.filter(b => {
        const matchBuilding = !selectedBuilding || b.building === selectedBuilding;
        const matchFloor = !selectedFloor || b.floor === selectedFloor;
        return matchBuilding && matchFloor && matchesSearchQuery(b);
    });

    renderBeaconList(filteredBeacons);
    renderScannerMarkers(filteredScanners);
    renderBeaconMarkers(filteredBeacons);

    updateStatusBar();
    checkEmptyStateVisibility();
}

// ==========================================================================
// BULK RENDERERS
// ==========================================================================

function renderBeaconList(filteredBeacons) {
    const container = document.getElementById("beacon-list-container");
    if (!container) return;

    container.innerHTML = "";

    if (!filteredBeacons || filteredBeacons.length === 0) {
        container.innerHTML = `
            <div class="text-center text-muted py-4 small">
                <i class="bi bi-search fs-4 d-block mb-1 opacity-50"></i>
                No beacon found.
            </div>
        `;
        return;
    }

    filteredBeacons.forEach(beacon => {
        updateSingleBeaconListItem(beacon, true);
    });
}

function clearAllMarkers() {
    const markerContainers = document.querySelectorAll(".room-markers");
    markerContainers.forEach(c => {
        c.innerHTML = "";
    });
}

function renderScannerMarkers(filteredScanners) {
    clearAllMarkers();

    filteredScanners.forEach((scanner, index) => {
        const roomAreaKey = getRoomAreaFromLocation(scanner.location);
        const container = document.getElementById(`markers-${roomAreaKey}`);
        if (!container) return;

        const scannerName = scanner.scannerName || scanner.scannerId || "Unknown Access Point";
        const locationText = scanner.location || "Unknown Location";
        const shortLabel = getScannerShortLabel(scanner, index);

        const scannerElem = document.createElement("div");
        scannerElem.className = "map-marker scanner-marker";
        scannerElem.setAttribute("title", `Access Point: ${scannerName} (${locationText})`);

        scannerElem.innerHTML = `
            <span>${escapeHtml(shortLabel)}</span>
        `;

        container.appendChild(scannerElem);
    });
}

function renderBeaconMarkers(filteredBeacons) {
    filteredBeacons.forEach(beacon => {
        updateSingleBeaconMarker(beacon, true);
    });
}

/**
 * Generate hover tooltip HTML for a beacon marker
 * Shows Device Name, RSSI, Battery, Estimated Distance without clicking
 */
function createTooltipHtml(beacon) {
    const deviceName = beacon.deviceName || beacon.macAddress || "Unknown Beacon";
    const rssiText = (beacon.rssi && beacon.rssi !== 0) ? `${beacon.rssi} dBm` : "N/A";
    const batteryText = (beacon.batteryLevel !== null && beacon.batteryLevel !== undefined) ? `${beacon.batteryLevel}%` : "N/A";
    const distanceText = calculateEstimatedDistance(beacon.rssi);

    return `
        <div class="floormap-tooltip">
            <div class="tooltip-title">${escapeHtml(deviceName)}</div>
            <div class="tooltip-row">
                <span class="label">RSSI:</span>
                <span>${rssiText}</span>
            </div>
            <div class="tooltip-row">
                <span class="label">Battery:</span>
                <span>${batteryText}</span>
            </div>
            <div class="tooltip-row">
                <span class="label">Distance:</span>
                <span class="text-info fw-bold">${distanceText}</span>
            </div>
        </div>
    `;
}

// ==========================================================================
// BIDIRECTIONAL SELECTION & HIGHLIGHTING HANDLERS
// ==========================================================================

function highlightBeacon(beaconId) {
    const beaconIdStr = String(beaconId);

    if (String(selectedBeaconId) === beaconIdStr) {
        selectedBeaconId = null;
        hideBeaconPopup();
        clearSelections();
        return;
    }

    selectedBeaconId = beaconIdStr;

    // 1. Highlight matching Beacon List Card & scroll list if necessary
    document.querySelectorAll(".beacon-card-item").forEach(card => {
        if (card.getAttribute("data-beacon-id") === String(selectedBeaconId)) {
            card.classList.add("selected");
            card.scrollIntoView({ behavior: "smooth", block: "nearest" });
        } else {
            card.classList.remove("selected");
        }
    });

    // 2. Highlight matching Map Marker & scroll map if necessary
    let selectedMarkerElem = null;
    document.querySelectorAll(".beacon-marker").forEach(marker => {
        if (marker.getAttribute("data-beacon-id") === String(selectedBeaconId)) {
            marker.classList.add("selected");
            selectedMarkerElem = marker;
            marker.scrollIntoView({ behavior: "smooth", block: "nearest" });
        } else {
            marker.classList.remove("selected");
        }
    });

    // 3. Highlight Room Cell
    document.querySelectorAll(".room-cell").forEach(room => {
        room.classList.remove("highlight-room");
    });

    const beacon = floorMapData.beacons.find(b => String(b.beaconId) === String(selectedBeaconId) || getSafeDomId(b) === String(selectedBeaconId));
    if (beacon) {
        const roomAreaKey = getRoomAreaFromLocation(beacon.location);
        const roomCell = document.querySelector(`.area-${roomAreaKey}`);
        if (roomCell) {
            roomCell.classList.add("highlight-room");
        }

        // Open floating detail popup for selected beacon
        updatePopupContent(beacon, selectedMarkerElem);
    } else {
        hideBeaconPopup();
    }
}

function clearSelections() {
    document.querySelectorAll(".beacon-card-item").forEach(c => c.classList.remove("selected"));
    document.querySelectorAll(".beacon-marker").forEach(m => m.classList.remove("selected"));
    document.querySelectorAll(".room-cell").forEach(r => r.classList.remove("highlight-room"));
}

// ==========================================================================
// OFFLINE TIMEOUT CHECKER (30-SECOND RULE REUSE)
// ==========================================================================

function initOfflineTimeoutChecker() {
    setInterval(() => {
        const now = new Date();
        const buildingSelect = document.getElementById("building-filter");
        const floorSelect = document.getElementById("floor-filter");
        const selectedBuilding = buildingSelect ? buildingSelect.value : "";
        const selectedFloor = floorSelect ? floorSelect.value : "";

        floorMapData.beacons.forEach(beacon => {
            if (beacon.rawLastSeen) {
                const diffMs = now - new Date(beacon.rawLastSeen);
                const diffSecs = Math.max(0, Math.floor(diffMs / 1000));

                if (diffSecs > 30 && beacon.isOnline) {
                    beacon.isOnline = false;
                    beacon.status = "Offline";
                    beacon.lastSeen = formatRelativeTime(diffSecs);

                    const isVisible = (!selectedBuilding || beacon.building === selectedBuilding) &&
                                      (!selectedFloor || beacon.floor === selectedFloor) &&
                                      matchesSearchQuery(beacon);

                    updateSingleBeaconListItem(beacon, isVisible);
                    updateSingleBeaconMarker(beacon, isVisible);

                    if (String(selectedBeaconId) === String(beacon.beaconId)) {
                        const markerElem = document.getElementById(`map-marker-${getSafeDomId(beacon)}`);
                        updatePopupContent(beacon, markerElem);
                    }
                } else if (beacon.isOnline) {
                    beacon.lastSeen = formatRelativeTime(diffSecs);
                    updateBeaconRelativeTimeUI(beacon);
                }
            }
        });

        updateStatusBar();
    }, 5000);
}

function updateBeaconRelativeTimeUI(beacon) {
    const safeId = getSafeDomId(beacon);
    const cardElem = document.getElementById(`beacon-card-item-${safeId}`);
    if (cardElem) {
        const timeElem = cardElem.querySelector(".last-seen-val");
        if (timeElem) {
            timeElem.textContent = beacon.lastSeen;
        }
    }
}

// ==========================================================================
// EMPTY STATE HANDLING
// ==========================================================================

function checkEmptyStateVisibility() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    const visibleScanners = floorMapData.scanners.filter(s =>
        (!selectedBuilding || s.building === selectedBuilding) &&
        (!selectedFloor || s.floor === selectedFloor)
    );

    const visibleBeacons = floorMapData.beacons.filter(b =>
        (!selectedBuilding || b.building === selectedBuilding) &&
        (!selectedFloor || b.floor === selectedFloor) &&
        matchesSearchQuery(b)
    );

    showEmptyState(visibleScanners.length === 0 && visibleBeacons.length === 0);
}

function showEmptyState(isEmpty) {
    const gridElem = document.querySelector(".floor-map-grid");
    if (!gridElem) return;

    let emptyElem = document.getElementById("floormap-empty-state");

    if (isEmpty) {
        if (!emptyElem) {
            emptyElem = document.createElement("div");
            emptyElem.id = "floormap-empty-state";
            emptyElem.className = "floormap-empty-state";
            emptyElem.innerHTML = `
                <div class="floormap-empty-icon">🗺️</div>
                <div class="floormap-empty-title">No Access Points or Beacons Found</div>
                <p class="floormap-empty-desc">There are no assets matching the selected building, floor, or search filters.</p>
            `;
            gridElem.appendChild(emptyElem);
        }
        emptyElem.style.display = "flex";
    } else {
        if (emptyElem) {
            emptyElem.style.display = "none";
        }
    }
}

// ==========================================================================
// UTILITY FUNCTIONS
// ==========================================================================

function getSafeDomId(beacon) {
    const raw = beacon.beaconId || beacon.macAddress || "unknown";
    return String(raw).replace(/[^a-zA-Z0-9_-]/g, "-");
}

function getStatusClass(statusText, isOnline) {
    if (!isOnline || statusText === "Offline") return "offline";
    if (statusText === "Moving") return "moving";
    return "online";
}

function formatRelativeTime(diffSecs) {
    if (diffSecs < 10) return "just now";
    if (diffSecs < 60) return `${diffSecs} sec ago`;
    if (diffSecs < 3600) return `${Math.floor(diffSecs / 60)} min ago`;
    if (diffSecs < 86400) return `${Math.floor(diffSecs / 3600)} hr ago`;
    return `${Math.floor(diffSecs / 86400)} days ago`;
}

function escapeHtml(str) {
    if (!str) return "";
    return String(str)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}
