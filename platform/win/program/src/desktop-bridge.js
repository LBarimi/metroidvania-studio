
// This module extension is applied only to the Windows desktop build.
window.metroidvaniaDesktop = {
  async prepareClose() {
    document.body.inert = true;
    await settleFileSnapshot();
    if (!api.online || api.pending || map.hasPendingWork) throw new Error('Editing has not finished. Please retry closing after the current action completes.');
    return { ok: true, revision: state?.documentRevision };
  },
  resume() { document.body.inert = false; }
};
if (new URLSearchParams(location.search).has('desktop-test')) {
  window.metroidvaniaDesktop.test = { api, map, mini, get state() { return state; } };
}

// Retain the desktop description; the common dialog owns copyright and contact text.
const desktopShowModal = showModal;
showModal = (...args) => {
  const dialog = desktopShowModal(...args);
  if (dialog.querySelector('.about-heading')) {
    const description = dialog.querySelector('.about-heading + p');
    const translatedDescription = desktopLabels.aboutDescription?.[locale.language];
    if (description && translatedDescription) description.textContent = translatedDescription;
  }
  return dialog;
};
