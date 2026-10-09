const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert=require('node:assert/strict');
const out=require('node:path').resolve(__dirname, '../../artifacts/dashboard');
require('node:fs').mkdirSync(out,{recursive:true});
(async()=>{
const browser=await chromium.launch({headless:true,channel:'chrome'});
const context=await browser.newContext({viewport:{width:1440,height:1050},timezoneId:'America/New_York'});
await context.addCookies([{name:'PetPottyUtcOffsetMinutes',value:'240',url:'http://127.0.0.1:5088'}]);
const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
const base='http://127.0.0.1:5088/Home';
for(const theme of ['light','dark'])for(const width of [1440,390]) {
 await page.setViewportSize({width,height:1050});await page.goto(`${base}?theme=${theme}`);
 assert(await page.locator('.pet-card').first().evaluate(el=>el.getBoundingClientRect().top) < await page.locator('#today-care').evaluate(el=>el.getBoundingClientRect().top));
 assert(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth));
 assert.equal(await page.locator('.care-upcoming').getAttribute('open'),null);
 assert.equal(await page.locator(`.task-log-toggle[aria-expanded="${width >= 768}"]`).count(),2);
 assert(!(await page.locator('#today-care').innerText()).includes('Resolved skipped'));
 assert((await page.locator('#today-care').innerText()).includes('Any time today'));
 await page.screenshot({path:`${out}/${width===1440?'desktop':'mobile'}-${theme}.png`,fullPage:true});
 if (width >= 768) {
  await page.locator('.task-log-toggle').first().click();
  await page.locator('#taskLog1').waitFor({state:'hidden'});
 }
 await page.locator('.task-log-toggle').first().click();
 await page.locator('#taskLog1').waitFor({state:'visible'});
 assert((await page.locator('#taskLog1').innerText()).includes('A relaxed walk'));
 assert(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth));
 await page.locator('.care-more summary').first().click();
 assert.equal(await page.locator('.care-row:visible').count(),12);

}
for(const scenario of ['no-pets','empty','error','older','caregiver']) {
 await page.goto(`${base}?scenario=${scenario}`);
 const care=await page.locator('#today-care').innerText();
 if(scenario==='empty'||scenario==='no-pets') assert(care.includes('No scheduled care due today.'));
 if(scenario==='error') {assert(care.includes('Care could not be loaded'));assert(!care.includes('No scheduled care due today'));}
 if(scenario==='older') {assert.equal(await page.getByText('No activity in this period.').count(),2);assert.equal(await page.getByText('Explore older history').count(),2);}
 if(scenario==='caregiver') assert.equal(await page.locator('.edit-pet-btn').count(),0);
 if(scenario==='no-pets') assert.equal(await page.locator('.pet-card').count(),0);
}
await page.goto(base);
await page.locator('.confirm-dose-btn').first().click();
await page.locator('#confirmDoseModal').waitFor({state:'visible'});
assert((await page.locator('#confirmDoseMedName').inputValue()).includes('Milo'));
assert((await page.locator('#confirmDoseScheduledDisplay').inputValue()).includes('9:00 AM'));
await page.locator('#confirmDoseNotes').fill('Isolated fixture dose');
let writes=0;page.on('request',r=>{if(r.method()==='POST'&&r.url().includes('ConfirmSchedule'))writes++;});
const responsePromise=page.waitForResponse(r=>r.url().includes('ConfirmSchedule'));
await page.locator('#confirmDoseModal form').evaluate(form=>{form.requestSubmit();form.requestSubmit();});
const response=await responsePromise;assert.equal(response.status(),200);
assert.equal(writes,1);await page.waitForLoadState('networkidle');
await page.goto(base);
await page.locator('.record-dose-btn').first().click();
await page.locator('#recordDoseReason').fill('Fixture pet refused');
const skippedResponse=page.waitForResponse(r=>r.url().includes('RecordDose'));
await page.locator('#recordDoseModal button[type=submit]').click();
assert.equal((await skippedResponse).status(),200);await page.waitForLoadState('networkidle');
await page.goto(base);
await page.locator('.quick-log-trigger').first().click();
await page.locator('#quickLogCustomTime').fill('2026-10-01T20:15');
let quickWrites=0;page.on('request',r=>{if(r.method()==='POST'&&r.url().includes('QuickLog')) {quickWrites++; assert(r.postData().includes('2026-10-01T20:15'));}});
const quickResponse=page.waitForResponse(r=>r.url().includes('QuickLog'));
await page.locator('#quickLogCustomSubmit').evaluate(el=>{el.click();el.click();});
assert.equal((await quickResponse).status(),200);assert.equal(quickWrites,1);await page.waitForLoadState('networkidle');
await page.goto(base);
await page.route('**/Medications?handler=ConfirmSchedule',route=>route.fulfill({status:500,contentType:'application/json',body:JSON.stringify({message:'Fixture save failed'})}));
await page.locator('.confirm-dose-btn').first().click();await page.locator('#confirmDoseNotes').fill('Retain these notes');
await page.locator('#confirmDoseModal button[type=submit]').click();await page.getByText('Fixture save failed').waitFor();
assert.equal(await page.locator('#confirmDoseNotes').inputValue(),'Retain these notes');assert(await page.locator('#confirmDoseModal button[type=submit]').isEnabled());
await page.keyboard.press('Escape');await page.locator('#confirmDoseModal').waitFor({state:'hidden'});
await page.waitForFunction(()=>document.activeElement===document.querySelector('.confirm-dose-btn'));
// Real handler rejects missing CSRF tokens even in the isolated fixture host.
const denied=await context.request.post('http://127.0.0.1:5088/Home?handler=QuickLog',{form:{petID:'1',taskType:'Pee',localTime:'2026-10-01T20:15'}});assert.equal(denied.status(),400);
await page.unroute('**/Medications?handler=ConfirmSchedule');
await page.goto(base);
const token=await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
for(const test of [
 {handler:'RecordDose',fields:{status:'Skipped',reason:''},expected:400},
 {handler:'RecordDose',fields:{status:'Missed',reason:'fixture'},expected:200},
 {handler:'ConfirmSchedule',fields:{confirmedAt:'invalid'},expected:400},
 {handler:'ConfirmSchedule',fields:{medID:'999',confirmedAt:'2026-10-02T09:00'},expected:404}
]) {
 const result=await context.request.post(`http://127.0.0.1:5088/Medications?handler=${test.handler}`,{form:{__RequestVerificationToken:token,dashboardRequest:'true',medID:'1',logDate:'2026-10-02T09:00',utcOffsetMinutes:'240',...test.fields}});
 assert.equal(result.status(),test.expected);
}
assert.deepEqual(errors,[]);console.log('PASS: four layout/theme renders, no horizontal overflow, state fixtures, dose and skip saves, backdated quick log, duplicate guards, save failure retention, modal focus return, antiforgery.');
await browser.close();
})().catch(e=>{console.error(e);process.exit(1);});
