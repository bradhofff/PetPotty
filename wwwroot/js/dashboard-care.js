// Choose the initial state once; resizing must not undo a manual toggle.
(() => {
    const expanded = window.matchMedia('(min-width: 768px)').matches;
    document.querySelectorAll('.task-log-toggle').forEach(button => {
        const panel = document.getElementById(button.getAttribute('aria-controls'));
        if (!panel) return;
        panel.classList.toggle('show', expanded);
        button.classList.toggle('collapsed', !expanded);
        button.setAttribute('aria-expanded', String(expanded));
    });
})();

// Save only after an explicit confirmation, retaining inputs on failure.
async function saveDashboardForm(form, modal) {
    if (form.dataset.saving) return;
    form.dataset.saving = 'true';
    let feedback = modal.querySelector('[data-save-feedback]');
    if (!feedback) {
        feedback = document.createElement('p');
        feedback.dataset.saveFeedback = '';
        feedback.className = 'alert alert-info m-3';
        feedback.setAttribute('role', 'status');
        modal.querySelector('.modal-content').append(feedback);
    }
    feedback.textContent = 'Saving…';
    const data = new FormData(form);
    data.set('dashboardRequest', 'true');
    const controls = [...modal.querySelectorAll('button, input, select, textarea')].filter(c => !c.disabled);
    controls.forEach(c => c.disabled = true);
    try {
        const response = await fetch(form.action, { method: 'POST', body: data });
        const result = await response.json().catch(() => ({}));
        if (!response.ok || result.saved !== true)
            throw new Error(result.message || 'Could not confirm the save. Refresh and check the history before trying again.');
        // A full refresh also reflects any schedule changes caused by a late dose.
        window.location.hash = modal.dataset.petId ? `pet-reminders-${modal.dataset.petId}` : ''; 
        window.location.reload();
    } catch (error) {
        feedback.className = 'alert alert-warning m-3';
        feedback.textContent = error.message;
        feedback.setAttribute('role', 'alert');
        delete form.dataset.saving;
        controls.forEach(c => c.disabled = false);
        feedback.tabIndex = -1;
        feedback.focus();
    }
}

for (const [selector, prefix, modalId] of [
    ['.confirm-dose-btn', 'confirmDose', 'confirmDoseModal'],
    ['.record-dose-btn', 'recordDose', 'recordDoseModal']
]) {
    const modal = document.getElementById(modalId);
    const form = modal.querySelector('form');
    let trigger = null;
    modal.addEventListener('hidden.bs.modal', () => trigger?.focus());
    document.querySelectorAll(selector).forEach(button => button.addEventListener('click', () => {
        trigger = button;
        modal.dataset.petId = button.dataset.petId;
        form.reset();
        modal.querySelector('[data-save-feedback]')?.remove();
        document.getElementById(prefix + 'MedID').value = button.dataset.medId;
        document.getElementById(prefix + 'LogDate').value = button.dataset.logDate;
        document.getElementById(prefix + 'MedName').value = button.dataset.medName;
        document.getElementById(prefix + 'ScheduledDisplay').value = button.dataset.scheduledDisplay;
        form.querySelector('[data-utc-offset]').value = new Date().getTimezoneOffset();
        if (prefix === 'confirmDose')
            document.getElementById('confirmDoseConfirmedAt').value = formatLocalDateTime(new Date());
    }));
    form.addEventListener('submit', event => {
        event.preventDefault();
        saveDashboardForm(form, modal);
    });
    modal.addEventListener('hide.bs.modal', event => { if (form.dataset.saving) event.preventDefault(); });
}

// Normal pet/task form posts keep their existing redirects and validation.
// Disable all submitters, including buttons associated through a form attribute.
document.querySelectorAll('.home-dashboard form, #addPetModal form, #editPetModal form, #addTaskModal form, #editTaskModal form').forEach(form => {
    form.addEventListener('submit', event => {
        if (event.defaultPrevented) return;
        if (form.dataset.submitting) { event.preventDefault(); return; }
        form.dataset.submitting = 'true';
        [...form.elements].filter(c => c.type === 'submit').forEach(c => c.disabled = true);
    });
});
window.addEventListener('pageshow', () => {
    document.querySelectorAll('form[data-submitting]').forEach(form => {
        delete form.dataset.submitting;
        [...form.elements].filter(c => c.type === 'submit').forEach(c => c.disabled = false);
    });
});
