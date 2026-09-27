(() => {
    'use strict';
    const root = document.querySelector('.pack-landing');
    if (!root || !('IntersectionObserver' in window)) return;
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const desktop = window.matchMedia('(min-width: 901px)');
    const chapters = [...root.querySelectorAll('[data-chapter]')];
    const screens = [...root.querySelectorAll('[data-screen]')];
    const labels = ['Daily care', 'Health history', 'Medications', 'Vet visits'];
    const stageLabel = root.querySelector('[data-stage-label]');
    const stageCount = root.querySelector('[data-stage-count]');
    const progress = root.querySelector('[data-tour-progress]');
    const hero = root.querySelector('[data-hero-product]');
    let active = -1;
    let frame = 0;
    let observer;

    function update() {
        frame = 0;
        if (reducedMotion.matches || !desktop.matches) {
            hero.style.removeProperty('transform');
            return;
        }
        // Read geometry together, then update transforms; native scrolling stays intact.
        const center = window.innerHeight * .52;
        const positions = chapters.map(chapter => chapter.getBoundingClientRect());
        let closest = 0;
        positions.forEach((position, index) => {
            if (position.top < center) closest = index;
        });
        if (closest !== active) {
            active = closest;
            screens.forEach((screen, index) => screen.classList.toggle('is-active', index === active));
            stageLabel.textContent = labels[active];
            stageCount.textContent = `0${active + 1} / 04`;
            progress.style.transform = `scaleX(${(active + 1) / chapters.length})`;
        }
        const amount = Math.min(1, Math.max(0, window.scrollY / window.innerHeight));
        hero.style.transform = `translateY(${amount * -18}px) scale(${1 - amount * .025})`;
    }

    function queueUpdate() {
        if (!frame) frame = requestAnimationFrame(update);
    }

    function configureMotion() {
        observer?.disconnect();
        document.body.classList.toggle('pack-motion', !reducedMotion.matches);
        if (!reducedMotion.matches) {
            observer = new IntersectionObserver(entries => {
                entries.forEach(entry => {
                    if (entry.isIntersecting) {
                        entry.target.classList.add('is-visible');
                        observer.unobserve(entry.target);
                    }
                });
            }, { threshold: .08, rootMargin: '0px 0px -24px 0px' });
            root.querySelectorAll('[data-reveal]').forEach(element => observer.observe(element));
        }
        queueUpdate();
    }

    configureMotion();
    window.addEventListener('scroll', queueUpdate, { passive: true });
    window.addEventListener('resize', queueUpdate, { passive: true });
    reducedMotion.addEventListener('change', configureMotion);
    desktop.addEventListener('change', queueUpdate);
})();
