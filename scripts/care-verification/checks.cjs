const {chromium}=require(process.env.PLAYWRIGHT_MODULE || '/Users/macbradley/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const assert=require('node:assert/strict');
const path=require('node:path');
const fs=require('node:fs');
const base='http://127.0.0.1:5091';
const out=path.resolve(__dirname,'../../artifacts/care');fs.mkdirSync(out,{recursive:true});
(async()=>{
const browser=await chromium.launch({headless:true,channel:'chrome'});
const context=await browser.newContext({viewport:{width:1440,height:1100},timezoneId:'America/New_York'});
await context.addCookies([{name:'PetPottyUtcOffsetMinutes',value:'240',url:base}]);
const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
async function go(url){const r=await page.goto(base+url);assert.equal(r.status(),200,url);}
async function post(url,fields){const token=await page.locator('input[name=__RequestVerificationToken]').first().inputValue();return context.request.post(base+url,{form:{...fields,__RequestVerificationToken:token}});}
async function expandCare(section){const panel=page.locator('#care-section-'+section);if(await panel.getAttribute('open')===null) await panel.locator('summary').click();}
async function selectPet(id){await go('/Health?scenario=normal');const r=await post('/Health?handler=SelectPet',{selectedPetID:String(id)});assert.equal(r.status(),200);await go('/Health');}
await selectPet(1);
assert.match(await page.locator('#pet-care').innerText(),/Chicken/);
assert.match(await page.locator('#pet-care').innerText(),/None known/);
assert.equal(await page.locator('#care-section-Routine').getAttribute('open'),null);
assert.equal(await page.locator('#care-section-Contacts').getAttribute('open'),null);
assert(await page.locator('#health-history').count());assert(await page.locator('#vet-report').count());
// Each modal saves only its section; data remains plain text and all optional fields can clear.
await expandCare("Important");await page.locator('[data-bs-target="#care-edit-Important"]').click();
await page.locator('#care-Allergies').fill('None known');
await page.locator('#care-CriticalInstructions').fill('Line one\n<script>window.careInjected=true</script>');
let writes=0;page.on('request',r=>{if(r.method()==='POST' && r.url().includes('SaveCare'))writes++;});
const saved=page.waitForResponse(r=>r.url().includes('SaveCare'));
await page.locator('#care-edit-Important form').evaluate(f=>{f.requestSubmit();f.requestSubmit();});
assert.equal((await saved).status(),200);assert.equal(writes,1);await page.locator('#care-edit-Important').waitFor({state:'hidden'});
assert.match(await page.locator('#care-save-status').innerText(),/Abby/);
assert.equal(await page.evaluate(()=>window.careInjected),undefined);
await page.reload();assert.equal(await page.locator('[data-care-value=Allergies]').textContent(),'None known');
assert.equal(await page.locator('[data-care-value=Food]').textContent(),'Salmon kibble');
let r=await post('/Health?handler=SaveCare',{petID:'1',section:'Contacts','CareValues[Microchip]':'000000123','CareValues[Food]':'MUST NOT CHANGE'});assert.equal(r.status(),200);
await page.reload();assert.equal(await page.locator('[data-care-value=Microchip]').textContent(),'000000123');assert.equal(await page.locator('[data-care-value=Food]').textContent(),'Salmon kibble');
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Important','CareValues[Allergies]':'x'.repeat(1001)});assert.equal(r.status(),400);assert((await r.json()).errors.Allergies);
r=await post('/Health?handler=SaveCare',{petID:'999',section:'Important'});assert.equal(r.status(),404);
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Invalid'});assert.equal(r.status(),400);
r=await context.request.post(base+'/Health?handler=SaveCare',{form:{petID:'1',section:'Important'}});assert.equal(r.status(),400);
r=await context.request.get(base+'/Health?handler=CareSheet&petID=999');assert.equal(r.status(),404);
await go('/Health?scenario=caregiver');assert.equal(await page.locator('.care-edit-form').count(),0);
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Important'});assert.equal(r.status(),403);
r=await context.request.get(base+'/Health?handler=CareSheet&petID=1');assert.equal(r.status(),200);
await go('/Health?scenario=missing');assert.match(await page.locator('#pet-care').innerText(),/migration/);assert(await page.locator('#health-history').count());assert.equal(await page.locator('.care-edit-form').count(),0);
await go('/Health?handler=CareSheet&petID=1');assert.match(await page.locator('main').innerText(),/does not include saved/);
await go('/Health?scenario=read-error');assert.match(await page.locator('#pet-care').innerText(),/could not be loaded/);assert(!((await page.locator('#pet-care').innerText()).includes('Not recorded')));
await go('/Health?scenario=save-error');await expandCare("Important");await page.locator('[data-bs-target="#care-edit-Important"]').click();await page.locator('#care-Allergies').fill('Retain after failure');
const failed=page.waitForResponse(r=>r.url().includes('SaveCare'));await page.locator('#care-edit-Important button[type=submit]').click();assert.equal((await failed).status(),503);
assert.equal(await page.locator('#care-Allergies').inputValue(),'Retain after failure');assert(await page.locator('#care-edit-Important').isVisible());assert(await page.locator('#care-edit-Important button[type=submit]').isEnabled());
await selectPet(2);assert.equal(await page.locator('[data-care-value=Allergies]').textContent(),'Not recorded');
r=await post('/Health?handler=SaveCare',{petID:'2',section:'Routine','CareValues[Food]':'Wet food\nHalf a pouch'});assert.equal(r.status(),200);
await page.reload();assert.equal(await page.locator('[data-care-value=Food]').textContent(),'Wet food\nHalf a pouch');
r=await post('/Health?handler=SaveCare',{petID:'2',section:'Routine'});assert.equal(r.status(),200);await page.reload();assert.equal(await page.locator('[data-care-value=Food]').textContent(),'Not recorded');
// Dashboard eligibility, paging and exact-dose actions.
await go('/Home?scenario=normal');assert.equal(await page.locator('#today-care').count(),0);
assert.equal(await page.locator('.pet-card').count(),2);assert.equal(await page.locator('.quick-log-trigger').count(),4);
const reminders=await page.locator('.pet-card-reminders').allTextContents();const text=reminders.join('\n');
assert.match(text,/65 days past due/);assert.match(text,/Recorded skipped/);assert.match(text,/Recorded missed/);
assert.match(text,/1 day past due/);assert.match(text,/Past due/);assert.match(text,/Due today/);assert.match(text,/Due in 3 days/);
assert(!text.includes('Outside window'));assert(!text.includes('Already taken'));assert(!text.includes('Past timed visit'));assert(!text.includes('Dismissed visit'));assert(!text.includes('Future visit'));
assert(text.includes('All-day checkup'));assert(text.includes('Custom reminder visit'));assert(text.includes('1–8 of 12'));
await page.locator('#pet-reminders-1 a:has-text("Next doses")').click();assert.match(await page.locator('#pet-reminders-1').innerText(),/9–12 of 12/);
await go('/Home');const first=page.locator('#pet-reminders-1 .confirm-dose-btn').first();const doseDate=await first.getAttribute('data-log-date');
await first.click();await page.locator('#confirmDoseNotes').fill('Fixture administration');
const givenNavigation=page.waitForNavigation({waitUntil:'networkidle'});const given=page.waitForResponse(r=>r.url().includes('ConfirmSchedule'));await page.locator('#confirmDoseModal button[type=submit]').click();assert.equal((await given).status(),200);await givenNavigation;
await go('/Home');assert.equal(await page.locator(`[data-log-date="${doseDate}"]`).count(),0);assert.match(await page.locator('#pet-reminders-1').innerText(),/1–8 of 11/);
assert.match(await page.locator('#pet-reminders-2').textContent(),/Due in 3 days/);
// Later given dose must leave the earlier skipped/missed records visible.
await page.locator('#pet-reminders-1 details summary').click();const later=page.locator('#pet-reminders-1 .confirm-dose-btn').last();await later.click();const laterNavigation=page.waitForNavigation({waitUntil:'networkidle'});const laterGiven=page.waitForResponse(r=>r.url().includes('ConfirmSchedule'));await page.locator('#confirmDoseModal button[type=submit]').click();assert.equal((await laterGiven).status(),200);await laterNavigation;
await go('/Home');assert.match(await page.locator('#pet-reminders-1').innerText(),/Recorded skipped/);
await page.locator('#pet-reminders-1 form[action*="ReviewDose"] button').first().click();assert.match(page.url(),/Medications#dose-2/);assert(await page.locator('#dose-2').isVisible());assert.match(await page.locator('#dose-2').innerText(),/Skipped/);
await go('/Home?scenario=error');assert.match(await page.locator('.home-dashboard').innerText(),/could not be loaded/);assert.equal(await page.locator('.pet-card-reminders').count(),0);
await go('/Home?scenario=empty');assert.equal(await page.locator('.pet-card-reminders').count(),0);
// Failed dose writes retain the reminder and administration inputs.
await go('/Home?scenario=dose-error');const failedDoseButton=page.locator('.confirm-dose-btn').first();
const failedDate=await failedDoseButton.getAttribute('data-log-date');await failedDoseButton.click();await page.locator('#confirmDoseNotes').fill('Retain dose notes');
const doseFailure=page.waitForResponse(r=>r.url().includes('ConfirmSchedule'));await page.locator('#confirmDoseModal button[type=submit]').click();assert.equal((await doseFailure).status(),500);
assert(await page.locator('#confirmDoseModal').isVisible());assert.equal(await page.locator('#confirmDoseNotes').inputValue(),'Retain dose notes');assert(await page.locator(`[data-log-date="${failedDate}"]`).count());
await go('/Health?scenario=normal');
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Important','CareValues[CriticalInstructions]':'x'.repeat(2000)});assert.equal(r.status(),200);await page.setViewportSize({width:390,height:1100});await page.reload();assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
r=await context.request.get(base+'/Health?handler=CareSheet&petID=1&scenario=anonymous',{maxRedirects:0});assert.equal(r.status(),302);
// Restore readable fixture content for visual review; output contains no real care data.
await selectPet(1);
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Important','CareValues[Allergies]':'Chicken — avoid treats containing poultry.','CareValues[Conditions]':'None known','CareValues[CriticalInstructions]':'Keep the gate closed.\nUse the labeled supplies for Abby.'});assert.equal(r.status(),200);
r=await post('/Health?handler=SaveCare',{petID:'1',section:'Contacts','CareValues[VetName]':'Oak Street Veterinary','CareValues[VetPhone]':'555-0102','CareValues[VetAddress]':'10 Oak Street','CareValues[EmergencyName]':'Jordan','CareValues[EmergencyPhone]':'555-0103','CareValues[Microchip]':'000123456789'});assert.equal(r.status(),200);
for(const theme of ['light','dark']) for(const width of [1440,390]){
 await page.setViewportSize({width,height:1100});await go(`/Health?scenario=normal&theme=${theme}`);
 assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 await page.locator('#care-section-Routine summary').click();await page.locator('#care-section-Contacts summary').click();
 await expandCare("Important");await page.locator('[data-bs-target="#care-edit-Important"]').hover();
 assert(await page.locator('[data-bs-target="#care-edit-Important"]').evaluate(el=>getComputedStyle(el).color!==getComputedStyle(el).backgroundColor));
 await page.mouse.move(0,0);
 await page.screenshot({path:`${out}/health-${width}-${theme}.png`,fullPage:true});
 const routineShown=page.locator('#care-edit-Routine').evaluate(el=>new Promise(resolve=>el.addEventListener('shown.bs.modal',()=>resolve(true),{once:true})));
 await page.locator('[data-bs-target="#care-edit-Routine"]').click();await routineShown;await page.locator('#care-Food').waitFor({state:'visible'});
 assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:`${out}/edit-${width}-${theme}.png`});
 await go(`/Home?scenario=normal&theme=${theme}`);assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
 assert.equal(await page.locator(`.task-log-toggle[aria-expanded="${width>=768}"]`).count(),2);
 await page.screenshot({path:`${out}/dashboard-${width}-${theme}.png`,fullPage:true});
}
await page.setViewportSize({width:1000,height:1100});await go('/Health?handler=CareSheet&petID=1&scenario=normal');
const sheet=await page.locator('body').innerText();assert(!sheet.includes('000123456789'));assert(!sheet.includes('Microchip'));assert(sheet.includes('Give with food.'));assert(sheet.includes('Generated'));assert(sheet.includes('Annual wellness exam'));
assert.equal(await page.locator('.site-navbar').count(),0);await page.screenshot({path:`${out}/care-sheet.png`,fullPage:true});
await page.emulateMedia({media:'print'});assert(!await page.locator('.no-print').isVisible());await page.pdf({path:`${out}/care-sheet.pdf`,format:'A4',printBackground:true});
assert.deepEqual(errors,[]);await browser.close();console.log('PASS: care profile saves/clears, section isolation, safe rendering, roles, cross-pet denial, missing migration/errors, antiforgery, reminder paging and administration persistence, exact-dose review, responsive/theme and print checks.');
})().catch(e=>{console.error(e);process.exit(1)});
