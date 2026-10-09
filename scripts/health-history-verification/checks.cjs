const {chromium}=require(process.env.PLAYWRIGHT_MODULE || '/Users/macbradley/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const assert=require('node:assert/strict');const path=require('node:path');
const base=process.env.PACKTRACKER_FIXTURE_URL || 'http://127.0.0.1:5091';
const out=path.resolve(__dirname,'../../artifacts/health-history');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'chrome'});
 const context=await browser.newContext({viewport:{width:1440,height:1100},timezoneId:'America/New_York'});
 await context.addCookies([{name:'PetPottyUtcOffsetMinutes',value:'240',url:base}]);
 const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 async function go(url='/Health'){const r=await page.goto(base+url);assert.equal(r.status(),200);}
 async function post(handler,form,expected=200){const token=await page.locator('input[name=__RequestVerificationToken]').first().inputValue();const r=await context.request.post(base+'/Health?handler='+handler,{form:{...form,__RequestVerificationToken:token}});assert.equal(r.status(),expected);return r;}
 await go('/Health?scenario=history');await post('SelectPet',{selectedPetID:'1'});await go();
 assert.equal(await page.locator('.health-range-toggle [value=Recent]').getAttribute('aria-pressed'),'true');
 assert.equal(await page.locator('.health-timeline-row').count(),50);
 assert.equal(await page.locator('#vet-care').count(),0);
 assert(await page.locator('#upcoming-care').evaluate(el=>el.getBoundingClientRect().top < document.querySelector('#health-history').getBoundingClientRect().top));
 const upcoming=await page.locator('#upcoming-care').innerText();
 assert(upcoming.includes('All-day appointment'));assert(upcoming.includes('Time-unspecified appointment'));assert(upcoming.includes('Any-time care'));
 assert(!upcoming.includes('Past timed appointment'));assert(!upcoming.includes('Cancelled appointment'));assert(!upcoming.includes('Completed appointment'));assert(!upcoming.includes('Already administered'));
 assert(upcoming.includes('Showing 1–5 of 13'));
 const dates=await page.locator('#healthFrom').inputValue();
 assert(!dates.startsWith('0001'));
 await page.getByRole('link',{name:'Next records',exact:true}).click();
 assert((await page.locator('#health-history').innerText()).includes('Two-month boundary'));
 assert(!(await page.locator('#health-history').innerText()).includes('Older needle finding'));
 assert.equal(await page.getByRole('link',{name:'Next records',exact:true}).count(),0);
 // Mode switch resets the page and retains the rolling range choice across refresh.
 await page.locator('.health-range-toggle [value=All]').click();await page.waitForURL('**/Health#health-history');
 await page.reload();assert.equal(await page.locator('.health-range-toggle [value=All]').getAttribute('aria-pressed'),'true');
 assert.equal(await page.locator('.health-timeline-row').count(),50);
 assert.equal(await page.locator('#upcoming-care').innerText(),upcoming);
 const firstRows=await page.locator('.health-timeline-title').allTextContents();
 await page.getByRole('link',{name:'Next records',exact:true}).click();
 const secondRows=await page.locator('.health-timeline-title').allTextContents();assert.equal(secondRows.length,50);assert(!secondRows.some(r=>firstRows.includes(r)));
 await page.getByRole('link',{name:'Next records',exact:true}).click();
 assert((await page.locator('#health-history').innerText()).includes('Older needle finding'));
 assert.equal(await page.locator('.health-timeline-row').count(),27);
 assert.equal(await page.getByRole('link',{name:'Next records',exact:true}).count(),0);
 // Search must reach matches beyond page one; type and sort are applied before paging.
 await post('ApplyFilters',{HistoryRange:'All',Search:'Older needle',EventType:'Symptom',Sort:'Newest',HistoryView:'Timeline'});await go();
 assert.equal(await page.locator('.health-timeline-row').count(),1);assert.match(await page.locator('.health-timeline-title').innerText(),/Older needle/);
 assert.equal(await page.locator('#upcoming-care').innerText(),upcoming);
 await post('ApplyFilters',{HistoryRange:'All',Search:'',EventType:'VetVisit',Sort:'Oldest',HistoryView:'Timeline'});await go();
 assert.equal(await page.locator('.health-timeline-row').count(),32);assert.match((await page.locator('.health-timeline-title').allTextContents())[0],/124/);
 await post('ApplyFilters',{HistoryRange:'Custom',From:'2000-01-01',To:'2000-01-02',EventType:'All',Sort:'Newest',HistoryView:'Grouped'});await go();
 assert.equal(await page.locator('.health-timeline-row').count(),0);assert.equal(await page.locator('#healthRangePreset').inputValue(),'Custom');assert.equal(await page.locator('#upcoming-care').innerText(),upcoming);
 await post('ResetFilters',{});await go();assert.equal(await page.locator('.health-range-toggle [value=Recent]').getAttribute('aria-pressed'),'true');
 // Paging stays within the two-month window without affecting the history page.
 await page.locator('#upcoming-care').getByRole('link',{name:'Show more',exact:true}).click();
 assert.match(await page.locator('#upcoming-care').innerText(),/Showing 6–10 of 13/);
 await page.locator('#upcoming-care').getByRole('link',{name:'Show more',exact:true}).click();
 const finalUpcoming=await page.locator('#upcoming-care').innerText();
 assert.match(finalUpcoming,/Two-month upcoming boundary/);
 assert(!finalUpcoming.includes('Distant appointment'));assert(!finalUpcoming.includes('Beyond upcoming boundary'));assert(!finalUpcoming.includes('Distant medication'));
 assert.equal(await page.locator('#upcoming-care').getByRole('link',{name:'Show more',exact:true}).count(),0);
 assert.equal(await page.locator('.health-timeline-row').count(),50);
 // Pet selection remains shared, and explicit actions retain the exact dose or appointment.
 await post('SelectPet',{selectedPetID:'2'});await go();assert.equal(await page.locator('#currentPet').inputValue(),'2');
 assert(!(await page.locator('#upcoming-care').innerText()).includes('Any-time care'));
 await post('SelectPet',{selectedPetID:'1'});await go();
 await page.locator('#upcoming-care form[action*="ReviewDose"]').first().getByRole('button').click();await page.waitForURL('**/Medications#dose-500');
 assert(await page.locator('#dose-500').count());
 await go();await page.locator('#upcoming-care form[action*="OpenVisit"]').first().getByRole('button').click();await page.waitForURL('**/VetVisits');assert.equal(await page.locator('#currentPet').inputValue(),'1');
 await go();await post('SelectPet',{selectedPetID:'999'},404);
 await post('SetHistoryRange',{range:'bogus'},400);
 const denied=await context.request.post(base+'/Health?handler=SetHistoryRange',{form:{range:'All'}});assert.equal(denied.status(),400);
 // New history controls must not hide current care or the recently added weight UI.
 for(const theme of ['light','dark'])for(const width of [1440,390]){
  await page.setViewportSize({width,height:1100});await go('/Health?scenario=history&theme='+theme);
  assert(await page.locator('#pet-care').count());assert(await page.locator('#weight-overview-card').count());
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  await page.screenshot({path:`${out}/health-${width}-${theme}.png`,fullPage:true});
  await page.locator('#health-history').screenshot({path:`${out}/history-${width}-${theme}.png`});
  await page.locator('#upcoming-care').screenshot({path:`${out}/upcoming-${width}-${theme}.png`});
 }
 assert.deepEqual(errors,[]);await browser.close();
 console.log('PASS: Recent two-month boundary, persistent All time, 50-row paging, older search/type/sort/custom filters, independent upcoming pagination, all-day/unspecified/timed/cancelled/completed eligibility, explicit record context, antiforgery, preserved weight UI, mobile/light/dark layouts.');
})().catch(e=>{console.error(e);process.exit(1)});
