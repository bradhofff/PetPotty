"""HTTP integration checks against the isolated, in-memory fixture host only."""
import os, html, http.cookiejar, re, urllib.parse, urllib.request, urllib.error
BASE = os.environ.get('PACKTRACKER_FIXTURE_URL', 'http://127.0.0.1:5089')
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def get(path):
    return client.open(BASE + path).read().decode()

def post(page, handler, fields, status=200):
    body = get(page)
    token = html.unescape(re.search(r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"', body)[1])
    fields = dict(fields, __RequestVerificationToken=token)
    try:
        response = client.open(BASE + page + ('?handler=' + handler if handler else ''), urllib.parse.urlencode(fields).encode())
        assert response.status == status
        return response.read().decode()
    except urllib.error.HTTPError as error:
        assert error.code == status, (page, handler, error.code)
        return ''

def selected(page, pet):
    body = get(page)
    control = re.search(r'<select id="currentPet".*?</select>', body, re.S)[0]
    assert re.search(r'<option value="' + str(pet) + r'" selected="selected">', control), (page, pet, control)
    return body

pages = ['/Health', '/Medications', '/VetVisits']
for page in pages: selected(page, 0)
body = post('/Medications', 'OpenMedication', {'petID':1, 'medicationID':1})
assert 'const modalToOpen = "editMedModal"' in body
post('/Health', 'SelectPet', {'selectedPetID':0})
# Dashboard action uses this exact Health handler and pet field.
assert 'asp-page' not in get('/Home')
post('/Health', 'SelectPet', {'selectedPetID':1})
for page in pages: selected(page, 1)
for page in pages:
    post(page, '' if page == '/VetVisits' else 'SelectPet', {'selectedPetID':2})
    for other in pages: selected(other, 2)
    post('/Health', 'SelectPet', {'selectedPetID':1})
# Explicit record context wins over Bella.
post('/Health', 'SelectPet', {'selectedPetID':2})
post('/Medications', 'OpenMedication', {'petID':1, 'medicationID':1})
for page in pages: selected(page, 1)
post('/Health', 'SelectPet', {'selectedPetID':2})
post('/VetVisits', 'OpenVisit', {'petID':1, 'vetVisitID':11})
selected('/Health', 1)
# No submitted choice on the existing Vet handler must not clear the preference.
post('/VetVisits', '', {'sort':'Oldest'})
selected('/VetVisits', 1)
for page in pages:
    for invalid in [999, -1, 'not-an-id']:
        post(page, '' if page == '/VetVisits' else 'SelectPet', {'selectedPetID':invalid}, 404)
    selected(page, 1)
post('/Medications', 'OpenMedication', {'petID':999, 'medicationID':1}, 404)
post('/VetVisits', 'OpenVisit', {'petID':2, 'vetVisitID':11}, 404)
get('/fixture/legacy')
post('/Health', 'SelectPet', {'selectedPetID':0})
for page in pages: selected(page, 0)
for boundary in ['stale', 'account', 'household']:
    post('/Health', 'SelectPet', {'selectedPetID':1})
    get('/fixture/' + boundary)
    for page in pages: selected(page, 0)
# Invalid form submissions: no fixture write methods are permitted.
post('/Health', 'SelectPet', {'selectedPetID':2})
body = post('/Health', 'LogEvent', {'NewEvent.PetID':1, 'NewEvent.EventKind':'Symptom'})
assert 'Health records for Abby' in body
selected('/Medications', 1)
post('/Health', 'SelectPet', {'selectedPetID':2})
body = post('/Medications', 'AddMedication', {'NewMedPetID':1, 'NewMedFrequencyType':'Invalid'})
assert 'Medications for Abby' in body
post('/Health', 'SelectPet', {'selectedPetID':2})
body = post('/Medications', 'EditMedication', {'EditMedID':1, 'SelectedPetID':2, 'EditMedFrequencyType':'Invalid'})
assert 'Medications for Abby' in body
post('/Health', 'SelectPet', {'selectedPetID':2})
body = post('/VetVisits', 'AddVisit', {'NewVisit.PetID':1})
assert 'Vet visits for Abby' in body
for page in pages: selected(page, 1)
print('PASS: shared selection, refresh, explicit record precedence, clear/legacy, stale/account/household boundaries, unauthorized IDs, and invalid forms.')
# Success paths return from fake services; no SQL or persistent care writes occur.
for handler, prefix in [('LogEvent','NewEvent'), ('EditEvent','EditEvent')]:
    post('/Health', 'SelectPet', {'selectedPetID':2})
    body = post('/Health', handler, {prefix+'.PetID':1, prefix+'.HealthEventID':1, prefix+'.EventKind':'Symptom', prefix+'.EventType':'Coughing', prefix+'.OccurredAtLocal':'2026-10-03T10:00'})
    assert ('recorded for Abby' if handler == 'LogEvent' else 'updated for Abby') in body
    selected('/Health', 1)
for handler, prefix in [('AddMedication','NewMed'), ('EditMedication','EditMed')]:
    post('/Health', 'SelectPet', {'selectedPetID':2})
    body = post('/Medications', handler, {prefix+'PetID':1, prefix+'ID':1, prefix+'Name':'Fixture medication', prefix+'Dosage':'1 tablet', prefix+'FrequencyType':'Daily', prefix+'TimingDoesNotMatter':'true', prefix+'StartDate':'2026-10-03', prefix+'Forever':'true', 'SelectedPetID':2})
    assert ('added successfully' if handler == 'AddMedication' else 'updated successfully') in body
    for page in pages: selected(page, 1)
print('PASS: successful Health and medication add/edit redirects preserve context with fake service writes.')
for handler, prefix in [('AddVisit','NewVisit'), ('EditVisit','EditVisit')]:
    post('/Health', 'SelectPet', {'selectedPetID':2})
    body = post('/VetVisits', handler, {prefix+'.PetID':1, prefix+'.VetVisitID':11, prefix+'.VisitDate':'2099-01-01', prefix+'.ClinicName':'Fixture clinic', prefix+'.VeterinarianName':'Fixture vet', prefix+'.VisitReason':'Fixture check', prefix+'.ReminderChoice':'None', 'submissionToken':'fixture-only'})
    assert ('Vet visit added' if handler == 'AddVisit' else 'updated') in body
    for page in pages: selected(page, 1)
print('PASS: successful Vet Visits add/edit redirects preserve context with fake service writes.')
