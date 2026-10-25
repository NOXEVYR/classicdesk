param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
. (Join-Path $PSScriptRoot 'Explorer-StartupPrerequisite.ps1')
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$Condition){$results.Add([pscustomobject]@{Name=$Name;Passed=$Condition});if(-not $Condition){throw "Failed: $Name"}}
$unicodePath='C:\'+[char]0x4e2a+[char]0x4eba+[char]0x5c0f+[char]0x7a0b+[char]0x5e8f+'\classicdesk'
$receiptPath=Join-Path $OutputDirectory 'utf8-no-bom-receipt.json'
$receiptJson=@{OwnerSid='fixture-owner';Source=@{Package=@{Root=$unicodePath}};TargetProfile=@{ClassicRibbon=$true}} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($receiptPath,$receiptJson,[Text.UTF8Encoding]::new($false))
$decoded=Read-ExplorerInstallationReceipt $receiptPath
Check 'BOM-less UTF8 receipt preserves Chinese path' ($decoded.Source.Package.Root -ceq $unicodePath)
Check 'UTF8 receipt preserves target profile' ($decoded.TargetProfile.ClassicRibbon -eq $true)
$ribbon=[pscustomobject]@{ClassicRibbon=$true}
$facts=[pscustomobject]@{DefinitionPresent=$true;DefinitionValid=$true;OwnerHiveLoaded=$true;Kind='DWord';Value=0}
Check 'known option off accepts Win10 preparation' (Test-ExplorerPrelaunchFacts $ribbon $facts)
foreach($field in @('DefinitionValid','OwnerHiveLoaded','Kind','Value')) {
    $bad=$facts | ConvertTo-Json | ConvertFrom-Json
    switch($field){'Kind' {$bad.Kind='String'} 'Value' {$bad.Value=1} default {$bad.$field=$false}}
    Check "reject changed $field" (-not (Test-ExplorerPrelaunchFacts $ribbon $bad))
}
$missing=$facts | ConvertTo-Json | ConvertFrom-Json;$missing.Kind=$null;$missing.Value=$null
Check 'absence means Windows default on, not ready' (-not (Test-ExplorerPrelaunchFacts $ribbon $missing))
$old=[pscustomobject]@{DefinitionPresent=$false}
Check 'old OS without option remains supported' (Test-ExplorerPrelaunchFacts $ribbon $old)
Check 'Win11 plan has no Ribbon prerequisite' (Test-ExplorerPrelaunchFacts ([pscustomobject]@{ClassicRibbon=$false}) $missing)
Check 'legacy default profile requires Ribbon preparation' (-not (Test-ExplorerPrelaunchFacts ([pscustomobject]@{}) $missing))
$script:ownerRead=$null
function Get-ExplorerPrelaunchFacts([string]$OwnerSid){$script:ownerRead=$OwnerSid;return $facts}
Assert-ExplorerStartupPrerequisite ([pscustomobject]@{OwnerSid='fixture-owner';TargetProfile=$ribbon;Source=[pscustomobject]@{AppliedProfile=[pscustomobject]@{ClassicRibbon=$false}}})
Check 'target profile takes precedence and checks installation owner' ($script:ownerRead -ceq 'fixture-owner')
$script:ownerRead=$null
Assert-ExplorerStartupPrerequisite ([pscustomobject]@{OwnerSid='fixture-owner';TargetProfile=[pscustomobject]@{ClassicRibbon=$false};Source=[pscustomobject]@{AppliedProfile=$ribbon}})
Check 'native profile does not access registry' ($null -eq $script:ownerRead)
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Encoding utf8
[pscustomobject]@{Passed=$results.Count;Failed=0;RegistryReads=0;RegistryWrites=0;StartupChanges=0}
