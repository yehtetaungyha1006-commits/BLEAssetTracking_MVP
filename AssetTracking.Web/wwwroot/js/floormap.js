/**
 * Floor Map Module JavaScript - Enterprise RTLS Edition with Dynamic Building & Floor Management
 * Real-time SignalR broadcasts, dynamic Building & Floor dropdown management, image-based floor plan renderer,
 * search beacon filter with "No beacon found" state, compact circular markers with soft pulses,
 * smart non-clipping floating popup, hover tooltips, bidirectional selection synchronization,
 * event delegation AP pointer drag-and-drop over image bounds, and bottom status bar tracking.
 */

// ==========================================================================
// STATE MANAGEMENT & JSON DATA LOADING
// ==========================================================================

let floorMapData = {
    scanners: [],
    beacons: [],
    buildingList: [],
    floorList: [],
    buildings: [],
    floors: []
};

// Currently selected beacon ID
let selectedBeaconId = null;

// Active search query
let activeSearchQuery = "";

// SignalR Connection instance reference
let signalRConnection = null;

// Admin Edit Mode state & drag tracking variables
let isEditModeActive = false;
let originalScannerPositions = {};
let pendingChanges = {};

let draggingAp = null;
let activePointerId = null;
let dragScannerObj = null;

// Explicit coordinate percentage positions for location fallbacks
const accessPointPositions = {
    sitel: { x: 25, y: 30 },
    room617: { x: 55, y: 30 },
    storage: { x: 80, y: 30 },
    corridor: { x: 50, y: 55 },
    training: { x: 25, y: 80 },
    training1: { x: 55, y: 80 },
    toilet: { x: 80, y: 80 },
    elevator: { x: 90, y: 55 }
};

/**
 * Normalize Thai/English location names and return uniform keys.
 */
function normalizeLocation(location) {
    if (!location) return "";
    let loc = String(location).trim().replace(/\s+/g, ' ').toLowerCase();

    if (loc === "ห้อง 617" || loc === "ห้องประชุม 617" || loc === "ห้อง ประชุม 617" || loc === "617" || loc === "room 617" || loc === "room617") {
        return "room617";
    }
    if (loc === "ห้อง sitel" || loc === "sitel" || loc === "sitel room" || loc === "sitel_room" || loc === "ห้องsitel") {
        return "sitel";
    }
    if (loc === "ทางเดิน" || loc === "corridor" || loc === "hallway") {
        return "corridor";
    }
    if (loc === "ห้องเก็บของ" || loc === "storage" || loc === "storage room" || loc === "storage_room") {
        return "storage";
    }
    if (loc === "ลิฟต์" || loc === "elevator" || loc === "lift") {
        return "elevator";
    }
    if (loc === "ห้องอบรม" || loc === "training" || loc === "training room" || loc === "training_room") {
        return "training";
    }
    if (loc === "ห้องอบรม 1" || loc === "ห้องอบรม1" || loc === "training1" || loc === "training 1") {
        return "training1";
    }
    if (loc === "ห้องน้ำ" || loc === "toilet" || loc === "restroom" || loc === "bathroom") {
        return "toilet";
    }
    return loc;
}

/**
 * Retrieve absolute coordinates by Location name.
 */
function getPositionByLocation(location, scannerId) {
    const key = normalizeLocation(location);
    if (accessPointPositions[key]) {
        return accessPointPositions[key];
    }
    return null;
}

// ==========================================================================
// INITIALIZATION
// ==========================================================================

