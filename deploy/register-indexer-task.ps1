# Register run-indexer.bat in Windows Task Scheduler.
# (comments/messages are ASCII on purpose: Windows PowerShell 5.1 code page safe)
#
#   .\register-indexer-task.ps1               Registers both Hourly and Daily Full tasks as SYSTEM (LOCALSYSTEM)
#   .\register-indexer-task.ps1 -DailyAt 02:00  Change daily full scan time
#   .\register-indexer-task.ps1 -HourlyOnly   Register only the Hourly task
#   .\register-indexer-task.ps1 -DailyOnly    Register only the Daily Full task
#   .\register-indexer-task.ps1 -Unregister   Remove both scheduled tasks
#
# Must be run from an elevated (Administrator) PowerShell.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$HourlyTaskName = 'NowFound Indexer (Hourly)',
    [string]$DailyTaskName = 'NowFound Indexer (Daily Full)',
    [string]$DailyAt = '03:00',
    [int]$HourlyIntervalHours = 1,
    [switch]$HourlyOnly,
    [switch]$DailyOnly,
    [string]$TaskName = '',
    [switch]$Unregister,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

function Show-Usage {
    Write-Host @'
Usage: .\register-indexer-task.ps1 [-DailyAt <HH:mm>] [-HourlyIntervalHours <hours>] [-HourlyOnly] [-DailyOnly] [-Unregister]

Registers NowFound indexer tasks in Windows Task Scheduler (SYSTEM / LOCALSYSTEM, highest privileges).
By default, registers 2 tasks:
  1. "NowFound Indexer (Hourly)":     Runs every hour (fast differential scan)
  2. "NowFound Indexer (Daily Full)": Runs daily at 03:00 (--full scan)

Options:
  -DailyAt <HH:mm>            Daily full scan start time (default: 03:00)
  -HourlyIntervalHours <int>  Repetition interval in hours for hourly task (default: 1)
  -HourlyOnly                 Register only the hourly task
  -DailyOnly                  Register only the daily full task
  -TaskName <name>            Specify a single custom task name
  -Unregister                 Remove scheduled tasks
  -h, --help, -help           Show this usage

Examples:
  .\register-indexer-task.ps1
  .\register-indexer-task.ps1 -DailyAt 02:00
  .\register-indexer-task.ps1 -Unregister
'@
}

if ($RemainingArgs) {
    if ($RemainingArgs | Where-Object { $_ -in '-h', '--help', '-help', '-?', '/?' }) {
        Show-Usage
        exit 0
    }

    Write-Host "Unknown argument: $($RemainingArgs -join ' ')"
    Write-Host ''
    Show-Usage
    exit 1
}

$ErrorActionPreference = 'Stop'

$principalCheck = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required. Run this script from an elevated PowerShell.'
}

# Determine which tasks to process
$targetTasks = @()
if ($TaskName) {
    $targetTasks += [pscustomobject]@{ Name = $TaskName; Full = $false }
} else {
    if (-not $DailyOnly) {
        $targetTasks += [pscustomobject]@{ Name = $HourlyTaskName; Full = $false }
    }
    if (-not $HourlyOnly) {
        $targetTasks += [pscustomobject]@{ Name = $DailyTaskName; Full = $true }
    }
}

if ($Unregister) {
    $tasksToRemove = @($targetTasks.Name)
    # Also clean up legacy task name if present
    if (-not $TaskName) {
        $tasksToRemove += 'NowFound Indexer'
    }

    foreach ($name in ($tasksToRemove | Select-Object -Unique)) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            Write-Host "Removed task '$name'."
        } else {
            Write-Host "Task '$name' does not exist."
        }
    }
    return
}

$batPath = Join-Path $PSScriptRoot 'run-indexer.bat'
if (-not (Test-Path -LiteralPath $batPath)) {
    throw "run-indexer.bat not found: $batPath"
}

$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 12)

foreach ($target in $targetTasks) {
    $argsStr = if ($target.Full) { '--full' } else { '' }
    $cmdArgs = if ($argsStr) {
        "/c `"`"$batPath`" $argsStr`""
    } else {
        "/c `"`"$batPath`"`""
    }

    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $cmdArgs -WorkingDirectory $PSScriptRoot

    if ($target.Full) {
        $trigger = New-ScheduledTaskTrigger -Daily -At $DailyAt
        $description = 'NowFound file indexer (daily full scan)'
        $scheduleDesc = "daily at $DailyAt"
    } else {
        # Repeat every N hours indefinitely starting at 00:00
        $trigger = New-ScheduledTaskTrigger -Once -At '00:00' -RepetitionInterval (New-TimeSpan -Hours $HourlyIntervalHours)
        $description = 'NowFound file indexer (hourly fast differential scan)'
        $scheduleDesc = "every $HourlyIntervalHours hour(s)"
    }

    Register-ScheduledTask -TaskName $target.Name -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force `
        -Description $description | Out-Null

    Write-Host "Registered task '$($target.Name)': $scheduleDesc as SYSTEM (LOCALSYSTEM, highest privileges)."
}

Write-Host ''
Write-Host 'Registered task list:'
foreach ($target in $targetTasks) {
    Write-Host "  Start-ScheduledTask -TaskName '$($target.Name)'"
}
