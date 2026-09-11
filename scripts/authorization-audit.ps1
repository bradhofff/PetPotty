param([string]$Phase = 'baseline', [string]$BaseUrl = 'http://127.0.0.1:5187', [switch]$FunctionsOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$connectionString = (Get-Content "$root/appsettings.development.json" | ConvertFrom-Json).ConnectionStrings.DefaultConnection
$results = [Collections.Generic.List[object]]::new()
function Sql([string]$query) {
    $conn = [System.Data.SqlClient.SqlConnection]::new($connectionString)
    try { $conn.Open(); $cmd = $conn.CreateCommand(); $cmd.CommandText = if ($query.EndsWith('FOR JSON PATH')) { "SELECT ($query)" } else { $query }; return $cmd.ExecuteScalar() }
    finally { $conn.Dispose() }
}
function Client {
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    return [Net.Http.HttpClient]::new($handler)
}
function Request($client, [string]$method, [string]$path, [hashtable]$body = @{}, [string]$upload = '') {
    $msg = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), "$BaseUrl$path")
    if ($method -eq 'POST') {
        $page = $path.Split('?')[0]
        $html = $client.GetStringAsync("$BaseUrl$page").GetAwaiter().GetResult()
        $token = [regex]::Match($html, 'name="__RequestVerificationToken" type="hidden" value="([^"]+)"').Groups[1].Value
        if (!$token) { throw "No antiforgery token on $page" }
        $fields = [Collections.Generic.Dictionary[string,string]]::new()
        foreach ($key in $body.Keys) { $fields[$key] = [string]$body[$key] }
        $fields['__RequestVerificationToken'] = $token
        if ($upload) {
            $form = [Net.Http.MultipartFormDataContent]::new()
            foreach ($key in $fields.Keys) { $form.Add([Net.Http.StringContent]::new($fields[$key]), $key) }
            if ($upload.EndsWith('Image')) {
                $file = [Net.Http.ByteArrayContent]::new([Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6n0kAAAAASUVORK5CYII='))
                $file.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('image/png')
                $form.Add($file, $upload, 'audit.png')
            } else {
                $file = [Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes("%PDF-1.4`nAUDIT_PRIVATE_DOCUMENT`n%%EOF"))
                $file.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/pdf')
                $form.Add($file, $upload, 'audit.pdf')
            }
            $msg.Content = $form
        } else { $msg.Content = [Net.Http.FormUrlEncodedContent]::new($fields) }
    }
    $response = $client.SendAsync($msg).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $result = @{Status=[int]$response.StatusCode; Text=$text; Location=[string]$response.Headers.Location}
    $msg.Dispose(); $response.Dispose(); return $result
}
function Ok($response) { if ($response.Status -ne 302) { throw "Setup/positive control failed: $($response.Status) $(([regex]::Replace($response.Text, '<[^>]+>', ' ') -replace '\s+',' '))" } }
function PetFields([string]$prefix, [string]$name) {
    return @{"${prefix}Name"=$name;"${prefix}Type"='Dog';"${prefix}Breed"='Audit';"${prefix}Age"='2';"${prefix}Birthdate"='2024-01-01';"${prefix}Gender"='Male'}
}
function MedFields([string]$prefix, [string]$name) {
    return @{"${prefix}Name"=$name;"${prefix}Dosage"='1';"${prefix}FrequencyType"='Daily';"${prefix}FrequencyInterval"='1';"${prefix}TimingDoesNotMatter"='true';"${prefix}StartDate"=[DateTime]::Today.ToString('yyyy-MM-dd');"${prefix}Forever"='false';"${prefix}EndDate"=[DateTime]::Today.AddDays(3).ToString('yyyy-MM-dd');"${prefix}Notes"=$name}
}
function VisitFields([string]$prefix, [int]$pet, [string]$name) {
    return @{"$prefix.PetID"=$pet;"$prefix.VisitDate"=[DateTime]::Today.AddDays(2).ToString('yyyy-MM-dd');"$prefix.IsAllDay"='true';"$prefix.ClinicName"=$name;"$prefix.VeterinarianName"='Audit Vet';"$prefix.VisitReason"=$name;"$prefix.VisitType"='Wellness exam';"$prefix.Status"='Scheduled';"$prefix.ReminderChoice"='OneDay';submissionToken=[guid]::NewGuid().ToString('N')}
}
function Seed([string]$label) {
    $client = Client; $name = 'authaudit_' + [guid]::NewGuid().ToString('N').Substring(0,12) + "_$label"
    $password = [guid]::NewGuid().ToString('N')
    Ok (Request $client POST /Signup @{SignupName=$name;SignupUserName=$name;SignupEmail="$name@example.invalid";SignupPass=$password;SignupConfirmPass=$password})
    Ok (Request $client POST /Login @{Username=$name;Password=$password})
    $user = [int](Sql "SELECT userID FROM Users WHERE userName='$name'")
    Ok (Request $client POST '/Home?handler=AddPet' (PetFields NewPet $name) NewPetImage)
    $pet = [int](Sql "SELECT petID FROM Pets WHERE userID=$user")
    Ok (Request $client POST '/Home?handler=AddTask' @{NewTaskPetID=$pet;NewTaskType='Walk';NewTaskNotes=$name;NewTaskCreatedAt=[DateTime]::Now.ToString('s')})
    $task = [int](Sql "SELECT MAX(taskID) FROM Tasks WHERE petID=$pet")
    $medForm = MedFields NewMed $name; $medForm.NewMedPetID=$pet
    Ok (Request $client POST '/Medications?handler=AddMedication' $medForm)
    $med = [int](Sql "SELECT MAX(medID) FROM Medications WHERE petID=$pet")
    Ok (Request $client POST '/VetVisits?handler=AddVisit' (VisitFields NewVisit $pet $name))
    $visit = [int](Sql "SELECT MAX(VetVisitID) FROM VetVisits WHERE PetID=$pet")
    if (!$visit) { throw 'Visit not created' }
    Ok (Request $client POST '/VetVisits?handler=UploadDocument' @{DocumentVisitID=$visit;DocumentType='Other';DocumentDisplayName=$name} DocumentUpload)
    $doc = [int](Sql "SELECT MAX(VetVisitDocumentID) FROM VetVisitDocuments WHERE VetVisitID=$visit")
    $reminder = [int](Sql "SELECT MAX(VetVisitReminderID) FROM VetVisitReminders WHERE VetVisitID=$visit")
    $null = Request $client GET /Home
    $null = Request $client GET "/Medications?petID=$pet"
    $photo = [string](Sql "SELECT ProfileImagePath FROM Pets WHERE petID=$pet")
    return @{Client=$client;Name=$name;User=$user;Pet=$pet;Task=$task;Med=$med;Visit=$visit;Doc=$doc;Reminder=$reminder;Photo=$photo}
}
function Snapshot($user) {
    $parts = foreach ($q in @(
        "SELECT * FROM Pets WHERE userID=$user ORDER BY petID FOR JSON PATH",
        "SELECT t.* FROM Tasks t JOIN Pets p ON p.petID=t.petID WHERE p.userID=$user ORDER BY t.taskID FOR JSON PATH",
        "SELECT m.* FROM Medications m JOIN Pets p ON p.petID=m.petID WHERE p.userID=$user ORDER BY m.medID FOR JSON PATH",
        "SELECT s.* FROM MedicationSchedule s JOIN Medications m ON m.medID=s.medID JOIN Pets p ON p.petID=m.petID WHERE p.userID=$user ORDER BY s.medID,s.scheduleDate FOR JSON PATH",
        "SELECT v.* FROM VetVisits v JOIN Pets p ON p.petID=v.PetID WHERE p.userID=$user ORDER BY v.VetVisitID FOR JSON PATH",
        "SELECT d.* FROM VetVisitDocuments d JOIN VetVisits v ON v.VetVisitID=d.VetVisitID JOIN Pets p ON p.petID=v.PetID WHERE p.userID=$user ORDER BY d.VetVisitDocumentID FOR JSON PATH",
        "SELECT r.* FROM VetVisitReminders r JOIN VetVisits v ON v.VetVisitID=r.VetVisitID JOIN Pets p ON p.petID=v.PetID WHERE p.userID=$user ORDER BY r.VetVisitReminderID FOR JSON PATH",
        "SELECT h.* FROM VetVisitHistory h JOIN VetVisits v ON v.VetVisitID=h.VetVisitID JOIN Pets p ON p.petID=v.PetID WHERE p.userID=$user ORDER BY h.VetVisitHistoryID FOR JSON PATH"
    )) { [string](Sql $q) }
    $files = [Collections.Generic.List[string]]::new()
    $photos = Sql "SELECT ProfileImagePath FROM Pets WHERE userID=$user AND ProfileImagePath IS NOT NULL FOR JSON PATH"
    foreach ($photo in ($photos | ConvertFrom-Json)) {
        $path = Join-Path $root $photo.ProfileImagePath.TrimStart('/')
        $files.Add($(if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'missing' }))
    }
    $documents = Sql "SELECT d.StoredPath FROM VetVisitDocuments d JOIN VetVisits v ON v.VetVisitID=d.VetVisitID JOIN Pets p ON p.petID=v.PetID WHERE p.userID=$user ORDER BY d.VetVisitDocumentID FOR JSON PATH"
    foreach ($document in ($documents | ConvertFrom-Json)) {
        $path = Join-Path "$root/vet-documents" $document.StoredPath
        $files.Add($(if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'missing' }))
    }
    return ($parts + $files.ToArray()) -join "`n"
}
function Attack([string]$path, [hashtable]$body=@{}, [string]$method='POST', [string]$variant='form', [string]$upload='') {
    $before = Snapshot $b.User
    $response = Request $a.Client $method $path $body $upload
    $after = Snapshot $b.User
    $leak = $response.Text.Contains($b.Name) -or $response.Text.Contains('AUDIT_PRIVATE_DOCUMENT') -or ($variant -eq 'photo path' -and $response.Status -eq 200)
    $row = [ordered]@{Method=$method;Endpoint=$path;Variant=$variant;Fields=$body;Status=$response.Status;Location=$response.Location;Unchanged=($before -ceq $after);Leak=$leak;Pass=($response.Status -in @(400,401,403,404,405) -and $before -ceq $after -and !$leak)}
    $results.Add($row); Write-Host "$method $path => $($row.Status) unchanged=$($row.Unchanged) leak=$leak"
    $results | ConvertTo-Json -Depth 8 | Set-Content "$PSScriptRoot/audit-$Phase.json"
    if ($Phase -eq 'final' -and $variant -eq 'form') {
        $query = ($body.GetEnumerator() | ForEach-Object { [uri]::EscapeDataString($_.Key) + '=' + [uri]::EscapeDataString([string]$_.Value) }) -join '&'
        $separator = if ($path.Contains('?')) { '&' } else { '?' }
        Attack "$path$separator$query" @{} POST query
    }
}
if ($FunctionsOnly) { return }
$a=Seed A; $b=Seed B
$metaA=$a.Clone(); $metaA.Remove('Client'); $metaB=$b.Clone(); $metaB.Remove('Client')
@{Phase=$Phase;A=$metaA;B=$metaB} | ConvertTo-Json -Depth 3 | Set-Content "$PSScriptRoot/audit-$Phase-fixtures.json"
Write-Host "Fixtures created: A=$($a.User), B=$($b.User)"
if ($Phase -eq 'final') {
    Ok (Request $b.Client POST '/Medications?handler=ConfirmSchedule' @{medID=$b.Med;logDate=[DateTime]::Today.ToString('s');confirmedAt=[DateTime]::Now.AddMinutes(-1).ToString('s')})
}
Attack $b.Photo @{} GET 'photo path'
Attack "/Medications?petID=$($b.Pet)" @{} GET query
Attack '/Medications?handler=SelectPet' @{selectedPetID=$b.Pet}
Attack "/VetVisits?petID=$($b.Pet)&vetVisitID=$($b.Visit)" @{} GET query
foreach($page in @('Home','Medications','VetVisits')) { Attack "/$page/$($b.Pet)" @{} GET route }
$f=PetFields EditPet ATTACK; $f.EditPetID=$b.Pet; Attack '/Home?handler=EditPet' $f
Attack '/Home?handler=ResetPetImage' @{EditPetID=$b.Pet}
Attack '/Home?handler=DeletePet' @{EditPetID=$b.Pet}
Attack '/Home?handler=QuickLog' @{petID=$b.Pet;taskType='Pee'}
Attack "/Home?handler=QuickLog&petID=$($b.Pet)&taskType=Poop" @{} POST query
Attack '/Home?handler=AddTask' @{NewTaskPetID=$b.Pet;NewTaskType='Walk';NewTaskNotes='ATTACK';NewTaskCreatedAt=[DateTime]::Now.ToString('s')}
Attack '/Home?handler=UpdateTask' @{UpdateTaskID=$b.Task;UpdateTaskType='Play';UpdateTaskNotes='ATTACK';UpdateTaskCreatedAt=[DateTime]::Now.ToString('s')}
Attack "/Home?handler=DeleteTask&taskID=$($b.Task)" @{} POST query
$f=MedFields NewMed ATTACK; $f.NewMedPetID=$b.Pet; Attack '/Medications?handler=AddMedication' $f
$f=MedFields EditMed ATTACK; $f.EditMedID=$b.Med; $f.SelectedPetID=$a.Pet; Attack '/Medications?handler=EditMedication' $f
$today=[DateTime]::Today.ToString('s')
Attack '/Medications?handler=ConfirmSchedule' @{medID=$b.Med;logDate=$today;confirmedAt=[DateTime]::Now.ToString('s')}
Attack "/Medications?handler=UnconfirmSchedule&medID=$($b.Med)&logDate=$today" @{} POST query
Attack '/Medications?handler=DeleteMedication' @{medID=$b.Med}
Attack '/VetVisits' @{selectedPetID=$b.Pet;add='true'}
Attack '/VetVisits?handler=AddVisit' (VisitFields NewVisit $b.Pet ATTACK)
$f=VisitFields EditVisit $b.Pet ATTACK; $f.'EditVisit.VetVisitID'=$b.Visit; Attack '/VetVisits?handler=EditVisit' $f
$f=VisitFields EditVisit $b.Pet ATTACK; $f.'EditVisit.VetVisitID'=$a.Visit; Attack '/VetVisits?handler=EditVisit' $f POST reparent
Attack "/VetVisits?handler=ChangeStatus&vetVisitID=$($b.Visit)" @{status='Cancelled';details='ATTACK'} POST query
Attack '/VetVisits?handler=CompleteVisit' @{'Completion.VetVisitID'=$b.Visit;'Completion.VisitSummary'='ATTACK'}
Attack '/VetVisits?handler=DismissReminder' @{reminderID=$b.Reminder;petID=$a.Pet}
Attack '/VetVisits?handler=UploadDocument' @{DocumentVisitID=$b.Visit;DocumentType='Other';DocumentDisplayName='ATTACK'} POST multipart DocumentUpload
Attack "/VetVisits?handler=PreviewDocument&documentID=$($b.Doc)" @{} GET query
Attack '/VetVisits?handler=DownloadDocument' @{documentID=$b.Doc}
Attack '/VetVisits?handler=UpdateDocument' @{documentID=$b.Doc;documentType='Other';displayName='ATTACK';description='ATTACK'}
Attack "/VetVisits?handler=DeleteDocument&documentID=$($b.Doc)" @{} POST query
Attack '/VetVisits?handler=DeleteVisit' @{vetVisitID=$b.Visit}
if ($Phase -eq 'final') {
    Attack '/Home?handler=DeleteTask' @{taskID=$b.Task} POST 'body task ID'
    Attack '/Medications?handler=UnconfirmSchedule' @{medID=$b.Med;logDate=$today} POST 'body medication ID'
    Attack '/Medications?handler=EditMedication' @{EditMedID=$b.Med;EditMedFrequencyType='invalid'} POST 'invalid form foreign ID'
    Attack '/VetVisits?handler=ChangeStatus' @{vetVisitID=$b.Visit;status='Cancelled'} POST 'body visit ID'
    Attack '/VetVisits?handler=DeleteDocument' @{documentID=$b.Doc} POST 'body document ID'
    $f=PetFields EditPet ATTACK; $f.EditPetID=$b.Pet
    Attack '/Home?handler=EditPet' $f POST multipart EditPetImage
    $f=VisitFields EditVisit $a.Pet ATTACK; $f.'EditVisit.VetVisitID'=$b.Visit
    Attack '/VetVisits?handler=EditVisit' $f POST 'foreign visit own pet'
    Attack '/VetVisits?handler=DismissReminder' @{reminderID=$a.Reminder;petID=$b.Pet} POST 'own reminder foreign pet'
}
Write-Host "Results: $(@($results | Where-Object Pass).Count)/$($results.Count) pass"
if ($Phase -eq 'final') {
    $controls = [Collections.Generic.List[object]]::new()
    function Control([string]$path, [hashtable]$body=@{}, [string]$method='POST', [int]$expected=302, [scriptblock]$verify={ $true }) {
        $before = Snapshot $b.User
        $r = Request $a.Client $method $path $body
        $valid = & $verify
        $pass = $r.Status -eq $expected -and $valid -and (Snapshot $b.User) -ceq $before
        $controls.Add(@{Method=$method;Endpoint=$path;Status=$r.Status;Pass=$pass})
        $controls | ConvertTo-Json -Depth 4 | Set-Content "$PSScriptRoot/audit-final-controls.json"
        if (!$pass) { throw "Positive control failed: $method $path status=$($r.Status) database=$valid" }
    }
    Control /Home @{} GET 200
    Control $a.Photo @{} GET 200
    Control "/Medications?petID=$($a.Pet)" @{} GET 200
    Control '/Medications?handler=SelectPet' @{selectedPetID=$a.Pet}
    Control '/Medications?handler=SetScheduleView' @{showAllTime='true'}
    Control '/Home?handler=SetTaskView' @{showAllTime='true'}
    Control '/Home?handler=ShowMoreTasks'
    $f=PetFields EditPet OWN_EDIT; $f.EditPetID=$a.Pet
    Control '/Home?handler=EditPet' $f POST 302 { (Sql "SELECT name FROM Pets WHERE petID=$($a.Pet)") -eq 'OWN_EDIT' }
    Control '/Home?handler=QuickLog' @{petID=$a.Pet;taskType='Pee'} POST 302 { (Sql "SELECT COUNT(*) FROM Tasks WHERE petID=$($a.Pet) AND taskType='Pee'") -gt 0 }
    Control '/Home?handler=UpdateTask' @{UpdateTaskID=$a.Task;UpdateTaskType='Play';UpdateTaskNotes='OWN_EDIT';UpdateTaskCreatedAt=[DateTime]::Now.ToString('s')} POST 302 { (Sql "SELECT notes FROM Tasks WHERE taskID=$($a.Task)") -eq 'OWN_EDIT' }
    Control '/Home?handler=DeleteTask' @{taskID=$a.Task} POST 302 { (Sql "SELECT COUNT(*) FROM Tasks WHERE taskID=$($a.Task)") -eq 0 }
    $f=MedFields EditMed OWN_EDIT; $f.EditMedID=$a.Med
    Control '/Medications?handler=EditMedication' $f POST 302 { (Sql "SELECT medicationName FROM Medications WHERE medID=$($a.Med)") -eq 'OWN_EDIT' }
    Control '/Medications?handler=ConfirmSchedule' @{medID=$a.Med;logDate=$today;confirmedAt=[DateTime]::Now.ToString('s')} POST 302 { (Sql "SELECT COUNT(*) FROM MedicationSchedule WHERE medID=$($a.Med) AND isConfirmed=1") -gt 0 }
    Control '/Medications?handler=UnconfirmSchedule' @{medID=$a.Med;logDate=$today} POST 302 { (Sql "SELECT COUNT(*) FROM MedicationSchedule WHERE medID=$($a.Med) AND isConfirmed=1") -eq 0 }
    Control '/Medications?handler=DeleteMedication' @{medID=$a.Med} POST 302 { (Sql "SELECT COUNT(*) FROM Medications WHERE medID=$($a.Med)") -eq 0 }
    Control '/VetVisits' @{selectedPetID=$a.Pet}
    $f=VisitFields EditVisit $a.Pet OWN_EDIT; $f.'EditVisit.VetVisitID'=$a.Visit
    Control '/VetVisits?handler=EditVisit' $f POST 302 { (Sql "SELECT ClinicName FROM VetVisits WHERE VetVisitID=$($a.Visit)") -eq 'OWN_EDIT' }
    Control "/VetVisits?handler=PreviewDocument&documentID=$($a.Doc)" @{} GET 200
    Control '/VetVisits?handler=DownloadDocument' @{documentID=$a.Doc} POST 200
    Control '/VetVisits?handler=UpdateDocument' @{documentID=$a.Doc;documentType='Other';displayName='OWN_EDIT'} POST 302 { (Sql "SELECT DisplayName FROM VetVisitDocuments WHERE VetVisitDocumentID=$($a.Doc)") -eq 'OWN_EDIT' }
    Control '/VetVisits?handler=DeleteDocument' @{documentID=$a.Doc} POST 302 { (Sql "SELECT COUNT(*) FROM VetVisitDocuments WHERE VetVisitDocumentID=$($a.Doc)") -eq 0 }
    $a.Reminder=[int](Sql "SELECT MAX(VetVisitReminderID) FROM VetVisitReminders WHERE VetVisitID=$($a.Visit)")
    Control '/VetVisits?handler=DismissReminder' @{reminderID=$a.Reminder;petID=$a.Pet} POST 302 { (Sql "SELECT Status FROM VetVisitReminders WHERE VetVisitReminderID=$($a.Reminder)") -eq 'Dismissed' }
    Control '/VetVisits?handler=ChangeStatus' @{vetVisitID=$a.Visit;status='Confirmed'} POST 302 { (Sql "SELECT Status FROM VetVisits WHERE VetVisitID=$($a.Visit)") -eq 'Confirmed' }
    Control '/VetVisits?handler=CompleteVisit' @{'Completion.VetVisitID'=$a.Visit;'Completion.VisitSummary'='OWN_COMPLETED'} POST 302 { (Sql "SELECT Status FROM VetVisits WHERE VetVisitID=$($a.Visit)") -eq 'Completed' }
    Control '/VetVisits?handler=DeleteVisit' @{vetVisitID=$a.Visit} POST 302 { (Sql "SELECT COUNT(*) FROM VetVisits WHERE VetVisitID=$($a.Visit) AND IsDeleted=0") -eq 0 }
    Control '/Home?handler=ResetPetImage' @{EditPetID=$a.Pet} POST 302 { (Sql "SELECT COUNT(*) FROM Pets WHERE petID=$($a.Pet) AND ProfileImagePath IS NOT NULL") -eq 0 }
    Control '/Home?handler=DeletePet' @{EditPetID=$a.Pet} POST 302 { (Sql "SELECT COUNT(*) FROM Pets WHERE petID=$($a.Pet)") -eq 0 }
    Write-Host "Positive controls: $($controls.Count) passed (including database effects)."
    if (@($results | Where-Object { !$_.Pass }).Count) { throw 'Authorization regression detected; see audit-final.json' }
}