document.addEventListener("DOMContentLoaded", () => {
    loadDatabasePayload();
    populateBuildingFilter();
    populateFloorFilter();
    initSearchFilter();
    initPopupHandlers();
    initModalHandlers();
    applyMapFilters();
    initSignalRConnection();
    initOfflineTimeoutChecker();
    initEditModeHandlers();
    initApPointerDragListeners();
    initStageResizer();
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
                buildingList: parsed.buildingList || [],
                floorList: parsed.floorList || [],
                buildings: parsed.buildings || [],
                floors: parsed.floors || []
            };

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
// TOAST NOTIFICATIONS
// ==========================================================================

function showToast(message, isSuccess = true) {
    const toastElem = document.getElementById("floormap-toast");
    const toastBody = document.getElementById("floormap-toast-body");
    if (!toastElem || !toastBody) return;

    toastBody.textContent = message;
    toastElem.className = `toast align-items-center text-white ${isSuccess ? "bg-success" : "bg-danger"} border-0 position-fixed bottom-0 end-0 m-3 shadow-lg show`;

    setTimeout(() => {
        toastElem.classList.remove("show");
    }, 4000);
}

// ==========================================================================
// DYNAMIC BUILDING & FLOOR DROPDOWNS
// ==========================================================================

function populateBuildingFilter() {
    const buildingSelect = document.getElementById("building-filter");
    const modalBuildingSelect = document.getElementById("modal-building-select");
    if (!buildingSelect) return;

    const currentVal = buildingSelect.value;
    buildingSelect.innerHTML = "";
    if (modalBuildingSelect) modalBuildingSelect.innerHTML = "";

    let buildingItems = [];
    if (floorMapData.buildingList && floorMapData.buildingList.length > 0) {
        buildingItems = floorMapData.buildingList.map(b => ({
            id: b.buildingId,
            name: b.buildingName
        }));
    } else {
        const names = floorMapData.buildings && floorMapData.buildings.length > 0
            ? floorMapData.buildings
            : Array.from(new Set([
                ...floorMapData.scanners.map(s => s.building),
                ...floorMapData.beacons.map(b => b.building)
            ])).filter(b => b && b !== "Unknown");

        if (names.length === 0) names.push("Siriraj Building");
        buildingItems = names.map((n, idx) => ({ id: idx + 1, name: n }));
    }

    buildingItems.forEach(b => {
        const opt = document.createElement("option");
        opt.value = b.name;
        opt.setAttribute("data-building-id", b.id);
        opt.textContent = b.name;
        buildingSelect.appendChild(opt);

        if (modalBuildingSelect) {
            const mOpt = document.createElement("option");
            mOpt.value = b.id;
            mOpt.textContent = b.name;
            modalBuildingSelect.appendChild(mOpt);
        }
    });

    if (currentVal && buildingItems.some(b => b.name === currentVal)) {
        buildingSelect.value = currentVal;
    } else if (buildingItems.length > 0) {
        buildingSelect.value = buildingItems[0].name;
    }

    buildingSelect.onchange = () => {
        populateFloorFilter();
        applyMapFilters();
    };
}

function populateFloorFilter() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    if (!floorSelect) return;

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const currentVal = floorSelect.value;
    floorSelect.innerHTML = "";

    let floorItems = [];

    if (floorMapData.floorList && floorMapData.floorList.length > 0) {
        const matchingFloors = floorMapData.floorList.filter(f =>
            !selectedBuilding || (f.buildingName && f.buildingName.toLowerCase() === selectedBuilding.toLowerCase())
        );

        floorItems = matchingFloors.map(f => ({
            id: f.floorId,
            name: f.floorName,
            imagePath: f.floorMapImagePath
        }));
    }

    if (floorItems.length === 0) {
        const scannerFloors = floorMapData.scanners
            .filter(s => !selectedBuilding || s.building === selectedBuilding)
            .map(s => s.floor);

        const beaconFloors = floorMapData.beacons
            .filter(b => !selectedBuilding || b.building === selectedBuilding)
            .map(b => b.floor);

        let names = Array.from(new Set([...scannerFloors, ...beaconFloors]))
            .filter(f => f && f !== "Unknown")
            .sort();

        if (names.length === 0) names = ["Floor 6"];

        floorItems = names.map((n, idx) => ({ id: idx + 1, name: n, imagePath: null }));
    }

    floorItems.forEach(f => {
        const opt = document.createElement("option");
        opt.value = f.name;
        opt.setAttribute("data-floor-id", f.id);
        if (f.imagePath) opt.setAttribute("data-image-path", f.imagePath);
        opt.textContent = f.name.startsWith("Floor") ? f.name : `Floor ${f.name}`;
        floorSelect.appendChild(opt);
    });

    if (currentVal && floorItems.some(f => f.name === currentVal)) {
        floorSelect.value = currentVal;
    } else if (floorItems.length > 0) {
        floorSelect.value = floorItems[0].name;
    }

    updateFloorMapImageBackground();

    floorSelect.onchange = () => {
        updateFloorMapImageBackground();
        applyMapFilters();
    };
}

/**
 * Update Floor Map Image element or toggle Clean Empty State banner
 */
function updateFloorMapImageBackground() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    const imgElem = document.getElementById("floorPlanImage");
    const noImageState = document.getElementById("floormap-no-image-state");
    const titleTextElem = document.getElementById("floormap-title-text");

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    if (titleTextElem) {
        titleTextElem.textContent = `${selectedBuilding || "Building"} - ${selectedFloor || "Floor Plan"}`;
    }

    let imagePath = null;
    if (floorSelect && floorSelect.selectedIndex >= 0) {
        const selectedOpt = floorSelect.options[floorSelect.selectedIndex];
        imagePath = selectedOpt ? selectedOpt.getAttribute("data-image-path") : null;
    }

    if (!imagePath && floorMapData.floorList) {
        const match = floorMapData.floorList.find(f =>
            f.floorName === selectedFloor &&
            (!selectedBuilding || f.buildingName === selectedBuilding)
        );
        if (match) imagePath = match.floorMapImagePath;
    }

    const stageElem = document.getElementById("floor-plan-stage");

    if (imagePath && imagePath.trim().length > 0) {
        if (imgElem) {
            imgElem.src = imagePath;
            imgElem.classList.remove("d-none");
        }
        if (noImageState) {
            noImageState.classList.add("d-none");
            noImageState.classList.remove("d-flex");
        }
        if (stageElem) {
            stageElem.classList.remove("no-image");
        }
    } else {
        if (imgElem) {
            imgElem.src = "#";
            imgElem.classList.add("d-none");
        }
        if (noImageState) {
            noImageState.classList.remove("d-none");
            noImageState.classList.add("d-flex");
        }
        if (stageElem) {
            stageElem.classList.add("no-image");
        }
    }

    setTimeout(() => {
        updateStageAndMarkerLayerBounds();
    }, 50);
}

function initStageResizer() {
    const imgElem = document.getElementById("floorPlanImage");
    if (imgElem) {
        imgElem.addEventListener("load", () => {
            updateStageAndMarkerLayerBounds();
        });
    }

    window.addEventListener("resize", () => {
        updateStageAndMarkerLayerBounds();
    });

    updateStageAndMarkerLayerBounds();
}

