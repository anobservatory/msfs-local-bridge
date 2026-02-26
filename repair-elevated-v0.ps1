param(
  [ValidateSet("ShowFirewall39000", "OpenFirewall39000", "RemoveFirewall39000", "ShowFirewall39002", "OpenFirewall39002", "RemoveFirewall39002")]
  [string]$Action = "ShowFirewall39000",
  [int]$Port = 39000,
  [switch]$Force
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
  try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  }
  catch {
    return $false
  }
}

function Confirm-RepairAction {
  param(
    [string]$Title,
    [string]$WhatChanges
  )

  if ($Force) {
    return $true
  }

  Write-Host ""
  Write-Host "Repair action: $Title" -ForegroundColor Yellow
  Write-Host "This action requires administrator privileges." -ForegroundColor Yellow
  Write-Host "Changes:" -ForegroundColor Yellow
  Write-Host "  $WhatChanges"
  Write-Host ""
  $answer = Read-Host "Continue? [y/N]"
  return $answer -match '^(y|yes)$'
}

function Ensure-AdminOrExit {
  param(
    [string]$RequestedAction
  )

  if (Test-IsAdministrator) {
    return
  }

  Write-Host "[FAIL] This action requires an elevated PowerShell session." -ForegroundColor Red
  Write-Host "Open PowerShell as Administrator and run:" -ForegroundColor Yellow
  Write-Host "  .\repair-elevated-v0.ps1 -Action $RequestedAction -Port $Port"
  exit 1
}

function Get-RuleName {
  param(
    [int]$RulePort
  )
  return "AO MSFS Bridge TCP $RulePort (Private)"
}

function Show-FirewallRule {
  param(
    [string]$RuleName
  )

  $rules = @(Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)
  if ($rules.Count -eq 0) {
    Write-Host "[INFO] No firewall rule found: $RuleName"
    return
  }

  Write-Host "[PASS] Firewall rule found: $RuleName" -ForegroundColor Green
  foreach ($rule in $rules) {
    Write-Host "  Enabled: $($rule.Enabled)"
    Write-Host "  Direction: $($rule.Direction)"
    Write-Host "  Action: $($rule.Action)"
    Write-Host "  Profile: $($rule.Profile)"
  }
}

function Add-OrUpdateFirewallRule {
  param(
    [string]$RuleName,
    [int]$RulePort
  )

  $existing = @(Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)
  if ($existing.Count -gt 0) {
    Remove-NetFirewallRule -DisplayName $RuleName -ErrorAction Stop
  }

  New-NetFirewallRule `
    -DisplayName $RuleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $RulePort `
    -Profile Private `
    -Enabled True `
    -ErrorAction Stop | Out-Null

  Write-Host "[PASS] Added/updated firewall rule: $RuleName" -ForegroundColor Green
}

function Remove-FirewallRule {
  param(
    [string]$RuleName
  )

  $existing = @(Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue)
  if ($existing.Count -eq 0) {
    Write-Host "[INFO] Firewall rule not found: $RuleName"
    return
  }

  Remove-NetFirewallRule -DisplayName $RuleName -ErrorAction Stop
  Write-Host "[PASS] Removed firewall rule: $RuleName" -ForegroundColor Green
}

$ruleName = Get-RuleName -RulePort $Port

Write-Host "MSFS Local Bridge Elevated Repair Tool" -ForegroundColor Cyan
Write-Host "Action: $Action"
Write-Host "Port:   $Port"

switch ($Action) {
  "ShowFirewall39000" {
    Show-FirewallRule -RuleName $ruleName
    break
  }

  "ShowFirewall39002" {
    Show-FirewallRule -RuleName $ruleName
    break
  }

  "OpenFirewall39000" {
    Ensure-AdminOrExit -RequestedAction $Action
    $ok = Confirm-RepairAction `
      -Title "Open inbound TCP $Port for Private network" `
      -WhatChanges "Adds/updates Windows Firewall inbound allow rule '$ruleName'."
    if (-not $ok) {
      Write-Host "[CANCELLED] No changes were applied."
      exit 0
    }
    Add-OrUpdateFirewallRule -RuleName $ruleName -RulePort $Port
    break
  }

  "OpenFirewall39002" {
    Ensure-AdminOrExit -RequestedAction $Action
    $ok = Confirm-RepairAction `
      -Title "Open inbound TCP $Port for Private network" `
      -WhatChanges "Adds/updates Windows Firewall inbound allow rule '$ruleName'."
    if (-not $ok) {
      Write-Host "[CANCELLED] No changes were applied."
      exit 0
    }
    Add-OrUpdateFirewallRule -RuleName $ruleName -RulePort $Port
    break
  }

  "RemoveFirewall39000" {
    Ensure-AdminOrExit -RequestedAction $Action
    $ok = Confirm-RepairAction `
      -Title "Remove inbound TCP $Port firewall rule" `
      -WhatChanges "Removes Windows Firewall rule '$ruleName'."
    if (-not $ok) {
      Write-Host "[CANCELLED] No changes were applied."
      exit 0
    }
    Remove-FirewallRule -RuleName $ruleName
    break
  }

  "RemoveFirewall39002" {
    Ensure-AdminOrExit -RequestedAction $Action
    $ok = Confirm-RepairAction `
      -Title "Remove inbound TCP $Port firewall rule" `
      -WhatChanges "Removes Windows Firewall rule '$ruleName'."
    if (-not $ok) {
      Write-Host "[CANCELLED] No changes were applied."
      exit 0
    }
    Remove-FirewallRule -RuleName $ruleName
    break
  }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
