(() => {
    document.querySelectorAll('.care-edit-form').forEach(form => {
        let saving = false;
        const modal = form.closest('.modal');
        let shown = false;
        modal.addEventListener('shown.bs.modal', () => { shown = true; });
        modal.addEventListener('hidden.bs.modal', () => { shown = false; });
        modal.addEventListener('hide.bs.modal', event => { if (saving) event.preventDefault(); });
        form.addEventListener('submit', async event => {
            event.preventDefault();
            if (saving || !form.reportValidity()) return;
            const error = form.querySelector('.care-form-error');
            error.textContent = '';
            form.querySelectorAll('[data-error-for]').forEach(el => el.textContent = '');
            form.querySelectorAll('[data-field]').forEach(el => el.removeAttribute('aria-invalid'));
            const body = new FormData(form);
            saving = true;
            form.querySelectorAll('button').forEach(el => el.disabled = true);
            form.setAttribute('aria-busy', 'true');
            try {
                const response = await fetch(form.action, { method: 'POST', body, headers: { 'Accept': 'application/json' } });
                const data = await response.json().catch(() => ({}));
                if (!response.ok || !data.message) {
                    for (const [key, message] of Object.entries(data.errors || {})) {
                        const field = [...form.querySelectorAll('[data-field]')].find(el => el.dataset.field === key);
                        if (field) { field.setAttribute('aria-invalid', 'true'); document.getElementById(`care-error-${key}`).textContent = message; }
                    }
                    throw new Error(data.message || 'Could not save. Your entries are still here. Check your access and try again.');
                }
                const previews = [];
                form.querySelectorAll('[data-field]').forEach(field => {
                    const value = field.value.trim();
                    field.value = value;
                    document.querySelector(`[data-care-value="${field.dataset.field}"]`).textContent = value || 'Not recorded';
                    if (value) previews.push(`${field.dataset.label}: ${value}`);
                });
                const preview = previews.join(' · ');
                document.querySelector(`#care-section-${form.dataset.section} .care-preview`).textContent = preview.length > 140 ? preview.slice(0, 140) + '…' : preview || 'Not recorded';
                document.getElementById('care-save-status').textContent = data.message;
                saving = false;
                const close = () => bootstrap.Modal.getOrCreateInstance(modal).hide();
                if (shown) close();
                else modal.addEventListener('shown.bs.modal', close, { once: true });
            } catch (failure) {
                error.textContent = failure.message || 'Could not save. Your entries are still here.';
                error.focus();
            } finally {
                saving = false;
                form.removeAttribute('aria-busy');
                form.querySelectorAll('button').forEach(el => el.disabled = false);
            }
        });
    });
})();