function updateStageAndMarkerLayerBounds() {
    const img = document.getElementById("floorPlanImage");
    const stage = document.getElementById("floor-plan-stage") || document.getElementById("floorPlanStage");
    const markerLayer = document.getElementById("mapMarkerLayer");
    const wrapper = document.getElementById("floor-map-wrapper");

    if (!wrapper) return;

    if (!img || img.classList.contains("d-none") || !img.getAttribute("src") || img.getAttribute("src") === "#") {
        if (stage) {
            stage.style.width = "100%";
            stage.style.height = "100%";
        }
        if (markerLayer) {
            markerLayer.style.width = "100%";
            markerLayer.style.height = "100%";
            markerLayer.style.left = "0px";
            markerLayer.style.top = "0px";
        }
        return;
    }

    const wrapperRect = wrapper.getBoundingClientRect();
    const containerWidth = wrapperRect.width;
    let containerHeight = wrapperRect.height;

    if (containerHeight <= 0) {
        containerHeight = wrapper.offsetHeight || 440;
    }

    if (containerWidth <= 0 || containerHeight <= 0) return;

    const naturalWidth = img.naturalWidth || img.width;
    const naturalHeight = img.naturalHeight || img.height;

    if (!naturalWidth || !naturalHeight) return;

    // Calculate exact aspect-contain ratio (zero cropping)
    const scale = Math.min(containerWidth / naturalWidth, containerHeight / naturalHeight);
    if (scale <= 0) return;

    const renderedWidth = Math.floor(naturalWidth * scale);
    const renderedHeight = Math.floor(naturalHeight * scale);

    if (renderedWidth <= 0 || renderedHeight <= 0) return;

    if (stage) {
        stage.style.width = renderedWidth + "px";
        stage.style.height = renderedHeight + "px";
    }

    if (img) {
        img.style.width = renderedWidth + "px";
        img.style.height = renderedHeight + "px";
    }

    if (markerLayer) {
        markerLayer.style.width = renderedWidth + "px";
        markerLayer.style.height = renderedHeight + "px";
        markerLayer.style.left = "0px";
        markerLayer.style.top = "0px";
    }
}

// ==========================================================================
// MODAL DIALOG HANDLERS & AJAX SUBMISSIONS
// ==========================================================================

function initModalHandlers() {
    const uploadModalElem = document.getElementById("uploadFloorMapModal");
    if (uploadModalElem) {
        uploadModalElem.addEventListener("show.bs.modal", () => {
            const buildingSelect = document.getElementById("building-filter");
            const floorSelect = document.getElementById("floor-filter");
            const targetText = document.getElementById("upload-map-target-text");
            if (targetText && buildingSelect && floorSelect) {
                targetText.textContent = `${buildingSelect.value || "Building"} / ${floorSelect.value || "Floor"}`;
            }
        });
    }

    const formUploadMap = document.getElementById("form-upload-floor-map");
    if (formUploadMap) {
        formUploadMap.addEventListener("submit", async (e) => {
            e.preventDefault();
            await handleUploadFloorMapSubmit();
        });
    }
}

async function handleUploadFloorMapSubmit() {
    const fileInput = document.getElementById("floor-map-file-input");
    const errorElem = document.getElementById("upload-map-error");
    const submitBtn = document.getElementById("btn-submit-upload-map");

    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");

    if (!fileInput || !fileInput.files || fileInput.files.length === 0) {
        showError(errorElem, "Please select an image file to upload.");
        return;
    }

    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    let floorId = null;
    if (floorSelect && floorSelect.selectedIndex >= 0) {
        const selectedOpt = floorSelect.options[floorSelect.selectedIndex];
        if (selectedOpt) floorId = selectedOpt.getAttribute("data-floor-id");
    }

    if (!floorId && floorMapData.floorList) {
        const match = floorMapData.floorList.find(f =>
            f.floorName === selectedFloor &&
            (!selectedBuilding || f.buildingName === selectedBuilding)
        );
        if (match) floorId = match.floorId;
    }

    if (!floorId) {
        showError(errorElem, "Could not determine target floor ID. Please try again.");
        return;
    }

    hideError(errorElem);
    if (submitBtn) submitBtn.disabled = true;

    try {
        const formData = new FormData();
        formData.append("file", fileInput.files[0]);

        const response = await fetch(`/api/floors/${floorId}/map-image`, {
            method: "POST",
            body: formData
        });

        const data = await response.json();

        if (!response.ok) {
            showError(errorElem, data.message || "Failed to upload floor map image.");
            if (submitBtn) submitBtn.disabled = false;
            return;
        }

        const targetFloor = floorMapData.floorList.find(f => f.floorId === parseInt(floorId, 10));
        if (targetFloor) {
            targetFloor.floorMapImagePath = data.floorMapImagePath;
        }

        formResetAndHideModal("uploadFloorMapModal", "form-upload-floor-map");

        populateFloorFilter();
        if (floorSelect) floorSelect.value = selectedFloor;
        updateFloorMapImageBackground();

        showToast("Floor map image uploaded successfully!");
    } catch (err) {
        console.error("Error uploading floor map image:", err);
        showError(errorElem, "An unexpected error occurred while uploading.");
    } finally {
        if (submitBtn) submitBtn.disabled = false;
    }
}

function showError(elem, message) {
    if (elem) {
        elem.textContent = message;
        elem.classList.remove("d-none");
    }
}

function hideError(elem) {
    if (elem) {
        elem.textContent = "";
        elem.classList.add("d-none");
    }
}

function formResetAndHideModal(modalId, formId) {
    const formElem = document.getElementById(formId);
    if (formElem) formElem.reset();

    const modalElem = document.getElementById(modalId);
    if (modalElem && typeof bootstrap !== "undefined") {
        const modalInstance = bootstrap.Modal.getInstance(modalElem);
        if (modalInstance) modalInstance.hide();
    }
}

