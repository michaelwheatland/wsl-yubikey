param(
  [ValidateSet("attach","detach")]
  [string]$Action = "attach",

  [string[]]$Aliases = @(
    "yubi",
    "smartcard",
    "ccid",
    "fido",
    "security key"
  ),

  [string]$FallbackBusId = "6-4",

  [ValidateSet("info","debug")]
  [string]$LogLevel = "info",

  [switch]$Unattended
)

$ErrorActionPreference = "Stop"

function Log($msg) { Write-Host $msg }
function Debug($msg) { if ($LogLevel -eq "debug") { Write-Host "[debug] $msg" } }

# Run usbipd command safely, capture output and exit code without throwing
function Run-Usbipd {
  param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$UsbipdArgs
  )

  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  Debug ("Running: usbipd " + ($UsbipdArgs -join ' '))
  try {
    $out = & usbipd @UsbipdArgs 2>&1
    $ec = $LASTEXITCODE
    return @{ Out = $out; ExitCode = $ec }
  }
  finally {
    $ErrorActionPreference = $saved
  }
}

try {
  Debug "Running: usbipd list"
  $listRes = Run-Usbipd 'list'
  $raw = $listRes.Out
  Debug ($raw | Out-String)

  # Normalize and trim lines to make parsing robust against leading spaces
  $lines = $raw | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne "" }

  Debug "Trimmed lines:"
  Debug ($lines | Out-String)

  # Only rows that look like devices (BUSID at start like 1-2)
  $rows = $lines | Where-Object { $_ -match '^\d+-\d+\b' }
  Debug "Parsed device rows:"
  Debug ($rows | Out-String)

  # Build a single regex from aliases (lowercased) for reliable case-insensitive matching
  $aliasRegexLower = ($Aliases | ForEach-Object { [regex]::Escape($_.ToLower()) }) -join "|"
  Debug "Alias regex (lower): $aliasRegexLower"

  # Parse rows into structured objects
  $devices = @()
  foreach ($r in $rows) {
    $bus = $null; $state = $null; $desc = $null
    if ($r -match '^(\d+-\d+)\b') { $bus = $Matches[1] }
    # try to capture a state token like 'Shared' or 'Not shared' at the end
    if ($r -match '\b(Not shared|Not Shared|Shared|shared|Attached|attached)\b') { $state = $Matches[1] }
    else { $state = "Unknown" }
    # description is the rest of the line after the BUSID
    $desc = $r -replace '^\d+-\d+\s*',''
    $devices += [PSCustomObject]@{ Line = $r; BusId = $bus; State = $state; Desc = $desc }
  }

  if ($devices.Count -eq 0) {
    Debug "No devices were listed by usbipd."
  } else {
    Log "Found devices:"
    foreach ($d in $devices) {
      Log "  BUSID: $($d.BusId)  STATE: $($d.State)  DESC: $($d.Desc)"
    }
  }

  Debug "Alias match evaluation:"
  foreach ($d in $devices) {
    Debug ("  $($d.BusId): " + [bool]($d.Desc.ToLower() -match $aliasRegexLower) + "  [$($d.Desc)]")
  }

  $matchingDevices = @($devices | Where-Object { $_.Desc.ToLower() -match $aliasRegexLower })
  $mdType = if ($matchingDevices) { $matchingDevices.GetType().FullName } else { "null" }
  $mdCount = if ($matchingDevices) { $matchingDevices.Count } else { 0 }
  Debug ("matchingDevices type: " + $mdType)
  Debug ("matchingDevices count: " + $mdCount)
  Debug "Matching devices for aliases:"
  if ($matchingDevices -and $matchingDevices.Count -gt 0) {
    foreach ($m in $matchingDevices) { Debug "  $($m.Line)" }
  } else {
    Debug "  none"
  }

  if ($matchingDevices -and $matchingDevices.Count -gt 0) {
    if ($matchingDevices.Count -gt 1) {
      Log "Multiple matching devices found; using first (listing all):"
      foreach ($m in $matchingDevices) { Log "  $($m.Line)" }
    } else {
      Debug "Matched device: $($matchingDevices[0].Line)"
    }

    $chosen = $matchingDevices[0]
    if (-not $chosen.BusId) { throw "Could not extract BUSID from line: $($chosen.Line)" }
    $busid = $chosen.BusId

    # If the device is not shared, try to bind (requires admin)
    if ($chosen.State -match '(?i)not') {
      Log "Device $busid state is '$($chosen.State)'; attempting to enable sharing (usbipd bind --busid=$busid)."
      $bindRes = Run-Usbipd 'bind' "--busid=$busid"
      Log ($bindRes.Out | Out-String)
      if ($bindRes.ExitCode -ne 0) { Log "WARNING: usbipd bind exited with code $($bindRes.ExitCode). You may need Administrator privileges." }
      else { Debug "Bind command completed with exit code 0." }
    }

  }
  else {
    Log "No device matched aliases [$($Aliases -join ', ')]."
    Log "Falling back to BUSID $FallbackBusId"
    $busid = $FallbackBusId
  }

  if (-not $busid) { throw "No BUSID available to operate on." }

  if ($Action -eq "attach") {
    Log "Attaching BUSID $busid to WSL..."
    $res = Run-Usbipd 'attach' '--wsl' '--busid' $busid
    $outText = $res.Out | Out-String
    Log $outText
    if ($res.ExitCode -ne 0) {
      if ($outText -match 'already attached') {
        Log "Device $busid is already attached; continuing."
      } else {
        Log "WARNING: usbipd attach exited with code $($res.ExitCode)."
      }
    }
  }
  else {
    Log "Detaching BUSID $busid from WSL..."
    $res = Run-Usbipd 'detach' "--busid=$busid"
    Log ($res.Out | Out-String)
    if ($res.ExitCode -ne 0) { Log "WARNING: usbipd detach exited with code $($res.ExitCode)." }
  }

  # Re-check status to ensure it's attached/shared (with a few retries because the list output can lag)
  Debug "Re-checking usbipd list to verify device state..."
  $maxTries = 3
  $delaySeconds = 1
  $foundAfter = @()
  $stateOk = $false

  for ($i = 1; $i -le $maxTries; $i++) {
    $raw2 = usbipd list 2>&1
    $lines2 = $raw2 | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ -ne "" }
    $rows2 = $lines2 | Where-Object { $_ -match '^\d+-\d+\b' }
    $foundAfter = @($rows2 | Where-Object { $_ -match "^$busid\b" })

    if ($foundAfter.Count -gt 0) {
      $firstLine = $foundAfter[0]
      $isAttached = $firstLine -match '(?i)attached'
      if ($Action -eq "attach") {
        $stateOk = $isAttached
      } else {
        $stateOk = -not $isAttached
      }
      if ($stateOk) { break }
    }

    if ($i -lt $maxTries) {
      Debug "Verification attempt $i did not confirm expected state; sleeping $delaySeconds second(s) then retrying..."
      Start-Sleep -Seconds $delaySeconds
    }
  }

  if ($foundAfter.Count -gt 0) {
    Log "Verification: device line after action:"
    foreach ($l in $foundAfter) { Log "  $l" }
  } else {
    Log "WARNING: Device $busid was not found in usbipd list after the action. It may not be attached."
  }

  if (-not $stateOk) {
    if ($Action -eq "attach") {
      Log "WARNING: Device $busid did not show as Attached after $maxTries check(s)."
    } else {
      Log "WARNING: Device $busid still shows as Attached after $maxTries check(s)."
    }
  }

  Log "Done."
}
catch {
  Write-Host "ERROR: $($_.Exception.Message)"
}
finally {
  if (-not $Unattended) {
    Write-Host ""
    Write-Host "Press Enter to close..."
    [void][System.Console]::ReadLine()
  }
}

