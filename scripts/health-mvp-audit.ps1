param([string]$BaseUrl = 'http://127.0.0.1:5191')

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/authorization-audit.ps1" -FunctionsOnly -BaseUrl $BaseUrl

$checks = [Collections.Generic.List[object]]::new()
function Check([string]$name, [bool]$passed, [string]$detail = '') {
    $checks.Add([ordered]@{ Name=$name; Passed=$passed; Detail=$detail })
    if (!$passed) { throw "FAILED: $name $detail" }
    Write-Host "PASS: $name"
}
function HealthFields([int]$petID, [string]$kind, [string]$type, [DateTime]$when, [int]$severity = 3, [int]$offset = 0) {
    return @{
        'NewEvent.PetID'=$petID
        'NewEvent.EventKind'=$kind
        'NewEvent.EventType'=$type
        'NewEvent.OccurredAtLocal'=$when.ToString('s')
        'NewEvent.Severity'=$severity
        'NewEvent.Description'="$type details"
        'NewEvent.UtcOffsetMinutes'=$offset
    }
}
function AddHealth($fixture, [string]$kind, [string]$type, [DateTime]$when) {
    $response = Request $fixture.Client POST '/Health?handler=LogEvent' (HealthFields $fixture.Pet $kind $type $when)
    $validation = ([regex]::Matches($response.Text, '<span[^>]*field-validation-error[^>]*>(.*?)</span>') | ForEach-Object { [regex]::Replace($_.Groups[1].Value, '<[^>]+>', '') }) -join '; '
    if (!$validation) { $validation = [regex]::Match($response.Text, '<div class="alert alert-danger[^>]*>(.*?)</div>').Groups[1].Value }
    Check "create $type" ($response.Status -eq 302) "status=$($response.Status) validation=$validation"
    return [int](Sql "SELECT MAX(HealthEventID) FROM HealthEvents WHERE PetID=$($fixture.Pet) AND EventType='$type' AND IsDeleted=0")
}
function CleanupFixture($fixture) {
    if (!$fixture) { return }
    $petsJson = Sql "SELECT petID FROM Pets WHERE userID=$($fixture.User) FOR JSON PATH"
    if ($petsJson) {
        foreach ($pet in @($petsJson | ConvertFrom-Json)) {
            $response = Request $fixture.Client POST '/Home?handler=DeletePet' @{EditPetID=[int]$pet.petID}
            if ($response.Status -ne 302) { throw "Pet cleanup failed: $($response.Status)" }
        }
    }
    $remaining = Sql "DELETE FROM Users WHERE userID=$($fixture.User) AND userName='$($fixture.Name)' AND NOT EXISTS (SELECT 1 FROM Pets WHERE userID=$($fixture.User)); SELECT COUNT(*) FROM Users WHERE userID=$($fixture.User);"
    if ([int]$remaining -ne 0) { throw 'Account cleanup failed' }
    $fixture.Client.Dispose()
}

