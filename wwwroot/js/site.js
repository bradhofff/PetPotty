// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// JavaScript Date#getTimezoneOffset returns UTC - local time in minutes. The
// server uses this only to translate browser-local form values and legacy local
// records; it is not an authentication or authorization value.
(() => {
    const offset = new Date().getTimezoneOffset();
    const cookieName = 'PetPottyUtcOffsetMinutes';
    const current = document.cookie
        .split('; ')
        .find(value => value.startsWith(`${cookieName}=`))
        ?.split('=')[1];

    if (current !== String(offset)) {
        document.cookie = `${cookieName}=${offset}; path=/; max-age=31536000; samesite=lax`;
    }

    document.querySelectorAll('[data-utc-offset]').forEach(input => {
        input.value = String(offset);
    });
})();

// Animate the same range control on Home and Medications, then post its existing form.
(() => {
    document.querySelectorAll('body.app-ui .seg-toggle').forEach(toggle => {
        const activeRange = toggle.querySelector('.seg-option.seg-active')?.form
            .querySelector('[name="showAllTime"]');
        toggle.classList.toggle('is-all-time', activeRange?.value === 'true');
        toggle.querySelectorAll('form').forEach(form => {
            form.addEventListener('submit', event => {
                event.preventDefault();
                const showAllTime = form.querySelector('[name="showAllTime"]').value === 'true';
                toggle.classList.toggle('is-all-time', showAllTime);
                toggle.querySelectorAll('.seg-option').forEach(button => {
                    const active = button.form === form;
                    button.classList.toggle('seg-active', active);
                    button.setAttribute('aria-pressed', String(active));
                    button.disabled = true;
                });
                const delay = window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 0 : 180;
                window.setTimeout(() => form.submit(), delay);
            });
        });
    });
})();
