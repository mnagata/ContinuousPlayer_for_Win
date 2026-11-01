# Shared by fixture-based migration UI batches. Requires their checked UI helper.
$script:registeredFixtureName=$null
$script:testFixtureWasRegistered=$false
function Register-TestFixture([string]$Path){
    $absolute=[IO.Path]::GetFullPath($Path)
    $toolsRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.tools'))+[IO.Path]::DirectorySeparatorChar
    if(!$absolute.StartsWith($toolsRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture registration must remain inside this repository .tools directory.'}
    UI @('wait-for','RegisterFolder','-t','5000') | Out-Null
    UI @('invoke','RegisterFolder') | Out-Null
    UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','8000') | Out-Null
    $script:registeredFixtureName=Split-Path -Leaf $absolute;$script:testFixtureWasRegistered=$true
    UI @('wait-for',$script:registeredFixtureName,'-t','5000') | Out-Null
    UI @('click',$script:registeredFixtureName) | Out-Null
    UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','8000') | Out-Null
}
function Get-TestRegistrationNodes($items){foreach($item in $items){if($null -ne $item){$item;Get-TestRegistrationNodes $item.children}}}
function Remove-TestFixtureRegistration{
    if(!$script:registeredFixtureName){return}
    $tree=UI @('inspect','--json','-d','12') | ConvertFrom-Json
    $items=@(Get-TestRegistrationNodes @($tree.windows | ForEach-Object {$_.elements}))
    if($items.automationId -contains 'MediaInfoText' -or $items.automationId -contains 'PrimaryButton'){
        UI @('invoke','CloseButton') | Out-Null
        UI @('wait-for','CloseButton','--gone','-t','5000') | Out-Null
    }
    foreach($n in 1..2){
        $tree=UI @('inspect','--json','-d','12') | ConvertFrom-Json
        $items=@(Get-TestRegistrationNodes @($tree.windows | ForEach-Object {$_.elements}))
        if($items.automationId -contains 'BrowserCancel'){UI @('invoke','BrowserCancel') | Out-Null}
    }
    UI @('send-keys','h','--via','send-input') | Out-Null
    UI @('wait-for','ChooseFolder','-p','IsEnabled','--value','True','-t','5000') | Out-Null
    UI @('invoke','ChooseFolder') | Out-Null
    UI @('wait-for','BrowserRefresh','-p','IsEnabled','--value','True','-t','8000') | Out-Null
    $tree=UI @('inspect','--json','-d','12') | ConvertFrom-Json
    $items=@(Get-TestRegistrationNodes @($tree.windows | ForEach-Object {$_.elements}))
    $matches=@($items | Where-Object {$_.type -eq 'ListItem' -and $_.name.StartsWith($script:registeredFixtureName+'。')})
    if($matches.Count -ne 1){throw 'Expected exactly one owned fixture registration during cleanup.'}
    UI @('invoke',$matches[0].selector,'--action','select') | Out-Null
    UI @('send-keys','delete','--via','send-input') | Out-Null
    UI @('wait-for','PrimaryButton','-t','5000') | Out-Null;UI @('invoke','PrimaryButton') | Out-Null
    UI @('wait-for',$script:registeredFixtureName,'--gone','-t','5000') | Out-Null
    $script:registeredFixtureName=$null
}
