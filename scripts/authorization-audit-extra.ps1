$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/authorization-audit.ps1" -Phase extra -FunctionsOnly
$meta = Get-Content "$PSScriptRoot/audit-final-fixtures.json" | ConvertFrom-Json -AsHashtable
$a=$meta.A; $b=$meta.B
$a.Client=Client
$password = Sql "SELECT pass FROM Users WHERE userID=$($a.User) AND userName='$($a.Name)'"
Ok (Request $a.Client POST /Login @{Username=$a.Name;Password=$password})
$checks=[Collections.Generic.List[object]]::new()
function Check($name, $r, $expected, $before) {
    $pass=$r.Status -eq $expected -and (Snapshot $b.User) -ceq $before -and !$r.Text.Contains($b.Name) -and !$r.Text.Contains('AUDIT_PRIVATE_DOCUMENT')
    $checks.Add(@{Test=$name;Status=$r.Status;Pass=$pass})
    $checks | ConvertTo-Json -Depth 4 | Set-Content "$PSScriptRoot/audit-extra-controls.json"
    if (!$pass) { throw "Failed: $name" }
}
$before=Snapshot $b.User
$anonymous=Client
$anonymous.DefaultRequestHeaders.Add('Cookie', "userID=$($b.User); userName=forged")
Check 'GET /Home with forged identity cookies, no session' (Request $anonymous GET /Home) 302 $before
Check 'GET foreign photo with forged identity cookies, no session' (Request $anonymous GET $b.Photo) 401 $before
Check 'POST /VetVisits?handler=PreviewDocument with no session' (Request $anonymous POST '/VetVisits?handler=PreviewDocument' @{documentID=$b.Doc}) 401 $before

# A session is authoritative even if the display/legacy identity cookie is forged.
$a.Client.DefaultRequestHeaders.Add('Cookie', "userID=$($b.User)")
Attack '/Home?handler=DeleteTask' @{taskID=$b.Task;userID=$b.User} POST 'forged userID cookie and body'
Attack '/Medications?handler=ConfirmSchedule' @{medID=$b.Med;userID=$b.User;logDate=[DateTime]::Today.ToString('s');confirmedAt=[DateTime]::Now.ToString('s')} POST 'forged userID cookie and body'

$f=PetFields NewPet AUDIT_OWNER_OVERPOST; $f.UserID=$b.User; $f.NewPetID=$b.Pet
$r=Request $a.Client POST '/Home?handler=AddPet' $f
Check 'POST /Home?handler=AddPet forged UserID and NewPetID creates only an A pet' $r 302 $before
$pet=[int](Sql "SELECT MAX(petID) FROM Pets WHERE userID=$($a.User) AND name='AUDIT_OWNER_OVERPOST'")
if (!$pet) { throw 'Overposting did not create the pet under A' }
Ok (Request $a.Client POST '/Home?handler=DeletePet' @{EditPetID=$pet})

$fields=[Collections.Generic.Dictionary[string,string]]::new(); $fields['taskID']=[string]$b.Task
$r=$a.Client.PostAsync("$BaseUrl/Home?handler=DeleteTask", [Net.Http.FormUrlEncodedContent]::new($fields)).GetAwaiter().GetResult()
Check 'POST /Home?handler=DeleteTask without antiforgery token' @{Status=[int]$r.StatusCode;Text=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()} 400 $before
$html=$a.Client.GetStringAsync("$BaseUrl/Home").GetAwaiter().GetResult()
$token=[regex]::Match($html,'name="__RequestVerificationToken" type="hidden" value="([^"]+)"').Groups[1].Value
$msg=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,"$BaseUrl/Home?handler=DeleteTask&taskID=$($b.Task)")
$msg.Headers.Add('RequestVerificationToken',$token)
$msg.Content=[Net.Http.StringContent]::new('{"taskID":'+$b.Task+'}',[Text.Encoding]::UTF8,'application/json')
$r=$a.Client.SendAsync($msg).GetAwaiter().GetResult()
Check 'JSON POST /Home?handler=DeleteTask, foreign query ID, valid antiforgery header' @{Status=[int]$r.StatusCode;Text=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()} 404 $before
if (@($results | Where-Object { !$_.Pass }).Count) { throw 'Extra attack failed' }
Write-Host "Extra controls: $($checks.Count) passed; cross-account attacks: $($results.Count) passed."
