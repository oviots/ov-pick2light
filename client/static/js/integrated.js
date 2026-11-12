/* Loaded only by the UI embedded in p2l. The Bridge owns license, Keychain and MQTT. */
'use strict';

// Keep existing device/picking views; no second process or editable connection settings.
window.saveConfig = async () => toast('Cấu hình được quản lý trên authen.', 'info');
window.loadAppsettings = async () => {};
window.refreshP2lServiceBadge = async () => {
  const badge = $('p2l-svc-badge');
  if (badge) { badge.textContent = 'Đang chạy trong p2l'; badge.className = 'status-badge OPERATING'; }
};
window.p2lServiceAction = async () => toast('Bridge đang chạy trong Terminal. Dùng Ctrl+C để dừng.', 'info');
document.addEventListener('DOMContentLoaded', () => {
  document.querySelectorAll('.sub-tab-btn[data-subtab="client"], .sub-tab-btn[data-subtab="bridge"], #sub-pane-client, #sub-pane-bridge')
    .forEach(element => element.remove());
  const scan = $('sub-tab-btn-scan');
  if (scan) {
    scan.dataset.integrated = 'true';
    scan.classList.add('active');
    scan.style.display = '';
  }
  const pane = $('sub-pane-scan');
  if (pane) {
    pane.classList.add('active');
    pane.style.display = 'block';
  }
});
