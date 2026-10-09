(() => {
    const modal = document.getElementById('pet-weight');
    if (!modal) return;
    const open = () => bootstrap.Modal.getOrCreateInstance(modal).show();
    modal.querySelector('[data-weight-cancel]')?.addEventListener('click', () => {
        const editor = document.getElementById('weight-editor');
        if (!editor) return;
        editor.open = false;
        editor.querySelector('summary')?.focus();
    });
    if (modal.dataset.autoOpen === 'true' || location.hash === '#pet-weight') open();
    window.addEventListener('hashchange', () => {
        if (location.hash === '#pet-weight') open();
    });
    modal.addEventListener('hidden.bs.modal', () => {
        if (location.hash === '#pet-weight') history.replaceState(null, '', location.pathname + location.search);
        document.getElementById('weight-overview-card')?.focus();
    });
})();