// ==========================================================================
// BLE ESTIMATED DISTANCE CALCULATION
// ==========================================================================

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

function initSearchFilter() {
    const searchInput = document.getElementById("beacon-search-input");
    if (!searchInput) return;

    searchInput.addEventListener("input", (e) => {
        activeSearchQuery = (e.target.value || "").trim().toLowerCase();
        applyMapFilters();
    });
}

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

    signalRConnection.on("BeaconUpdate", (data) => {
        if (!data) return;

        if (data.macAddress && data.macAddress.startsWith("00:11:22:33:44")) {
            return;
        }

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
    const textElem = document.getElementById("signalr-status-text");
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

    let selectedBuildingId = null;
    let selectedBuildingName = "";
    if (buildingSelect && buildingSelect.selectedIndex >= 0) {
        selectedBuildingName = buildingSelect.value || "";
        const opt = buildingSelect.options[buildingSelect.selectedIndex];
        if (opt) {
            const bIdAttr = opt.getAttribute("data-building-id");
            if (bIdAttr) selectedBuildingId = parseInt(bIdAttr, 10);
        }
    }

    let selectedFloorId = null;
    let selectedFloorName = "";
    if (floorSelect && floorSelect.selectedIndex >= 0) {
        selectedFloorName = floorSelect.value || "";
        const opt = floorSelect.options[floorSelect.selectedIndex];
        if (opt) {
            const fIdAttr = opt.getAttribute("data-floor-id");
            if (fIdAttr) selectedFloorId = parseInt(fIdAttr, 10);
        }
    }

    if (beacon.scannerId) {
        const sc = floorMapData.scanners.find(s => s.scannerId === beacon.scannerId || s.accessPointId === beacon.scannerId);
        if (sc) {
            beacon.buildingId = sc.buildingId;
            beacon.floorId = sc.floorId;
            if (sc.building) beacon.building = sc.building;
            if (sc.floor) beacon.floor = sc.floor;
        }
    }

    const matchesBuilding = isBuildingMatch(beacon, selectedBuildingId, selectedBuildingName);
    const matchesFloor = isFloorMatch(beacon, selectedFloorId, selectedFloorName);

    const isVisible = matchesBuilding && matchesFloor && matchesSearchQuery(beacon);

    updateSingleBeaconListItem(beacon, isVisible);
    updateSingleBeaconMarker(beacon, isVisible);

    if (String(selectedBeaconId) === String(beacon.beaconId || getSafeDomId(beacon))) {
        const markerElem = document.getElementById(`map-marker-${getSafeDomId(beacon)}`);
        updatePopupContent(beacon, markerElem);
    }

    updateStatusBar();
}

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

function updateSingleBeaconMarker(beacon, isVisible) {
    const safeId = getSafeDomId(beacon);
    const beaconIdStr = String(beacon.beaconId || safeId);
    let markerElem = document.getElementById(`map-marker-${safeId}`);

    if (!isVisible) {
        if (markerElem) markerElem.remove();
        return;
    }

    const layer = document.getElementById("mapMarkerLayer") || document.getElementById("floor-map-wrapper");
    if (!layer) return;

    let x = null;
    let y = null;

    if (beacon.scannerId) {
        const scanner = floorMapData.scanners.find(s => s.scannerId === beacon.scannerId);
        if (scanner) {
            x = scanner.mapXPercent;
            y = scanner.mapYPercent;
            if (x === null || x === undefined || y === null || y === undefined) {
                const scannerPos = getPositionByLocation(scanner.location, scanner.scannerId);
                if (scannerPos) {
                    x = scannerPos.x;
                    y = scannerPos.y;
                }
            }
        }
    }

    if (x === null || y === null) {
        const pos = getPositionByLocation(beacon.location, beacon.scannerId);
        if (pos) {
            x = pos.x;
            y = pos.y;
        } else {
            x = 50;
            y = 50;
        }
    }

    const baseIdx = floorMapData.beacons.findIndex(b => b.beaconId === beacon.beaconId || b.macAddress === beacon.macAddress);
    if (baseIdx >= 0) {
        const angle = baseIdx * (2 * Math.PI / 8) + Math.PI / 4;
        const radiusX = 3.5;
        const radiusY = 3.5;
        x += Math.cos(angle) * radiusX;
        y += Math.sin(angle) * radiusY;
    }

    const isSelected = beaconIdStr === String(selectedBeaconId);
    const statusText = beacon.status || (beacon.isOnline ? "Online" : "Offline");
    const statusClass = getStatusClass(statusText, beacon.isOnline);
    const shortLabel = getBeaconShortLabel(beacon);

    const innerHtml = `
        <span>${escapeHtml(shortLabel)}</span>
        ${createTooltipHtml(beacon)}
    `;

    if (markerElem) {
        if (markerElem.parentElement !== layer) {
            layer.appendChild(markerElem);
        }

        markerElem.className = `map-marker beacon-marker ${statusClass} ${isSelected ? "selected" : ""}`;
        markerElem.style.position = "absolute";
        markerElem.style.left = `${x}%`;
        markerElem.style.top = `${y}%`;
        markerElem.style.transform = "translate(-50%, -50%)";
        markerElem.innerHTML = innerHtml;

        markerElem.classList.remove("marker-flash");
        void markerElem.offsetWidth;
        markerElem.classList.add("marker-flash");
    } else {
        markerElem = document.createElement("div");
        markerElem.id = `map-marker-${safeId}`;
        markerElem.className = `map-marker beacon-marker ${statusClass} ${isSelected ? "selected" : ""} marker-flash`;
        markerElem.setAttribute("data-beacon-id", beaconIdStr);
        markerElem.style.position = "absolute";
        markerElem.style.left = `${x}%`;
        markerElem.style.top = `${y}%`;
        markerElem.style.transform = "translate(-50%, -50%)";
        markerElem.innerHTML = innerHtml;

        markerElem.addEventListener("click", (e) => {
            e.stopPropagation();
            highlightBeacon(beaconIdStr);
        });

        layer.appendChild(markerElem);
    }
}

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
// FLOATING DETAIL POPUP HANDLERS
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

    if (markerElem) {
        positionBeaconPopup(markerElem);
    }
}

