param([Alias('StartNotesAtLogin')][switch]$StartAtLogin)
$ErrorActionPreference = 'Stop'
$application = Join-Path $PSScriptRoot 'App\WeekJi.exe'
if (-not (Test-Path -LiteralPath $application)) { throw 'Run this script from the built WeekJi folder.' }
$shortcutShell = New-Object -ComObject WScript.Shell

function New-WeekJiShortcut([string]$folder) {
    $shortcut = $shortcutShell.CreateShortcut((Join-Path $folder 'week记.lnk'))
    try {
        $shortcut.TargetPath = $application
        $shortcut.Arguments = '--week'
        $shortcut.WorkingDirectory = $PSScriptRoot
        $shortcut.IconLocation = $application + ',0'
        $shortcut.Description = 'week记 · 周计划与随心记'
        $shortcut.Save()
    } finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
    }
}

try {
    New-WeekJiShortcut ([Environment]::GetFolderPath('Desktop'))
    if ($StartAtLogin) { New-WeekJiShortcut ([Environment]::GetFolderPath('Startup')) }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell)
}
Write-Output 'week记 desktop shortcut created.'
