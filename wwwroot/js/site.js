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