function positionBeaconPopup(markerElem) {
    const popup = document.getElementById("floormap-beacon-popup");
    const wrapper = document.getElementById("floor-map-wrapper");
    if (!popup || !wrapper || !markerElem) return;

    const wrapperRect = wrapper.getBoundingClientRect();
    const markerRect = markerElem.getBoundingClientRect();

    const popupWidth = popup.offsetWidth || 320;
    const popupHeight = popup.offsetHeight || 220;

    let left = markerRect.right - wrapperRect.left + 12;
    let top = markerRect.top - wrapperRect.top - 10;

    if (left + popupWidth > wrapperRect.width - 15) {
        left = markerRect.left - wrapperRect.left - popupWidth - 12;
    }

    left = Math.max(10, Math.min(left, wrapperRect.width - popupWidth - 10));
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
// MAP FILTER EXECUTION
// ==========================================================================

function isBuildingMatch(item, selectedBuildingId, selectedBuildingName) {
    if (!selectedBuildingName && (selectedBuildingId === null || selectedBuildingId === undefined)) return true;
    if (item.buildingId !== null && item.buildingId !== undefined && selectedBuildingId !== null && selectedBuildingId !== undefined) {
        return item.buildingId === selectedBuildingId;
    }
    if (item.building && selectedBuildingName) {
        const str1 = String(item.building).trim().toLowerCase();
        const str2 = String(selectedBuildingName).trim().toLowerCase();
        return str1 === str2 || str1.includes(str2) || str2.includes(str1);
    }
    return false;
}

function isFloorMatch(item, selectedFloorId, selectedFloorName) {
    if (!selectedFloorName && (selectedFloorId === null || selectedFloorId === undefined)) return true;
    if (item.floorId !== null && item.floorId !== undefined && selectedFloorId !== null && selectedFloorId !== undefined) {
        return item.floorId === selectedFloorId;
    }
    if (item.floor && selectedFloorName) {
        const str1 = String(item.floor).trim().toLowerCase();
        const str2 = String(selectedFloorName).trim().toLowerCase();
        if (str1 === str2) return true;
        const num1 = str1.replace(/\D/g, '');
        const num2 = str2.replace(/\D/g, '');
        if (num1 && num2 && num1 === num2) return true;
    }
    return false;
}

function applyMapFilters() {
    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");

    let selectedBuildingId = null;
    let selectedBuildingName = "";
    if (buildingSelect && buildingSelect.selectedIndex >= 0) {
        selectedBuildingName = buildingSelect.value || "";
        const opt = buildingSelect.options[buildingSelect.selectedIndex];
        if (opt) {
            const bIdAttr = opt.getAttribute("data-building-id");
            if (bIdAttr) selectedBuildingId = parseInt(bIdAttr, 10);
        }
    }

    let selectedFloorId = null;
    let selectedFloorName = "";
    if (floorSelect && floorSelect.selectedIndex >= 0) {
        selectedFloorName = floorSelect.value || "";
        const opt = floorSelect.options[floorSelect.selectedIndex];
        if (opt) {
            const fIdAttr = opt.getAttribute("data-floor-id");
            if (fIdAttr) selectedFloorId = parseInt(fIdAttr, 10);
        }
    }

    // Always clear all DOM markers before re-rendering for the selected floor
    clearAllMarkers();

    const filteredScanners = floorMapData.scanners.filter(s => {
        const matchBuilding = isBuildingMatch(s, selectedBuildingId, selectedBuildingName);
        const matchFloor = isFloorMatch(s, selectedFloorId, selectedFloorName);
        return matchBuilding && matchFloor;
    });

    const filteredBeacons = floorMapData.beacons.filter(b => {
        if (b.scannerId) {
            const sc = floorMapData.scanners.find(s => s.scannerId === b.scannerId || s.accessPointId === b.scannerId);
            if (sc) {
                b.buildingId = sc.buildingId;
                b.floorId = sc.floorId;
                if (sc.building) b.building = sc.building;
                if (sc.floor) b.floor = sc.floor;
            }
        }
        const matchBuilding = isBuildingMatch(b, selectedBuildingId, selectedBuildingName);
        const matchFloor = isFloorMatch(b, selectedFloorId, selectedFloorName);
        return matchBuilding && matchFloor && matchesSearchQuery(b);
    });

    renderBeaconList(filteredBeacons);
    renderScannerMarkers(filteredScanners);
    renderBeaconMarkers(filteredBeacons);

    updateStageAndMarkerLayerBounds();

    updateStatusBar();
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
    const layer = document.getElementById("mapMarkerLayer");
    if (layer) {
        layer.innerHTML = "";
    }
}

function renderBeaconMarkers(filteredBeacons) {
    if (!filteredBeacons) return;
    filteredBeacons.forEach(beacon => {
        updateSingleBeaconMarker(beacon, true);
    });
}

function renderScannerMarkers(filteredScanners) {
    // CRITICAL FIX: Do NOT re-render AP DOM during Edit Mode (protects marker being dragged)
    if (isEditModeActive) {
        return;
    }

    clearAllMarkers();

    const warningContainer = document.getElementById("unmapped-warnings");
    const warningText = document.getElementById("unmapped-warnings-text");
    if (warningContainer) {
        warningContainer.classList.add("d-none");
        if (warningText) warningText.innerHTML = "";
    }
    const unmappedScanners = [];

    const layer = document.getElementById("mapMarkerLayer") || document.getElementById("floor-map-wrapper");
    if (!layer) return;

    const occupiedPositions = {};

    filteredScanners.forEach((scanner, index) => {
        let x = scanner.mapXPercent;
        let y = scanner.mapYPercent;

        if (x === null || x === undefined || y === null || y === undefined) {
            const apId = scanner.scannerId || scanner.accessPointId;
            const pos = getPositionByLocation(scanner.location, apId);
            if (pos) {
                x = pos.x;
                y = pos.y;
            } else {
                x = 25 + (index * 20) % 60;
                y = 35 + (index * 15) % 40;
                unmappedScanners.push(scanner);
            }
        }

        if (!isEditModeActive) {
            const posKey = `${x},${y}`;
            const count = occupiedPositions[posKey] || 0;
            occupiedPositions[posKey] = count + 1;

            if (count > 0) {
                const angle = (count - 1) * (2 * Math.PI / 8);
                const radiusX = 3.5;
                const radiusY = 3.5;
                x += Math.cos(angle) * radiusX;
                y += Math.sin(angle) * radiusY;
            }
        }

        const apId = scanner.scannerId || scanner.accessPointId;
        const scannerName = scanner.scannerName || scanner.accessPointName || apId || "Unknown Access Point";
        const locationText = scanner.location || "Unknown Location";
        const shortLabel = getScannerShortLabel(scanner, index);

        const scannerElem = document.createElement("div");
        scannerElem.className = "map-marker ap-marker scanner-marker";
        scannerElem.setAttribute("data-scanner-id", apId);
        scannerElem.setAttribute("data-access-point-id", apId);
        scannerElem.setAttribute("title", `Access Point: ${scannerName} (${locationText})`);
        scannerElem.style.position = "absolute";
        scannerElem.style.left = `${x}%`;
        scannerElem.style.top = `${y}%`;
        scannerElem.style.transform = "translate(-50%, -50%)";

        scannerElem.innerHTML = `<span>${escapeHtml(shortLabel)}</span>`;

        layer.appendChild(scannerElem);
    });

    if (unmappedScanners.length > 0 && warningContainer && warningText) {
        const names = unmappedScanners.map(s => s.scannerName || s.accessPointName || s.scannerId || s.accessPointId).join(", ");
        warningText.innerHTML = `<strong>Warning:</strong> The following Access Points have no mapped position or location fallback: ${escapeHtml(names)}`;
        warningContainer.classList.remove("d-none");
    }
}

// ==========================================================================
// ADMIN EDIT MODE HANDLERS & POSITION SAVING
// ==========================================================================

function initEditModeHandlers() {
    const btnEditMode = document.getElementById("btn-edit-mode");
    const btnSavePositions = document.getElementById("btn-save-positions");
    const btnCancelPositions = document.getElementById("btn-cancel-positions");

    if (btnEditMode) {
        btnEditMode.addEventListener("click", () => {
            enterEditMode();
        });
    }

    if (btnSavePositions) {
        btnSavePositions.addEventListener("click", () => {
            saveEditedPositions();
        });
    }

    if (btnCancelPositions) {
        btnCancelPositions.addEventListener("click", () => {
            cancelEditMode();
        });
    }
}

function enterEditMode() {
    isEditModeActive = true;
    originalScannerPositions = {};
    pendingChanges = {};

    floorMapData.scanners.forEach(s => {
        originalScannerPositions[s.scannerId] = {
            mapXPercent: s.mapXPercent,
            mapYPercent: s.mapYPercent
        };
    });

    document.body.classList.add("ap-editing");
    const mapContainer = document.getElementById("floor-map-wrapper");
    if (mapContainer) {
        mapContainer.classList.add("edit-mode-active");
    }

    const editModeIndicator = document.getElementById("edit-mode-indicator");
    if (editModeIndicator) {
        editModeIndicator.classList.remove("d-none");
        editModeIndicator.classList.add("d-flex");
    }

    const btnEditMode = document.getElementById("btn-edit-mode");
    if (btnEditMode) {
        btnEditMode.classList.add("d-none");
    }

    hideBeaconPopup();

    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    const filteredScanners = floorMapData.scanners.filter(s => {
        const matchBuilding = !selectedBuilding || s.building === selectedBuilding;
        const matchFloor = !selectedFloor || s.floor === selectedFloor;
        return matchBuilding && matchFloor;
    });

    // Force initial render of AP markers for edit mode
    isEditModeActive = false;
    renderScannerMarkers(filteredScanners);
    isEditModeActive = true;

    console.log("Edit AP Mode activated | Scanners count:", floorMapData.scanners.length);
}

function cancelEditMode() {
    isEditModeActive = false;

    floorMapData.scanners.forEach(s => {
        const orig = originalScannerPositions[s.scannerId];
        if (orig) {
            s.mapXPercent = orig.mapXPercent;
            s.mapYPercent = orig.mapYPercent;
        }
    });

    originalScannerPositions = {};
    pendingChanges = {};

    document.body.classList.remove("ap-editing");
    const mapContainer = document.getElementById("floor-map-wrapper");
    if (mapContainer) {
        mapContainer.classList.remove("edit-mode-active");
    }

    const editModeIndicator = document.getElementById("edit-mode-indicator");
    if (editModeIndicator) {
        editModeIndicator.classList.add("d-none");
        editModeIndicator.classList.remove("d-flex");
    }

    const btnEditMode = document.getElementById("btn-edit-mode");
    if (btnEditMode) {
        btnEditMode.classList.remove("d-none");
    }

    applyMapFilters();
}

async function saveEditedPositions() {
    const btnSave = document.getElementById("btn-save-positions");
    const btnCancel = document.getElementById("btn-cancel-positions");

    if (btnSave) btnSave.disabled = true;
    if (btnCancel) btnCancel.disabled = true;

    const pendingList = Object.values(pendingChanges);

    if (pendingList.length === 0) {
        exitEditModeAfterSaveSuccess();
        return;
    }

    try {
        const batchPayload = pendingList.map(item => ({
            scannerId: item.scannerId,
            mapXPercent: item.mapXPercent,
            mapYPercent: item.mapYPercent
        }));

        let response = await fetch('/api/accesspoints/map-positions', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(batchPayload)
        });

        if (!response.ok) {
            let hasErrors = false;
            for (const item of pendingList) {
                const putRes = await fetch(`/api/accesspoints/${encodeURIComponent(item.scannerId)}/position`, {
                    method: 'PUT',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ xPercent: item.mapXPercent, yPercent: item.mapYPercent })
                });
                if (!putRes.ok) hasErrors = true;
            }
            if (hasErrors) throw new Error("Failed to save AP positions.");
        }

        showToast("Access Point positions saved successfully!");
        exitEditModeAfterSaveSuccess();
    } catch (err) {
        console.error("Error saving positions:", err);
        showToast("Failed to save Access Point positions.", false);
        cancelEditMode();
    } finally {
        if (btnSave) btnSave.disabled = false;
        if (btnCancel) btnCancel.disabled = false;
    }
}

