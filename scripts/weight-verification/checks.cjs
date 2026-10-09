// Uses the isolated care fixture host; never writes to a real database.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || '/Users/macbradley/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.PACKTRACKER_FIXTURE_URL || 'http://127.0.0.1:5091';
const out = path.resolve(__dirname, '../../artifacts/weight');
fs.mkdirSync(out, { recursive: true });
(async () => {
    const browser = await chromium.launch({ headless: true, channel: 'chrome' });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, timezoneId: 'America/New_York' });
    await context.addCookies([{ name: 'PetPottyUtcOffsetMinutes', value: '240', url: base }]);
    const page = await context.newPage(), errors = [];
    page.on('pageerror', e => errors.push(e.message));
    async function openWeights() {
        const modal = page.locator('#pet-weight');
        if (!await modal.isVisible()) {
            const shown = modal.evaluate(el => new Promise(resolve => el.addEventListener('shown.bs.modal', resolve, { once: true })));
            await page.locator('#weight-overview-card').click();
            await shown;
        }
    }
    async function go(query = '', open = true) {
        const r = await page.goto(base + '/Health' + query); assert.equal(r.status(), 200);
        if (await page.locator('#weight-overview-card').count()) {
            assert.equal(await page.locator('#pet-weight').isVisible(), false);
            assert.equal(await page.locator('#weight-chart').count(), 0);
            if (open) await openWeights();
        }
    }
    async function post(handler, fields, status = 200) {
        const token = await page.locator('input[name=__RequestVerificationToken]').first().inputValue();
        const r = await context.request.post(base + '/Health?handler=' + handler, { form: { ...fields, __RequestVerificationToken: token } });
        assert.equal(r.status(), status, `${handler}: ${await r.text()}`);
        return r;
    }
    async function select(id) { await post('SelectPet', { selectedPetID: String(id) }); await go(); }
    const day = days => { const d = new Date(); d.setUTCDate(d.getUTCDate() - days); return d.toISOString().slice(0, 10) + 'T09:00:00'; };
    const fields = (overrides = {}) => ({ 'WeightEntry.PetID': '1', 'WeightEntry.WeightEntryID': '0', 'WeightEntry.Weight': '10', 'WeightEntry.Unit': 'kg', 'WeightEntry.MeasuredAtLocal': day(1), 'WeightEntry.UtcOffsetMinutes': '240', ...overrides });
    await go(); assert.equal(await page.locator('#pet-weight').count(), 0);
    await select(1); assert.match(await page.locator('#pet-weight').innerText(), /No weight readings yet/);
    await page.locator('#weight-editor > summary').click();
    await page.locator('#WeightEntry_Weight').fill('22.046');
    await page.locator('#WeightEntry_Unit').selectOption('lb');
    await page.locator('#WeightEntry_MeasuredAtLocal').fill(day(2).slice(0,16));
    await page.locator('#WeightEntry_Notes').fill('At home');
    await Promise.all([page.waitForNavigation(), page.locator('#weight-form button[type=submit]').click()]);
    assert.match(await page.locator('#pet-weight').innerText(), /22.046 lb/);
    await post('SaveWeight', fields({ 'WeightEntry.Notes': 'At the vet\n<script>window.weightInjected=true</script>' }));
    await go();
    assert.equal(await page.locator('.weight-latest').innerText(), '10 kg');
    assert.equal(await page.locator('.weight-change').innerText(), '0 kg');
    assert.equal(await page.evaluate(() => window.weightInjected), undefined);
    // Backdated readings must not replace latest; the summary normalizes mixed units.
    await post('SaveWeight', fields({ 'WeightEntry.Weight': '9.5', 'WeightEntry.MeasuredAtLocal': day(120) }));
    await go(); assert.equal(await page.locator('.weight-latest').innerText(), '10 kg');
    assert.equal(await page.locator('.weight-history tbody tr').count(), 3);
    // Actual edit form opens with original data and updates one entry in place.
    await page.locator('#weight-entry-2 button:has-text("Edit")').click();
    assert.equal(await page.locator('#WeightEntry_Weight').inputValue(), '10');
    assert.equal(await page.locator('#WeightEntry_MeasuredAtLocal').inputValue(), day(1).slice(0,16));
    await page.locator('#WeightEntry_Weight').fill('10.25');
    await Promise.all([page.waitForNavigation(), page.locator('#weight-form button[type=submit]').click()]);
    assert.equal(await page.locator('.weight-latest').innerText(), '10.25 kg');
    assert.equal(await page.locator('.weight-history tbody tr').count(), 3);
    // Binding and server validation must reject malformed and out-of-range inputs.
    for (const override of [
        { 'WeightEntry.Weight': '' }, { 'WeightEntry.Weight': 'bad' }, { 'WeightEntry.Weight': '-1' },
        { 'WeightEntry.Weight': '0' }, { 'WeightEntry.Weight': '1000000' }, { 'WeightEntry.Weight': '1.2345' },
        { 'WeightEntry.Unit': 'oz' }, { 'WeightEntry.MeasuredAtLocal': '' }, { 'WeightEntry.MeasuredAtLocal': 'invalid' },
        { 'WeightEntry.MeasuredAtLocal': '2099-01-01T00:00' }, { 'WeightEntry.UtcOffsetMinutes': '9999' },
        { 'WeightEntry.WeightEntryID': 'bad' }, { 'WeightEntry.Notes': 'x'.repeat(1001) }
    ]) {
        const r = await post('SaveWeight', fields(override));
        assert.match(await r.text(), /validation-summary-errors/);
        await go(); assert.equal(await page.locator('.weight-history tbody tr').count(), 3);
    }
    await post('SaveWeight', fields({ 'WeightEntry.PetID': '999' }), 404);
    await post('SaveWeight', fields({ 'WeightEntry.PetID': '2', 'WeightEntry.WeightEntryID': '2' }), 404);
    await post('OpenWeight', { petID: '2', weightEntryID: '2' }, 404);
    await post('DeleteWeight', { petID: '2', weightEntryID: '2' }, 404);
    await post('DeleteWeight', { petID: '999', weightEntryID: '2' }, 404);
    let response = await context.request.post(base + '/Health?handler=SaveWeight', { form: fields() }); assert.equal(response.status(), 400);
    await select(2); assert.match(await page.locator('#pet-weight').innerText(), /No weight readings yet/);
    await select(1);
    // Database errors preserve values and the surrounding health page.
    await go('?scenario=weight-save-error');
    await page.locator('#weight-editor > summary').click();
    await page.locator('#WeightEntry_Weight').fill('15');
    await page.locator('#WeightEntry_MeasuredAtLocal').fill(day(1).slice(0,16));
    await page.locator('#WeightEntry_Notes').fill('Keep these notes');
    await Promise.all([page.waitForNavigation(), page.locator('#weight-form button[type=submit]').click()]);
    assert.match(await page.locator('#pet-weight').innerText(), /could not be saved/);
    assert.equal(await page.locator('#WeightEntry_Weight').inputValue(), '15');
    assert.equal(await page.locator('#WeightEntry_Notes').inputValue(), 'Keep these notes');
    await go('?scenario=weight-read-error');
    assert.match(await page.locator('#pet-weight').innerText(), /could not be loaded/);
    assert.equal(await page.locator('#health-history').count(), 1);
    assert.equal(await page.locator('#weight-form').count(), 0);
    await go('?scenario=weight-delete-error');
    response = await post('DeleteWeight', { petID: '1', weightEntryID: '2' });
    assert.match(await response.text(), /could not be deleted/);
    await go('?scenario=caregiver');
    response = await post('SaveWeight', fields({ 'WeightEntry.Weight': '9', 'WeightEntry.MeasuredAtLocal': day(200) }));
    assert.match(await response.text(), /Weight recorded/);
    await go('?scenario=normal');
    await page.locator('#weight-entry-4 .weight-delete > summary').click();
    await page.locator('#weight-entry-4 button:has-text("Confirm delete")').click();
    assert.equal(await page.locator('#weight-entry-4').count(), 0);
    // Removing the latest reading restores the previous reading as latest.
    await post('DeleteWeight', { petID: '1', weightEntryID: '2' });
    await go(); assert.equal(await page.locator('.weight-latest').innerText(), '22.046 lb');
    // Keep a useful mixed-unit sample for visual review.
    await post('SaveWeight', fields({ 'WeightEntry.Weight': '10.25' }));
    for (const theme of ['light','dark']) for (const width of [1440,390]) {
        await page.setViewportSize({ width, height: 1100 });
        await go(`?scenario=normal&theme=${theme}`, false);
        assert.equal(await page.locator('#health-overview .health-stat-card').count(), 5);
        if (width >= 1200) {
            const tops = await page.locator('#health-overview .health-stat-card').evaluateAll(cards => cards.map(c => c.getBoundingClientRect().top));
            assert(tops.every(top => Math.abs(top - tops[0]) < 1));
        }
        await page.locator('#health-overview').screenshot({ path: `${out}/overview-${width}-${theme}.png` });
        await openWeights();
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.locator('#pet-weight').screenshot({ path: `${out}/weight-${width}-${theme}.png` });
        await page.locator('#weight-editor > summary').click();
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.locator('#pet-weight').screenshot({ path: `${out}/weight-form-${width}-${theme}.png` });
        await page.locator('#pet-weight .btn-close').click();
        await page.locator('#pet-weight').waitFor({ state: 'hidden' });
        await page.waitForFunction(() => document.activeElement?.id === 'weight-overview-card');
    }
    response = await context.request.get(base + '/Health?scenario=anonymous', { maxRedirects: 0 }); assert.equal(response.status(), 302);
    assert.deepEqual(errors, []);
    await browser.close();
    console.log('PASS: weight CRUD, timezone round-trip, mixed-unit conversion, backdating, compact overview card and modal, binding and validation, pet isolation, antiforgery, caregiver recording, failure retention, and mobile/desktop light/dark layouts.');
})().catch(error => { console.error(error); process.exit(1); });
