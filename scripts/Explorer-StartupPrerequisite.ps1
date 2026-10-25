# Read-only checks in the installation owner's loaded hive. Never use the
# administrator's HKCU, load an offline hive, or write settings from LocalSystem.
function Read-ExplorerInstallationReceipt([string]$Path) {
    # Receipts are UTF-8; Windows PowerShell 5.1 otherwise assumes the ANSI code page.
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Test-ExplorerPrelaunchFacts($Profile, $Facts) {
    $ribbon=($Profile.PSObject.Properties.Name -notcontains 'ClassicRibbon') -or $Profile.ClassicRibbon -eq $true
    if(-not $ribbon){return $true}
    if(-not $Facts.DefinitionPresent){return $true}
    return $Facts.DefinitionValid -eq $true -and $Facts.OwnerHiveLoaded -eq $true -and $Facts.Kind -ceq 'DWord' -and $Facts.Value -eq 0
}
function Get-ExplorerPrelaunchFacts([string]$OwnerSid) {
    if($OwnerSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$'){throw 'Invalid installation owner SID'}
    $machine=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    $users=$null;$definition=$null;$owner=$null;$advanced=$null
    try {
        $definition=$machine.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Folder\ShouldPrelaunchFileExplorer')
        if(-not $definition){return [pscustomobject]@{DefinitionPresent=$false;DefinitionValid=$false;OwnerHiveLoaded=$false;Kind=$null;Value=$null}}
        $regPath='Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
        $valid=$definition.GetValue('HKeyRoot') -eq -2147483647 -and $definition.GetValue('RegPath') -ieq $regPath -and
            $definition.GetValue('ValueName') -ceq 'ShouldPrelaunchFileExplorer' -and $definition.GetValue('Type') -ceq 'checkbox' -and
            $definition.GetValue('CheckedValue') -eq 1 -and $definition.GetValue('UncheckedValue') -eq 0 -and $definition.GetValue('DefaultValue') -eq 1
        $users=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users,[Microsoft.Win32.RegistryView]::Registry64)
        $owner=$users.OpenSubKey($OwnerSid);$kind=$null;$value=$null
        if($owner){$advanced=$owner.OpenSubKey($regPath)}
        if($advanced -and $advanced.GetValueNames() -contains 'ShouldPrelaunchFileExplorer') {
            $kind=$advanced.GetValueKind('ShouldPrelaunchFileExplorer').ToString();$value=$advanced.GetValue('ShouldPrelaunchFileExplorer')
        }
        return [pscustomobject]@{DefinitionPresent=$true;DefinitionValid=$valid;OwnerHiveLoaded=($null -ne $owner);Kind=$kind;Value=$value}
    } finally {foreach($key in @($advanced,$owner,$users,$definition,$machine)){if($key){$key.Dispose()}}}
}
function Assert-ExplorerStartupPrerequisite($Receipt) {
    $profile=if($Receipt.TargetProfile){$Receipt.TargetProfile}else{$Receipt.Source.AppliedProfile}
    if(-not $profile){throw 'Installation profile missing'}
    if(($profile.PSObject.Properties.Name -contains 'ClassicRibbon') -and $profile.ClassicRibbon -eq $false){return}
    $facts=Get-ExplorerPrelaunchFacts $Receipt.OwnerSid
    if(-not (Test-ExplorerPrelaunchFacts $profile $facts)) {
        throw 'Win10 Explorer prelaunch prerequisite not ready. In the installation owner account run ClassicDesk --explorer-prelaunch prepare-ribbon, then inspect again. No startup change was made.'
    }
}