function exitEditModeAfterSaveSuccess() {
    isEditModeActive = false;
    originalScannerPositions = {};
    pendingChanges = {};

    document.body.classList.remove("ap-editing");
    const mapContainer = document.getElementById("floor-map-wrapper");
    if (mapContainer) {
        mapContainer.classList.remove("edit-mode-active");
    }

    const editModeIndicator = document.getElementById("edit-mode-indicator");
    if (editModeIndicator) {
        editModeIndicator.classList.add("d-none");
        editModeIndicator.classList.remove("d-flex");
    }

    const btnEditMode = document.getElementById("btn-edit-mode");
    if (btnEditMode) {
        btnEditMode.classList.remove("d-none");
    }

    const btnSave = document.getElementById("btn-save-positions");
    const btnCancel = document.getElementById("btn-cancel-positions");
    if (btnSave) btnSave.disabled = false;
    if (btnCancel) btnCancel.disabled = false;

    applyMapFilters();
}

// ==========================================================================
// POINTER DRAG EVENT DELEGATION FOR AP MARKERS
// ==========================================================================

function initApPointerDragListeners() {
    const markerLayer = document.getElementById("mapMarkerLayer") || document.getElementById("floor-map-wrapper");
    if (!markerLayer) return;

    markerLayer.addEventListener("pointerdown", onApPointerDown);
    document.addEventListener("pointermove", onPointerMove, { passive: false });
    document.addEventListener("pointerup", onPointerUp);
    document.addEventListener("pointercancel", onPointerUp);
}