$a = $null
$b = $null
try {
    $a = Seed HEALTH_A
    $b = Seed HEALTH_B
    # The HTTP harness does not execute site.js, so reports use the configured
    # zero-offset fallback. Match the server's UTC report day explicitly.
    $today = [DateTime]::UtcNow.Date

    $aEvent = AddHealth $a Symptom 'HEALTH_A_TODAY' $today.AddHours(10)
    $bEvent = AddHealth $b Incident 'HEALTH_B_PRIVATE' $today.AddHours(11)
    $boundary30 = AddHealth $a Incident 'HEALTH_A_DAY_29' $today.AddDays(-29).AddHours(8)
    $outside30 = AddHealth $a Symptom 'HEALTH_A_DAY_30' $today.AddDays(-30).AddHours(8)
    $boundary90 = AddHealth $a Symptom 'HEALTH_A_DAY_89' $today.AddDays(-89).AddHours(8)
    $outside90 = AddHealth $a Incident 'HEALTH_A_DAY_90' $today.AddDays(-90).AddHours(8)

    $storedUtc = [string](Sql "SELECT CONVERT(varchar(19), OccurredAtUtc, 126) FROM HealthEvents WHERE HealthEventID=$aEvent")
    Check 'health event stored as UTC' ($storedUtc -eq $today.AddHours(10).ToString('s')) $storedUtc
    $offsetFields = HealthFields $a.Pet Symptom 'HEALTH_OFFSET' $today.AddHours(7) 2 240
    Ok (Request $a.Client POST '/Health?handler=LogEvent' $offsetFields)
    $offsetUtc = [string](Sql "SELECT CONVERT(varchar(19), OccurredAtUtc, 126) FROM HealthEvents WHERE PetID=$($a.Pet) AND EventType='HEALTH_OFFSET'")
    Check 'nonzero browser offset converted to UTC' ($offsetUtc -eq $today.AddHours(11).ToString('s')) $offsetUtc

    Ok (Request $a.Client POST '/Home?handler=AddPet' (PetFields NewPet 'HEALTH_EMPTY_PET'))
    $emptyPet = [int](Sql "SELECT MAX(petID) FROM Pets WHERE userID=$($a.User)")
    $emptyTimeline = Request $a.Client GET "/Health?petID=$emptyPet"
    Check 'multiple-pet empty timeline' ($emptyTimeline.Status -eq 200 -and $emptyTimeline.Text.Contains('No medical events in this range'))

    $dashboardPage = Request $a.Client GET '/Home'
    Check 'primary navigation omits Reports' ($dashboardPage.Status -eq 200 -and !$dashboardPage.Text.Contains('📄 Reports'))
    Check 'dashboard offers in-place health logging' ($dashboardPage.Text.Contains('Log Health Event') -and $dashboardPage.Text.Contains('dashboardHealthEventModal'))
    $dashboardHealthFields = HealthFields $a.Pet Symptom 'HEALTH_FROM_DASHBOARD' $today.AddHours(12)
    $dashboardHealthFields.ReturnToDashboard = 'true'
    $dashboardHealth = Request $a.Client POST '/Health?handler=LogEvent' $dashboardHealthFields
    Check 'dashboard and Health share creation handler' ($dashboardHealth.Status -eq 302 -and $dashboardHealth.Location.EndsWith('/Home') -and [int](Sql "SELECT COUNT(*) FROM HealthEvents WHERE PetID=$($a.Pet) AND EventType='HEALTH_FROM_DASHBOARD' AND IsDeleted=0") -eq 1) "status=$($dashboardHealth.Status) location=$($dashboardHealth.Location)"
    Ok (Request $a.Client POST '/Home?handler=AddTask' @{NewTaskPetID=$a.Pet;NewTaskType='HEALTH_ROUTINE_ONLY';NewTaskNotes='must stay on dashboard';NewTaskCreatedAt=$today.AddHours(9).ToString('s')})

    $healthPage = Request $a.Client GET "/Health?petID=$($a.Pet)"
    Check 'timeline contains owned symptom' ($healthPage.Status -eq 200 -and $healthPage.Text.Contains('HEALTH_A_TODAY'))
    Check 'health overview includes medical summaries' ($healthPage.Text.Contains('Health overview') -and $healthPage.Text.Contains('Current medications') -and $healthPage.Text.Contains('30-day adherence') -and $healthPage.Text.Contains('vet visit'))
    Check 'routine Tasks stay out of Health' (!$healthPage.Text.Contains('HEALTH_ROUTINE_ONLY') -and !$healthPage.Text.Contains('must stay on dashboard'))
    Check 'new care record attributed to recorder' ([int](Sql "SELECT RecordedByUserID FROM Tasks WHERE taskID=$($a.Task)") -eq $a.User)
    Check 'timeline excludes another pet' (!$healthPage.Text.Contains('HEALTH_B_PRIVATE'))
    Check 'Health owns vet-report generation UX' ($healthPage.Text.Contains('id="vet-report"') -and $healthPage.Text.Contains('Generate vet report') -and $healthPage.Text.Contains('Last 30 days') -and $healthPage.Text.Contains('Last 90 days') -and $healthPage.Text.Contains('Custom range'))

    $incidentOnly = Request $a.Client GET "/Health?petID=$($a.Pet)&EventType=Incident&From=$($today.AddDays(-40).ToString('yyyy-MM-dd'))&To=$($today.ToString('yyyy-MM-dd'))"
    Check 'event-type filter' ($incidentOnly.Status -eq 200 -and $incidentOnly.Text.Contains("health-event-$boundary30") -and !$incidentOnly.Text.Contains("health-event-$aEvent"))

    $foreignTimeline = Request $a.Client GET "/Health?petID=$($b.Pet)"
    Check 'foreign timeline denied' ($foreignTimeline.Status -eq 404) "status=$($foreignTimeline.Status)"
    $foreignCreate = Request $a.Client POST '/Health?handler=LogEvent' (HealthFields $b.Pet Symptom 'ATTACK_CREATE' $today)
    Check 'foreign symptom create denied' ($foreignCreate.Status -eq 404 -and [int](Sql "SELECT COUNT(*) FROM HealthEvents WHERE PetID=$($b.Pet) AND EventType='ATTACK_CREATE'") -eq 0)
    $foreignDelete = Request $a.Client POST '/Health?handler=DeleteEvent' @{healthEventID=$bEvent;petID=$b.Pet}
    Check 'foreign health event delete denied' ($foreignDelete.Status -eq 404 -and [int](Sql "SELECT COUNT(*) FROM HealthEvents WHERE HealthEventID=$bEvent AND IsDeleted=0") -eq 1)

    $invalid = HealthFields $a.Pet Symptom 'INVALID_SEVERITY' $today
    $invalid['NewEvent.Severity'] = 7
    $invalidResponse = Request $a.Client POST '/Health?handler=LogEvent' $invalid
    Check 'invalid severity rejected' ($invalidResponse.Status -eq 200 -and [int](Sql "SELECT COUNT(*) FROM HealthEvents WHERE PetID=$($a.Pet) AND EventType='INVALID_SEVERITY'") -eq 0)

    $relatedAttack = HealthFields $a.Pet Symptom 'FOREIGN_MEDICATION' $today
    $relatedAttack['NewEvent.RelatedMedicationID'] = $b.Med
    $relatedResponse = Request $a.Client POST '/Health?handler=LogEvent' $relatedAttack
    Check 'foreign related medication rejected' ($relatedResponse.Status -eq 404 -and [int](Sql "SELECT COUNT(*) FROM HealthEvents WHERE PetID=$($a.Pet) AND EventType='FOREIGN_MEDICATION'") -eq 0)

    $exactName = 'HEALTH_EXACT_' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $start = $today.AddHours(6)
    $medForm = @{
        NewMedPetID=$a.Pet; NewMedName=$exactName; NewMedDosage='10 mg'
        NewMedFrequencyType='Daily'; NewMedFrequencyInterval='1'
        NewMedTimingDoesNotMatter='false'; NewMedStartDate=$today.ToString('yyyy-MM-dd')
        NewMedStartTime=$start.ToString('HH:mm'); NewMedForever='false'
        NewMedEndDate=$today.AddDays(4).ToString('yyyy-MM-dd'); NewMedNotes='adherence test'
    }
    Ok (Request $a.Client POST '/Medications?handler=AddMedication' $medForm)
    $exactMed = [int](Sql "SELECT medID FROM Medications WHERE petID=$($a.Pet) AND medicationName='$exactName'")
    $originalNext = [DateTime](Sql "SELECT MIN(scheduleDate) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate>'$($start.ToString('s'))'")
    $takenLateAt = $start.AddHours(2)
    $takenResponse = Request $a.Client POST '/Medications?handler=ConfirmSchedule' @{medID=$exactMed;logDate=$start.ToString('s');confirmedAt=$takenLateAt.ToString('s');utcOffsetMinutes=0;notes='admin note'}
    $takenRow = [string](Sql "SELECT CONCAT(DoseStatus,'|',RecordedByUserID,'|',CONVERT(varchar(19),AdministeredAtUtc,126),'|',AdministrationNotes) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate='$($start.ToString('s'))'")
    Check 'taken-late status, UTC time, attribution and notes' ($takenResponse.Status -eq 302 -and $takenRow -eq "Taken late|$($a.User)|$($takenLateAt.ToString('s'))|admin note") $takenRow
    $shiftedNext = [DateTime](Sql "SELECT MIN(scheduleDate) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate>'$($start.ToString('s'))'")
    Check 'existing exact-time schedule shift preserved' ($shiftedNext -eq $originalNext.AddHours(2)) "$originalNext -> $shiftedNext"

    $skipResponse = Request $a.Client POST '/Medications?handler=RecordDose' @{medID=$exactMed;logDate=$shiftedNext.ToString('s');status='Skipped';utcOffsetMinutes=0;reason='Pet refused';notes='retry later'}
    $skipRow = [string](Sql "SELECT CONCAT(DoseStatus,'|',StatusReason,'|',RecordedByUserID,'|',isConfirmed) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate='$($shiftedNext.ToString('s'))'")
    Check 'skipped dose distinct and attributed' ($skipResponse.Status -eq 302 -and $skipRow -eq "Skipped|Pet refused|$($a.User)|0") $skipRow

    $missingReason = Request $a.Client POST '/Medications?handler=RecordDose' @{medID=$exactMed;logDate=$originalNext.AddDays(1).AddHours(2).ToString('s');status='Missed';utcOffsetMinutes=0}
    Check 'skip/miss reason required' ($missingReason.Status -eq 200)

    $missedDate = [DateTime](Sql "SELECT MIN(scheduleDate) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate>'$($shiftedNext.ToString('s'))'")
    $missedResponse = Request $a.Client POST '/Medications?handler=RecordDose' @{medID=$exactMed;logDate=$missedDate.ToString('s');status='Missed';utcOffsetMinutes=0;reason='Not noticed until next day'}
    $missedRow = [string](Sql "SELECT CONCAT(DoseStatus,'|',StatusReason,'|',RecordedByUserID) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate='$($missedDate.ToString('s'))'")
    Check 'manual missed dose distinct and attributed' ($missedResponse.Status -eq 302 -and $missedRow -eq "Missed|Not noticed until next day|$($a.User)") $missedRow
    $undoMissed = Request $a.Client POST '/Medications?handler=UnconfirmSchedule' @{medID=$exactMed;logDate=$missedDate.ToString('s')}
    $undoRow = [string](Sql "SELECT CONCAT(DoseStatus,'|',ISNULL(RecordedByUserID,0)) FROM MedicationSchedule WHERE medID=$exactMed AND scheduleDate='$($missedDate.ToString('s'))'")
    Check 'undo restores due state' ($undoMissed.Status -eq 302 -and $undoRow -eq 'Due|0') $undoRow

    $dateOnlyLog = [DateTime](Sql "SELECT TOP (1) scheduleDate FROM MedicationSchedule WHERE medID=$($a.Med) ORDER BY ABS(DATEDIFF(DAY,scheduleDate,GETDATE()))")
    $dateOnlyTaken = Request $a.Client POST '/Medications?handler=ConfirmSchedule' @{medID=$a.Med;logDate=$dateOnlyLog.ToString('s');confirmedAt=$dateOnlyLog.AddHours(20).ToString('s');utcOffsetMinutes=0}
    $dateOnlyStatus = [string](Sql "SELECT TOP (1) DoseStatus FROM MedicationSchedule WHERE medID=$($a.Med) AND CONVERT(date,scheduleDate)='$($dateOnlyLog.ToString('yyyy-MM-dd'))'")
    Check 'timing-does-not-matter remains taken, never late' ($dateOnlyTaken.Status -eq 302 -and $dateOnlyStatus -eq 'Taken') $dateOnlyStatus

    $foreignDoseBefore = [string](Sql "SELECT CONCAT(DoseStatus,'|',ISNULL(RecordedByUserID,0)) FROM MedicationSchedule WHERE medID=$($b.Med) AND CONVERT(date,scheduleDate)=CONVERT(date,GETDATE())")
    $foreignDose = Request $a.Client POST '/Medications?handler=RecordDose' @{medID=$b.Med;logDate=$today.ToString('s');status='Skipped';utcOffsetMinutes=0;reason='ATTACK'}
    $foreignDoseAfter = [string](Sql "SELECT CONCAT(DoseStatus,'|',ISNULL(RecordedByUserID,0)) FROM MedicationSchedule WHERE medID=$($b.Med) AND CONVERT(date,scheduleDate)=CONVERT(date,GETDATE())")
    Check 'foreign dose outcome denied' ($foreignDose.Status -eq 404 -and $foreignDoseBefore -eq $foreignDoseAfter)

    $reportVisit = VisitFields NewVisit $a.Pet 'HEALTH_VET_REPORT'
    $reportVisit.'NewVisit.VisitDate' = $today.ToString('yyyy-MM-dd')
    Ok (Request $a.Client POST '/VetVisits?handler=AddVisit' $reportVisit)
    $reportVisitID = [int](Sql "SELECT MAX(VetVisitID) FROM VetVisits WHERE PetID=$($a.Pet) AND VisitReason='HEALTH_VET_REPORT'")
    Check 'new vet visit attributed to recorder' ([int](Sql "SELECT CreatedByUserID FROM VetVisits WHERE VetVisitID=$reportVisitID") -eq $a.User)

    $report30 = Request $a.Client POST '/Reports?handler=Generate' @{SelectedPetID=$a.Pet;Period='30';QuestionsForVet='HEALTH_QUESTION'}
    Check '30-day report boundaries' ($report30.Status -eq 200 -and $report30.Text.Contains('HEALTH_A_DAY_29') -and !$report30.Text.Contains('HEALTH_A_DAY_30'))
    Check 'report includes questions and adherence without routine tasks' ($report30.Text.Contains('HEALTH_QUESTION') -and $report30.Text.Contains('Medication adherence') -and !$report30.Text.Contains('HEALTH_ROUTINE_ONLY') -and !$report30.Text.Contains('Daily care signals'))
    $report90 = Request $a.Client POST '/Reports?handler=Generate' @{SelectedPetID=$a.Pet;Period='90'}
    Check '90-day report boundaries' ($report90.Status -eq 200 -and $report90.Text.Contains('HEALTH_A_DAY_89') -and !$report90.Text.Contains('HEALTH_A_DAY_90'))
    Check '90-day report includes health, medication and vet sources' ($report90.Text.Contains('HEALTH_VET_REPORT') -and $report90.Text.Contains($exactName) -and !$report90.Text.Contains('HEALTH_ROUTINE_ONLY'))
    $foreignReport = Request $a.Client POST '/Reports?handler=Generate' @{SelectedPetID=$b.Pet;Period='30'}
    Check 'foreign report denied' ($foreignReport.Status -eq 404) "status=$($foreignReport.Status)"

    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$PSScriptRoot/health-mvp-audit.json"
    Write-Host "Health MVP checks: $(@($checks | Where-Object Passed).Count)/$($checks.Count) passed."
}
finally {
    CleanupFixture $a
    CleanupFixture $b
}
