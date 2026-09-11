param([switch]$CurrentFinal)
# Exact disposable accounts created by the 2026-09-06 audit. No prefix deletion.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/authorization-audit.ps1" -FunctionsOnly
$accounts=@{
6='authaudit_32604aae842b_A';7='authaudit_6d117ebdfa38_B'
8='authaudit_2dbbc263a16c_A';9='authaudit_4da9699f69a9_B'
10='authaudit_06616c5012be_A';11='authaudit_5087552d30a2_B'
12='authaudit_9a6c440ffadc_A';13='authaudit_eb44b1ddb6d6_B'
14='authaudit_156f7b6a5876_A';15='authaudit_99516a152c65_B'
16='authaudit_7d3dc7b69e04_A';17='authaudit_bac987744b39_B'
18='authaudit_b36cf73e20c4_A';19='authaudit_073dd8715236_B'
20='authaudit_0a6edc0445eb_A';21='authaudit_5f02ba5b78be_B'
22='authaudit_a51993693af5_A';23='authaudit_b6e0d02538ad_B'
24='authaudit_7be3801fb512_A';25='authaudit_4e952a8bb5b1_B'
}
if ($CurrentFinal) {
    $fixtures=Get-Content "$PSScriptRoot/audit-final-fixtures.json" | ConvertFrom-Json
    $accounts=@{}
    foreach($fixture in @($fixtures.A,$fixtures.B)) {
        if ($fixture.Name -notmatch '^authaudit_[0-9a-f]{12}_[AB]$') { throw 'Not an audit fixture' }
        $accounts[[int]$fixture.User]=[string]$fixture.Name
    }
}
$rows=[Collections.Generic.List[object]]::new()
foreach($id in ($accounts.Keys | Sort-Object)) {
    $name=$accounts[$id]
    $password=Sql "SELECT pass FROM Users WHERE userID=$id AND userName='$name'"
    if (!$password -or $password -is [DBNull]) { continue }
    $client=Client
    Ok (Request $client POST /Login @{Username=$name;Password=$password})
    $pets=Sql "SELECT petID FROM Pets WHERE userID=$id FOR JSON PATH" | ConvertFrom-Json
    foreach($pet in $pets) {
        Ok (Request $client POST '/Home?handler=DeletePet' @{EditPetID=$pet.petID})
        if ((Sql "SELECT COUNT(*) FROM Pets WHERE petID=$($pet.petID)") -ne 0) { throw 'Pet cleanup failed' }
    }
    $remaining=Sql "DELETE FROM Users WHERE userID=$id AND userName='$name' AND NOT EXISTS (SELECT 1 FROM Pets WHERE userID=$id); SELECT COUNT(*) FROM Users WHERE userID=$id AND userName='$name';"
    if ($remaining -ne 0) { throw 'Account cleanup failed' }
    $rows.Add(@{UserID=$id;UserName=$name;Removed=$true;PetsRemoved=@($pets).Count})
    Write-Host "Removed audit account $id and its records."
    $client.Dispose()
}
$rows | ConvertTo-Json | Set-Content "$PSScriptRoot/audit-cleanup.json"