function onApPointerDown(e) {
    console.log("marker layer pointerdown:", e.target, e.target.closest(".ap-marker"));
    console.log("Edit mode status:", isEditModeActive);

    if (!isEditModeActive) return;

    const marker = e.target.closest(".ap-marker");
    if (!marker) return;

    if (e.button !== undefined && e.button !== 0) return;

    e.preventDefault();
    e.stopPropagation();

    draggingAp = marker;
    activePointerId = e.pointerId;

    const scannerId = marker.getAttribute("data-scanner-id") || marker.dataset.scannerId;
    dragScannerObj = floorMapData.scanners.find(s => s.scannerId === scannerId);

    marker.classList.add("dragging");
    document.body.classList.add("ap-editing");
    document.body.classList.add("dragging-active");

    try {
        marker.setPointerCapture?.(e.pointerId);
    } catch (err) { }

    console.log("AP drag start", scannerId, e.clientX, e.clientY);
}

function onPointerMove(e) {
    if (!draggingAp) return;
    if (activePointerId !== null && e.pointerId !== activePointerId) return;

    e.preventDefault();

    const imgElem = document.getElementById("floorPlanImage");
    const layerElem = document.getElementById("mapMarkerLayer") || document.getElementById("floor-map-wrapper");

    const targetRect = (imgElem && !imgElem.classList.contains("d-none") && imgElem.offsetWidth > 0)
        ? imgElem.getBoundingClientRect()
        : layerElem.getBoundingClientRect();

    if (!targetRect || targetRect.width === 0 || targetRect.height === 0) return;

    let x = e.clientX - targetRect.left;
    let y = e.clientY - targetRect.top;

    x = Math.max(0, Math.min(targetRect.width, x));
    y = Math.max(0, Math.min(targetRect.height, y));

    let xPercent = (x / targetRect.width) * 100;
    let yPercent = (y / targetRect.height) * 100;

    xPercent = Math.round(xPercent * 10) / 10;
    yPercent = Math.round(yPercent * 10) / 10;

    draggingAp.style.left = `${xPercent}%`;
    draggingAp.style.top = `${yPercent}%`;

    const scannerId = draggingAp.getAttribute("data-scanner-id") || draggingAp.dataset.scannerId;
    if (scannerId) {
        pendingChanges[scannerId] = {
            scannerId: scannerId,
            mapXPercent: xPercent,
            mapYPercent: yPercent,
            xPercent: xPercent,
            yPercent: yPercent
        };
        if (dragScannerObj) {
            dragScannerObj.mapXPercent = xPercent;
            dragScannerObj.mapYPercent = yPercent;
        }
        updateAssociatedBeaconsPositions(scannerId, xPercent, yPercent);
    }

    console.log("new AP position:", scannerId, xPercent, yPercent);
}

