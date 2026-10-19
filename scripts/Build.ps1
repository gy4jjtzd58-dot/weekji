$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler was not found.' }
$wpf = Join-Path (Split-Path -Parent $compiler) 'WPF'
$outputRoot = Join-Path $projectRoot 'dist\WeekJi'
$applicationFolder = Join-Path $outputRoot 'App'
New-Item -ItemType Directory -Path $applicationFolder -Force | Out-Null
$references = @(
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xaml.dll',
    ('/r:' + (Join-Path $wpf 'WindowsBase.dll')),
    ('/r:' + (Join-Path $wpf 'WindowsFormsIntegration.dll')),
    ('/r:' + (Join-Path $wpf 'PresentationCore.dll')),
    ('/r:' + (Join-Path $wpf 'PresentationFramework.dll'))
)
$iconPath = Join-Path $projectRoot 'assets\weekji.ico'
$branding = @(('/win32icon:' + $iconPath), ('/resource:' + $iconPath + ',ChineseWeeklyPlanner.weekji.ico'))
$source = @('WeekEditor.cs', 'GlassWeekWindow.cs', 'QuickNotesPanel.cs', 'DesktopGlassHost.cs', 'PlannerApplication.cs', 'AppearanceSettings.cs', 'AppearanceSettingsWindow.cs', 'DesktopEnvironmentMonitor.cs', 'AppBrand.cs', 'AssemblyInfoWeekJi.cs') | ForEach-Object { Join-Path (Join-Path $projectRoot 'src') $_ }
$output = Join-Path $applicationFolder 'WeekJi.exe'
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 $references $branding ('/out:' + $output) $source
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$testOutput = Join-Path $applicationFolder 'WeekJi.Test.exe'
try {
    & $compiler /nologo /target:exe /optimize+ /codepage:65001 $references $branding ('/out:' + $testOutput) $source
    if ($LASTEXITCODE -ne 0) { throw 'Verification compilation failed.' }
    & $testOutput --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Content persistence verification failed.' }
} finally {
    if (Test-Path -LiteralPath $testOutput) { Remove-Item -LiteralPath $testOutput -Force }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts\InstallShortcuts.ps1') -Destination (Join-Path $outputRoot 'InstallShortcuts.ps1') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputRoot 'README.md') -Force
Write-Output ('Built: ' + $output)
