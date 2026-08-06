// BLE Asset Tracking System - Global Ticker
document.addEventListener("DOMContentLoaded", function() {
    setInterval(function() {
        const timeEl = document.getElementById("system-last-updated");
        if (timeEl && window.lastSystemUpdateTime) {
            const diffSeconds = Math.round((Date.now() - window.lastSystemUpdateTime) / 1000);
            
            let statusText = "Updated just now";
            if (diffSeconds > 0) {
                if (diffSeconds < 60) {
                    statusText = `Updated ${diffSeconds}s ago`;
                } else {
                    const minutes = Math.floor(diffSeconds / 60);
                    const seconds = diffSeconds % 60;
                    statusText = `Updated ${minutes}m ${seconds}s ago`;
                }
            }
            timeEl.textContent = statusText;

            // If no data received for 35 seconds, set status indicator to Inactive
            const dotEl = document.getElementById("system-status-dot");
            const labelEl = document.getElementById("system-status-label");
            if (dotEl && labelEl) {
                if (diffSeconds > 35) {
                    dotEl.className = "status-badge-dot connecting";
                    labelEl.textContent = "Inactive";
                } else {
                    dotEl.className = "status-badge-dot";
                    labelEl.textContent = "Connected";
                }
            }
        }
    }, 1000);
});