function onPointerUp(e) {
    if (!draggingAp) return;

    const scannerId = draggingAp.getAttribute("data-scanner-id") || draggingAp.dataset.scannerId;

    draggingAp.classList.remove("dragging");
    document.body.classList.remove("ap-editing");
    document.body.classList.remove("dragging-active");

    try {
        draggingAp.releasePointerCapture?.(e.pointerId);
    } catch (err) { }

    console.log("AP drag end", scannerId, pendingChanges[scannerId]);

    draggingAp = null;
    activePointerId = null;
    dragScannerObj = null;

    const buildingSelect = document.getElementById("building-filter");
    const floorSelect = document.getElementById("floor-filter");
    const selectedBuilding = buildingSelect ? buildingSelect.value : "";
    const selectedFloor = floorSelect ? floorSelect.value : "";

    const filteredBeacons = floorMapData.beacons.filter(b =>
        (!selectedBuilding || b.building === selectedBuilding) &&
        (!selectedFloor || b.floor === selectedFloor) &&
        matchesSearchQuery(b)
    );
    renderBeaconMarkers(filteredBeacons);
}

function updateAssociatedBeaconsPositions(scannerId, scannerX, scannerY) {
    floorMapData.beacons.forEach((beacon) => {
        if (beacon.scannerId === scannerId) {
            const safeId = getSafeDomId(beacon);
            const markerElem = document.getElementById(`map-marker-${safeId}`);
            if (markerElem) {
                const baseIdx = floorMapData.beacons.findIndex(b => b.beaconId === beacon.beaconId || b.macAddress === beacon.macAddress);
                let x = scannerX;
                let y = scannerY;
                if (baseIdx >= 0) {
                    const angle = baseIdx * (2 * Math.PI / 8) + Math.PI / 4;
                    const radiusX = 3.5;
                    const radiusY = 3.5;
                    x += Math.cos(angle) * radiusX;
                    y += Math.sin(angle) * radiusY;
                }

                markerElem.style.left = `${x}%`;
                markerElem.style.top = `${y}%`;
            }
        }
    });
}

function renderBeaconMarkers(filteredBeacons) {
    filteredBeacons.forEach(beacon => {
        updateSingleBeaconMarker(beacon, true);
    });
}

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
// BIDIRECTIONAL SELECTION SYNCHRONIZATION
// ==========================================================================

function highlightBeacon(beaconIdStr) {
    if (selectedBeaconId === beaconIdStr) {
        hideBeaconPopup();
        selectedBeaconId = null;
        clearSelections();
        return;
    }

    selectedBeaconId = beaconIdStr;

    document.querySelectorAll(".beacon-card-item").forEach(card => {
        if (card.getAttribute("data-beacon-id") === String(selectedBeaconId)) {
            card.classList.add("selected");
            card.scrollIntoView({ behavior: "smooth", block: "nearest" });
        } else {
            card.classList.remove("selected");
        }
    });

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

    const beacon = floorMapData.beacons.find(b => String(b.beaconId) === String(selectedBeaconId) || getSafeDomId(b) === String(selectedBeaconId));
    if (beacon) {
        updatePopupContent(beacon, selectedMarkerElem);
    } else {
        hideBeaconPopup();
    }
}

function clearSelections() {
    document.querySelectorAll(".beacon-card-item").forEach(c => c.classList.remove("selected"));
    document.querySelectorAll(".beacon-marker").forEach(m => m.classList.remove("selected"));
}

// ==========================================================================
// OFFLINE TIMEOUT CHECKER
// ==========================================================================

function initOfflineTimeoutChecker() {
    setInterval(() => {
        const now = new Date();
        let stateChanged = false;

        floorMapData.beacons.forEach(beacon => {
            if (beacon.rawLastSeen) {
                const diffSecs = (now - beacon.rawLastSeen) / 1000;
                beacon.lastSeen = formatRelativeTime(diffSecs);

                const wasOnline = beacon.isOnline;
                beacon.isOnline = diffSecs < 60;
                beacon.status = beacon.isOnline ? (beacon.isMoving ? "Moving" : "Online") : "Offline";

                if (wasOnline !== beacon.isOnline) {
                    stateChanged = true;
                }
            }
        });

        if (stateChanged) {
            applyMapFilters();
        } else {
            updateLastSeenDisplayInList();
        }
    }, 5000);
}

function updateLastSeenDisplayInList() {
    floorMapData.beacons.forEach(beacon => {
        const safeId = getSafeDomId(beacon);
        const cardElem = document.getElementById(`beacon-card-item-${safeId}`);
        if (cardElem) {
            const timeElem = cardElem.querySelector(".last-seen-val");
            if (timeElem && beacon.lastSeen) {
                timeElem.textContent = beacon.lastSeen;
            }
        }
    });
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
